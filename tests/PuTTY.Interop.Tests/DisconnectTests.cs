using System.Text;
using PuTTY.Interop;

namespace PuTTY.Interop.Tests;

/// <summary>
/// Disconnect: the connection ends and the session stays, with its terminal, screen and scrollback, so that the host can
/// keep the tab as it is and offer Reconnect (windows/window.c's close_session does the same when a connection ends).
/// </summary>
[TestClass]
public sealed class DisconnectTests
{
    private const string Font = "Consolas";
    private static readonly TimeSpan Generous = TimeSpan.FromSeconds(10);

    private sealed class Rig : IDisposable
    {
        public readonly EngineThread Engine = new();
        public readonly LoopbackServer Server = new();
        public readonly PuttySession Session;
        public int Closed;

        public Rig()
        {
            Session = Engine.Run(() =>
            {
                var session = new PuttySession("127.0.0.1", Server.Port, SessionProtocol.Raw);
                session.Closed += (_, _) => Interlocked.Increment(ref Closed);
                session.Start();
                session.AttachOffscreenView(640, 320, Font, 22);
                session.SetLocalEchoAndEditing(LocalMode.Off, LocalMode.Off);
                return session;
            });
            Assert.IsTrue(EngineThread.WaitUntil(() => Server.ClientCount == 1, Generous), "never connected");
        }

        public int InkInRow(int row)
        {
            TerminalGeometry g = Engine.Run(() => Session.Geometry);
            return Engine.Run(() =>
            {
                int ink = 0;
                for (int y = row * g.CellHeight; y < (row + 1) * g.CellHeight; y++)
                {
                    for (int x = 0; x < 20 * g.CellWidth; x++)
                    {
                        if (Session.ReadPixel(x, y) != 0)
                        {
                            ink++;
                        }
                    }
                }

                return ink;
            });
        }

        public void Dispose()
        {
            Engine.Run(Session.Dispose);
            Engine.Dispose();
            Server.Dispose();
        }
    }

    [TestMethod]
    public void Disconnect_closes_the_connection_raises_Closed_once_and_keeps_the_screen()
    {
        using var rig = new Rig();
        rig.Server.Send("KEEP ME"u8);
        Assert.IsTrue(EngineThread.WaitUntil(() => rig.InkInRow(0) > 30, Generous), "the text never reached the screen");

        rig.Engine.Run(rig.Session.Disconnect);

        Assert.IsTrue(EngineThread.WaitUntil(() => rig.Server.HungUpCount == 1, Generous), "the server never saw the connection close");
        Assert.AreEqual(1, Volatile.Read(ref rig.Closed), "Closed is raised once");
        Assert.IsFalse(rig.Engine.Run(() => rig.Session.IsConnected));
        Assert.IsTrue(rig.InkInRow(0) > 30, "the screen should still show the text after the connection is gone");
    }

    [TestMethod]
    public void Disconnecting_twice_and_typing_afterwards_is_harmless()
    {
        using var rig = new Rig();
        rig.Engine.Run(rig.Session.Disconnect);

        rig.Engine.Run(rig.Session.Disconnect);
        rig.Engine.Run(() => rig.Session.TypeText("typed after"));
        Assert.ThrowsExactly<InvalidOperationException>(() => rig.Engine.Run(() => rig.Session.Send("sent after"u8)));   // a closed connection refuses raw sends

        Assert.AreEqual(1, Volatile.Read(ref rig.Closed));
        Assert.IsFalse(Encoding.ASCII.GetString(rig.Server.Received).Contains("after", StringComparison.Ordinal), "nothing can be sent once disconnected");
    }

    [TestMethod]
    public void A_disconnected_session_can_be_reconnected_from_its_settings_and_the_old_one_disposed()
    {
        using var rig = new Rig();
        rig.Engine.Run(rig.Session.Disconnect);

        PuttySession again = rig.Engine.Run(() =>
        {
            PuttySession copy = rig.Session.Clone();
            copy.Start();
            copy.EnableTerminal(80, 24);
            return copy;
        });

        Assert.IsTrue(EngineThread.WaitUntil(() => rig.Server.ClientCount == 2, Generous), "the reconnection never arrived");
        rig.Engine.Run(() => again.Dispose());
    }

    [TestMethod]
    public void Disconnect_from_inside_an_event_of_the_session_takes_effect_afterwards()
    {
        using var rig = new Rig();
        rig.Engine.Run(() => rig.Session.TitleChanged += (_, _) => rig.Session.Disconnect());

        rig.Server.Send("\x1b]0;going\x07"u8);       // the title event runs inside the emulator, inside PuTTY

        Assert.IsTrue(EngineThread.WaitUntil(() => rig.Server.HungUpCount == 1, Generous), "the deferred disconnect never happened");
        Assert.AreEqual(1, Volatile.Read(ref rig.Closed));
    }

    [TestMethod]
    public void Disposing_a_session_that_has_been_disconnected_is_safe_and_so_is_disposing_the_engine_with_one_left()
    {
        using var engine = new EngineThread();
        using var server = new LoopbackServer();
        PuttySession session = engine.Run(() =>
        {
            var s = new PuttySession("127.0.0.1", server.Port, SessionProtocol.Raw);
            s.Start();
            s.EnableTerminal(80, 24);
            return s;
        });
        Assert.IsTrue(EngineThread.WaitUntil(() => server.ClientCount == 1, Generous));
        engine.Run(session.Disconnect);
        _ = session;        // not disposed: the engine's shutdown closes it
    }
}
