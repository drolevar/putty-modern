using System.Text;
using PuTTY.Interop;

namespace PuTTY.Interop.Tests;

/// <summary>
/// The engine with many sessions at once, as a workspace of tabs has them: PuTTY's core keeps global state (a timer
/// queue, a callback queue, a random pool, a list of waitable handles limited to 64), and every session lives on the
/// one engine thread. These tests run many real loopback connections together and look for what would go wrong:
/// data crossing between sessions, a closed session taking the others with it, a limit that stops the 65th.
/// </summary>
[TestClass]
public sealed class ManySessionsTests
{
    private static readonly TimeSpan Generous = TimeSpan.FromSeconds(30);

    private sealed class Member(int index) : IDisposable
    {
        public readonly int Index = index;
        public readonly LoopbackServer Server = new();
        public PuttySession Session = null!;
        public int Closed;

        public string Marker => $"T{Index:D3}";

        public void Dispose() => Server.Dispose();
    }

    private static List<Member> StartMembers(EngineThread engine, int count, bool withView)
    {
        var members = new List<Member>();
        for (int i = 0; i < count; i++)
        {
            members.Add(new Member(i));
        }

        engine.Run(() =>
        {
            foreach (Member member in members)
            {
                Member m = member;
                var session = new PuttySession("127.0.0.1", m.Server.Port, SessionProtocol.Raw);
                session.Closed += (_, _) => Interlocked.Increment(ref m.Closed);
                session.Start();
                if (withView)
                {
                    session.AttachOffscreenView(320, 160, "Consolas", 16);
                }
                else
                {
                    session.EnableTerminal(80, 24);
                }

                session.SetLocalEchoAndEditing(LocalMode.Off, LocalMode.Off);
                m.Session = session;
            }
        });
        Assert.IsTrue(EngineThread.WaitUntil(() => members.All(m => m.Server.ClientCount == 1), Generous),
            $"only {members.Count(m => m.Server.ClientCount == 1)} of {count} sessions connected");
        return members;
    }

    private static string Text(byte[] bytes) => Encoding.ASCII.GetString(bytes);

    private static bool HasInkInFirstCell(EngineThread engine, Member member) =>
        engine.Run(() =>
        {
            TerminalGeometry g = member.Session.Geometry;
            for (int y = 0; y < g.CellHeight; y++)
            {
                for (int x = 0; x < g.CellWidth; x++)
                {
                    if (member.Session.ReadPixel(x, y) != 0x000000)
                    {
                        return true;
                    }
                }
            }

            return false;
        });

    [TestMethod]
    public void Many_sessions_with_terminals_run_side_by_side_without_mixing_their_data()
    {
        const int Count = 24;
        using var registry = new RegistrySandbox();
        using var engine = new EngineThread();
        List<Member> members = StartMembers(engine, Count, withView: true);
        try
        {
            // Each server draws a letter of its own into its session's terminal; each session types a marker of its own.
            foreach (Member m in members)
            {
                m.Server.Send(Encoding.ASCII.GetBytes("#"));
            }

            engine.Run(() =>
            {
                foreach (Member m in members)
                {
                    m.Session.TypeText(m.Marker);
                }
            });

            Assert.IsTrue(EngineThread.WaitUntil(() => members.All(m => Text(m.Server.Received).Contains(m.Marker)), Generous),
                "some markers never reached their servers: " + string.Join(", ", members.Where(m => !Text(m.Server.Received).Contains(m.Marker)).Select(m => m.Marker)));
            foreach (Member m in members)
            {
                string got = Text(m.Server.Received);
                Assert.AreEqual(m.Marker, got, $"server {m.Index} received what another session typed: [{got}]");
            }

            Assert.IsTrue(EngineThread.WaitUntil(() => members.All(m => HasInkInFirstCell(engine, m)), Generous),
                "some terminals never drew what their server sent");
        }
        finally
        {
            engine.Run(() => { foreach (Member m in members) { m.Session.Dispose(); } });
            foreach (Member m in members) { m.Dispose(); }
        }
    }

    [TestMethod]
    public void Closing_some_sessions_in_any_order_leaves_the_others_working()
    {
        const int Count = 20;
        using var registry = new RegistrySandbox();
        using var engine = new EngineThread();
        List<Member> members = StartMembers(engine, Count, withView: true);
        try
        {
            // Close the second half from the last to the first, and every fourth of the first half, while data flows.
            foreach (Member m in members)
            {
                m.Server.Send(Encoding.ASCII.GetBytes("before"));
            }

            engine.Run(() =>
            {
                for (int i = Count - 1; i >= Count / 2; i--)
                {
                    members[i].Session.Dispose();
                }

                for (int i = 0; i < Count / 2; i += 4)
                {
                    members[i].Session.Dispose();
                }
            });

            Member[] survivors = [.. members.Where((m, i) => i < Count / 2 && i % 4 != 0)];
            Assert.IsTrue(survivors.Length >= 5);
            foreach (Member m in survivors)
            {
                m.Server.Send(Encoding.ASCII.GetBytes("after"));
            }

            engine.Run(() =>
            {
                foreach (Member m in survivors)
                {
                    m.Session.TypeText("U" + m.Marker);
                }
            });

            Assert.IsTrue(EngineThread.WaitUntil(() => survivors.All(m => Text(m.Server.Received).Contains("U" + m.Marker)), Generous),
                "a survivor stopped working after others closed");
            foreach (Member m in survivors)
            {
                Assert.AreEqual(0, m.Closed, $"session {m.Index} was closed though only others were");
                Assert.IsTrue(HasInkInFirstCell(engine, m));
            }

        }
        finally
        {
            engine.Run(() => { foreach (Member m in members) { m.Session.Dispose(); } });
            foreach (Member m in members) { m.Dispose(); }
        }
    }

    [TestMethod]
    public void More_than_sixty_four_sessions_can_be_open_together()
    {
        // PuTTY's handle-wait list asserts at 64 waitable handles; network sessions here do not use it (their sockets
        // report through window messages), so a workspace is not limited to 64 tabs.
        const int Count = 70;
        using var registry = new RegistrySandbox();
        using var engine = new EngineThread();
        List<Member> members = StartMembers(engine, Count, withView: false);
        try
        {
            engine.Run(() =>
            {
                foreach (Member m in members)
                {
                    m.Session.TypeText(m.Marker);
                }
            });

            Assert.IsTrue(EngineThread.WaitUntil(() => members.All(m => Text(m.Server.Received) == m.Marker), Generous),
                "only " + members.Count(m => Text(m.Server.Received) == m.Marker) + " of " + Count + " sessions got their marker through");
            Assert.IsTrue(members.All(m => engine.Run(() => m.Session.IsConnected)), "all are connected");
        }
        finally
        {
            engine.Run(() => { foreach (Member m in members) { m.Session.Dispose(); } });
            foreach (Member m in members) { m.Dispose(); }
        }
    }

    [TestMethod]
    public void A_connection_lost_in_one_session_does_not_disturb_the_others()
    {
        const int Count = 8;
        using var registry = new RegistrySandbox();
        using var engine = new EngineThread();
        List<Member> members = StartMembers(engine, Count, withView: true);
        try
        {
            Member victim = members[3];
            victim.Server.DropClients();           // the server hangs up on one client

            Assert.IsTrue(EngineThread.WaitUntil(() => Volatile.Read(ref victim.Closed) > 0, Generous), "the dropped session never reported that it closed");

            foreach (Member m in members.Where(m => m != victim))
            {
                Assert.AreEqual(0, m.Closed, $"session {m.Index} closed when another's connection dropped");
                m.Server.Send(Encoding.ASCII.GetBytes("still here"));
            }

            engine.Run(() =>
            {
                foreach (Member m in members.Where(m => m != victim))
                {
                    m.Session.TypeText(m.Marker);
                }
            });
            Assert.IsTrue(EngineThread.WaitUntil(() => members.Where(m => m != victim).All(m => Text(m.Server.Received).Contains(m.Marker)), Generous));
        }
        finally
        {
            engine.Run(() => { foreach (Member m in members) { m.Session.Dispose(); } });
            foreach (Member m in members) { m.Dispose(); }
        }
    }
}
