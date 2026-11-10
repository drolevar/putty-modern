using System.Text;
using PuTTY.Interop;

namespace PuTTY.Interop.Tests;

/// <summary>
/// Sessions against a real TCP server on the loopback interface. These are the first tests
/// that need everything at once: PuTTY's sockets, the event loop, a Seat and a LogPolicy.
/// </summary>
[TestClass]
public sealed class SessionTests
{
    private static readonly TimeSpan Generous = TimeSpan.FromSeconds(5);

    /// <summary>Collects what a session reports. Handlers run on the engine thread.</summary>
    private sealed class Probe
    {
        private readonly Lock gate = new();
        private readonly List<byte> output = [];
        private readonly List<string> log = [];
        private readonly List<SessionErrorEventArgs> errors = [];
        private int closedCount;

        public byte[] Output { get { lock (gate) { return [.. output]; } } }

        public string[] Log { get { lock (gate) { return [.. log]; } } }

        public SessionErrorEventArgs[] Errors { get { lock (gate) { return [.. errors]; } } }

        public int ClosedCount { get { lock (gate) { return closedCount; } } }

        public void Attach(PuttySession session)
        {
            session.Output += (_, e) => { lock (gate) { output.AddRange(e.Data); } };
            session.Log += (_, e) => { lock (gate) { log.Add(e.Text); } };
            session.Error += (_, e) => { lock (gate) { errors.Add(e); } };
            session.Closed += (_, _) => { lock (gate) { closedCount++; } };
        }
    }

    private static PuttySession Start(EngineThread engine, int port, SessionProtocol protocol, Probe probe) =>
        engine.Run(() =>
        {
            var session = new PuttySession("127.0.0.1", port, protocol);
            probe.Attach(session);
            session.Start();
            return session;
        });

    private static bool Contains(byte[] haystack, byte[] needle) =>
        haystack.AsSpan().IndexOf(needle) >= 0;

    [TestMethod]
    public void A_raw_session_gets_back_what_it_sends()
    {
        using var server = new LoopbackServer(echo: true);
        using var engine = new EngineThread();
        var probe = new Probe();
        var session = Start(engine, server.Port, SessionProtocol.Raw, probe);

        Assert.IsTrue(EngineThread.WaitUntil(() => server.ClientCount == 1, Generous), "never connected");
        engine.Run(() => session.Send("hello"u8));

        Assert.IsTrue(EngineThread.WaitUntil(() => probe.Output.Length >= 5, Generous), "no echo came back");
        Assert.AreEqual("hello", Encoding.ASCII.GetString(probe.Output));

        engine.Run(session.Dispose);
    }

    [TestMethod]
    public void The_event_log_reports_the_connection_from_the_very_first_line()
    {
        using var server = new LoopbackServer();
        using var engine = new EngineThread();
        var probe = new Probe();
        var session = Start(engine, server.Port, SessionProtocol.Raw, probe);

        Assert.IsTrue(
            EngineThread.WaitUntil(() => probe.Log.Any(line => line.Contains("127.0.0.1")), Generous),
            $"no log line mentioned the host; got: {string.Join(" | ", probe.Log)}");

        engine.Run(session.Dispose);
    }

    [TestMethod]
    public void A_server_that_hangs_up_is_reported_exactly_once()
    {
        using var server = new LoopbackServer();
        using var engine = new EngineThread();
        var probe = new Probe();
        var session = Start(engine, server.Port, SessionProtocol.Raw, probe);
        Assert.IsTrue(EngineThread.WaitUntil(() => server.ClientCount == 1, Generous), "never connected");

        server.DropClients();

        Assert.IsTrue(EngineThread.WaitUntil(() => probe.ClosedCount >= 1, Generous), "the close was never reported");
        Thread.Sleep(300);          // PuTTY may notify more than once; the host must hear it once
        Assert.AreEqual(1, probe.ClosedCount);
        Assert.IsFalse(engine.Run(() => session.IsConnected));

        engine.Run(session.Dispose);
    }

    [TestMethod]
    public void A_refused_connection_is_reported_as_a_fatal_error_and_a_close()
    {
        using var engine = new EngineThread();
        var probe = new Probe();

        var session = Start(engine, LoopbackServer.UnusedPort(), SessionProtocol.Raw, probe);

        Assert.IsTrue(
            EngineThread.WaitUntil(() => probe.Errors.Any(e => e.IsFatal), Generous),
            "no fatal error was reported");
        Assert.IsTrue(EngineThread.WaitUntil(() => probe.ClosedCount >= 1, Generous), "no close was reported");
        Assert.IsTrue(probe.Errors.First(e => e.IsFatal).Message.Length > 0);

        engine.Run(session.Dispose);
    }

    [TestMethod]
    public void A_telnet_session_negotiates_and_escapes_the_interpret_as_command_byte()
    {
        using var server = new LoopbackServer();
        using var engine = new EngineThread();
        var probe = new Probe();
        var session = Start(engine, server.Port, SessionProtocol.Telnet, probe);

        // A Telnet client opens with option negotiation, which always starts with IAC (0xFF).
        Assert.IsTrue(EngineThread.WaitUntil(() => server.Received.Length > 0, Generous), "no negotiation arrived");
        Assert.AreEqual((byte)0xFF, server.Received[0]);

        // A literal 0xFF in the data must be doubled so the server does not take it for IAC.
        engine.Run(() => session.Send([0x41, 0xFF, 0x42]));
        Assert.IsTrue(
            EngineThread.WaitUntil(() => Contains(server.Received, [0x41, 0xFF, 0xFF, 0x42]), Generous),
            $"the doubled byte never arrived; got {Convert.ToHexString(server.Received)}");

        engine.Run(session.Dispose);
    }

    [TestMethod]
    public void A_handler_may_dispose_the_session_it_is_told_has_closed()
    {
        using var server = new LoopbackServer(echo: true);
        using var engine = new EngineThread();
        var probe = new Probe();
        bool disposedInsideHandler = false;

        PuttySession doomed = engine.Run(() =>
        {
            var session = new PuttySession("127.0.0.1", server.Port, SessionProtocol.Raw);
            probe.Attach(session);
            session.Closed += (_, _) =>
            {
                session.Dispose();      // PuTTY is still inside the backend that raised this
                disposedInsideHandler = true;
            };
            session.Start();
            return session;
        });
        Assert.IsTrue(EngineThread.WaitUntil(() => server.ClientCount == 1, Generous), "never connected");

        server.DropClients();
        Assert.IsTrue(EngineThread.WaitUntil(() => disposedInsideHandler, Generous), "the handler never ran");

        // The engine must be intact afterwards: a fresh session still works end to end.
        var again = new Probe();
        var fresh = Start(engine, server.Port, SessionProtocol.Raw, again);
        Assert.IsTrue(EngineThread.WaitUntil(() => server.ClientCount == 1, Generous), "the second session never connected");
        engine.Run(() => fresh.Send("ok"u8));
        Assert.IsTrue(EngineThread.WaitUntil(() => again.Output.Length >= 2, Generous), "the second session got no echo");
        Assert.AreEqual("ok", Encoding.ASCII.GetString(again.Output));

        engine.Run(fresh.Dispose);
        Assert.IsFalse(doomed.IsConnected);   // disposed: answers without touching the engine
    }

    [TestMethod]
    public void Sending_on_a_disposed_session_is_refused()
    {
        using var server = new LoopbackServer();
        using var engine = new EngineThread();
        var session = Start(engine, server.Port, SessionProtocol.Raw, new Probe());
        engine.Run(session.Dispose);

        engine.Run(() => Assert.Throws<ObjectDisposedException>(() => session.Send("x"u8)));
    }

    [TestMethod]
    public void A_session_left_open_is_closed_by_engine_shutdown_and_disposing_it_later_is_harmless()
    {
        using var server = new LoopbackServer();
        PuttySession session;
        using (var engine = new EngineThread())
        {
            session = Start(engine, server.Port, SessionProtocol.Raw, new Probe());
            Assert.IsTrue(EngineThread.WaitUntil(() => server.ClientCount == 1, Generous), "never connected");
        }                           // engine shutdown closes the session and its socket

        session.Dispose();          // the engine is gone: this must not throw or crash
    }

    [TestMethod]
    public void A_session_cannot_be_started_twice()
    {
        using var server = new LoopbackServer();
        using var engine = new EngineThread();
        var session = Start(engine, server.Port, SessionProtocol.Raw, new Probe());

        engine.Run(() => Assert.Throws<InvalidOperationException>(session.Start));

        engine.Run(session.Dispose);
    }

    [TestMethod]
    public void A_port_outside_the_valid_range_is_rejected_up_front()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new PuttySession("127.0.0.1", 0, SessionProtocol.Raw));
        Assert.Throws<ArgumentOutOfRangeException>(() => new PuttySession("127.0.0.1", 65536, SessionProtocol.Raw));
    }
}
