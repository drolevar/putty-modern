using PuTTY.Interop;

namespace PuTTY.Interop.Tests;

/// <summary>
/// The engine's event loop: a PuTTY core that never blocks depends on its front end for
/// deferred callbacks, timers and socket events. Socket events are covered by the session
/// tests; these check the other mechanisms and the engine's life cycle.
/// </summary>
[TestClass]
public sealed class EventLoopTests
{
    private static readonly TimeSpan Generous = TimeSpan.FromSeconds(5);

    [TestMethod]
    public void The_engine_starts_and_shuts_down_cleanly_on_its_own_thread()
    {
        using var engine = new EngineThread();   // throws if Initialize or Shutdown fails
        Assert.IsTrue(engine.Run(() => PuttyEngine.Pump(TimeSpan.Zero)) >= 0);
    }

    [TestMethod]
    public void The_engine_can_be_started_again_after_it_was_shut_down()
    {
        using (new EngineThread())
        {
        }

        using var second = new EngineThread();
        Assert.AreEqual(0u, second.Run(() => PuttyEngine.Diagnostics.GetCounters().CallbacksRun));
    }

    [TestMethod]
    public void A_deferred_callback_runs_without_the_host_doing_anything_but_pumping()
    {
        using var engine = new EngineThread();

        engine.Run(PuttyEngine.Diagnostics.QueueCallback);

        Assert.IsTrue(
            EngineThread.WaitUntil(() => engine.Run(() => PuttyEngine.Diagnostics.GetCounters().CallbacksRun) == 1, Generous),
            "the queued callback never ran");
    }

    [TestMethod]
    public void Many_callbacks_queued_at_once_all_run()
    {
        using var engine = new EngineThread();

        engine.Run(() =>
        {
            for (int i = 0; i < 50; i++)
            {
                PuttyEngine.Diagnostics.QueueCallback();
            }
        });

        Assert.IsTrue(
            EngineThread.WaitUntil(() => engine.Run(() => PuttyEngine.Diagnostics.GetCounters().CallbacksRun) == 50, Generous),
            "not every queued callback ran");
    }

    [TestMethod]
    public void A_timer_fires_after_its_delay_and_not_before()
    {
        using var engine = new EngineThread();
        var delay = TimeSpan.FromMilliseconds(150);
        var clock = System.Diagnostics.Stopwatch.StartNew();

        engine.Run(() => PuttyEngine.Diagnostics.ScheduleTimer(delay));

        Thread.Sleep(delay / 3);
        Assert.AreEqual(0u, engine.Run(() => PuttyEngine.Diagnostics.GetCounters().TimersFired), "fired too early");

        Assert.IsTrue(
            EngineThread.WaitUntil(() => engine.Run(() => PuttyEngine.Diagnostics.GetCounters().TimersFired) == 1, Generous),
            "the timer never fired");
        Assert.IsTrue(clock.Elapsed >= delay - TimeSpan.FromMilliseconds(20), $"fired after only {clock.Elapsed}");
    }

    [TestMethod]
    public void A_thread_that_did_not_start_the_engine_is_refused()
    {
        using var engine = new EngineThread();

        var ex = Assert.Throws<InvalidOperationException>(() => PuttyEngine.Pump(TimeSpan.Zero));

        StringAssert.Contains(ex.Message, "thread");
    }

    [TestMethod]
    public void Initialising_twice_on_the_same_thread_is_refused()
    {
        using var engine = new EngineThread();

        var ex = engine.Run(() => Assert.Throws<InvalidOperationException>(PuttyEngine.Initialize));

        StringAssert.Contains(ex.Message, "state");
    }

    [TestMethod]
    public void Using_the_engine_before_it_is_started_is_refused()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => PuttyEngine.Pump(TimeSpan.Zero));

        StringAssert.Contains(ex.Message, "state");
    }
}
