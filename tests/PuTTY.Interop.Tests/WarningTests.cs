using PuTTY.Interop;

namespace PuTTY.Interop.Tests;

/// <summary>
/// PuTTY's core reports some problems outside any session (<c>nonfatal()</c>, the old private key
/// format warning). They must reach the host rather than the debugger.
/// </summary>
[TestClass]
public sealed class WarningTests
{
    /// <summary>Subscribes for the length of a test and keeps what arrives.</summary>
    private sealed class Collector : IDisposable
    {
        private readonly List<EngineWarningEventArgs> received = [];
        private readonly List<int> threads = [];

        public Collector() => PuttyEngine.Warning += OnWarning;

        public EngineWarningEventArgs[] Received
        {
            get { lock (received) { return [.. received]; } }
        }

        public int[] Threads
        {
            get { lock (received) { return [.. threads]; } }
        }

        public void Dispose() => PuttyEngine.Warning -= OnWarning;

        private void OnWarning(object? sender, EngineWarningEventArgs e)
        {
            lock (received)
            {
                received.Add(e);
                threads.Add(Environment.CurrentManagedThreadId);
            }
        }
    }

    [TestMethod]
    public void A_non_fatal_warning_reaches_the_host_on_the_engine_thread()
    {
        using var engine = new EngineThread();
        using var collector = new Collector();

        int engineThread = engine.Run(() =>
        {
            PuttyEngine.Diagnostics.RaiseWarning(EngineWarningKind.NonFatal);
            return Environment.CurrentManagedThreadId;
        });

        EngineWarningEventArgs warning = Assert.ContainsSingle(collector.Received);
        Assert.AreEqual(EngineWarningKind.NonFatal, warning.Kind);
        Assert.AreEqual("diagnostic warning 42", warning.Message);
        Assert.AreEqual(engineThread, collector.Threads[0]);
    }

    [TestMethod]
    public void The_old_key_format_warning_carries_PuTTYs_explanation()
    {
        using var engine = new EngineThread();
        using var collector = new Collector();

        engine.Run(() => PuttyEngine.Diagnostics.RaiseWarning(EngineWarningKind.OldKeyFormat));

        EngineWarningEventArgs warning = Assert.ContainsSingle(collector.Received);
        Assert.AreEqual(EngineWarningKind.OldKeyFormat, warning.Kind);
        StringAssert.StartsWith(warning.Message, "You are loading an SSH-2 private key");
        StringAssert.Contains(warning.Message, "PuTTYgen");
    }

    [TestMethod]
    public void Warnings_still_arrive_after_the_engine_was_restarted()
    {
        using (new EngineThread())
        {
        }

        using var second = new EngineThread();
        using var collector = new Collector();

        second.Run(() => PuttyEngine.Diagnostics.RaiseWarning(EngineWarningKind.NonFatal));

        Assert.HasCount(1, collector.Received);
    }

    [TestMethod]
    public void A_handler_that_throws_does_not_disturb_the_engine()
    {
        using var engine = new EngineThread();
        void Throwing(object? sender, EngineWarningEventArgs e) => throw new InvalidOperationException("handler failure");
        PuttyEngine.Warning += Throwing;
        try
        {
            engine.Run(() => PuttyEngine.Diagnostics.RaiseWarning(EngineWarningKind.NonFatal));
        }
        finally
        {
            PuttyEngine.Warning -= Throwing;
        }

        // The engine is still usable.
        engine.Run(PuttyEngine.Diagnostics.QueueCallback);
        Assert.IsTrue(
            EngineThread.WaitUntil(() => engine.Run(() => PuttyEngine.Diagnostics.GetCounters().CallbacksRun) == 1, TimeSpan.FromSeconds(5)),
            "the engine stopped running callbacks after a handler threw");
    }

    [TestMethod]
    public void An_unknown_warning_kind_is_refused()
    {
        using var engine = new EngineThread();

        engine.Run(() => Assert.Throws<InvalidOperationException>(
            () => PuttyEngine.Diagnostics.RaiseWarning((EngineWarningKind)99)));
    }
}
