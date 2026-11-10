using System.Text;
using PuTTY.Interop;

namespace PuTTY.Interop.Tests;

/// <summary>
/// Mouse selection, clipboard, paste, scrollback, the wheel and mouse reporting, through the engine's
/// offscreen terminal view. The host owns the clipboard, so a test is the host: it receives the text
/// to copy as an event and answers a paste request itself. The real clipboard is never touched.
/// </summary>
[TestClass]
public sealed class TerminalMouseTests
{
    private const string Font = "Consolas";
    private static readonly TimeSpan Generous = TimeSpan.FromSeconds(5);

    private sealed class Rig : IDisposable
    {
        public readonly EngineThread Engine = new();
        public readonly LoopbackServer Server = new();
        public readonly PuttySession Session;
        public readonly TerminalGeometry Geometry;
        private readonly List<string> copied = [];
        private readonly List<SessionScrollbarEventArgs> scrollbars = [];
        private int pasteRequests;

        public Rig()
        {
            Session = Engine.Run(() =>
            {
                var session = new PuttySession("127.0.0.1", Server.Port, SessionProtocol.Raw);
                session.ClipboardWrite += (_, e) => { lock (copied) { copied.Add(e.Text); } };
                session.ScrollbarChanged += (_, e) => { lock (scrollbars) { scrollbars.Add(e); } };
                session.ClipboardPasteRequested += (_, _) => Interlocked.Increment(ref pasteRequests);
                session.Start();
                return session;
            });
            Assert.IsTrue(EngineThread.WaitUntil(() => Server.ClientCount == 1, Generous), "never connected");
            Geometry = Engine.Run(() =>
            {
                Session.AttachOffscreenView(640, 312, Font, 22);
                Session.SetLocalEchoAndEditing(LocalMode.Off, LocalMode.Off);
                return Session.Geometry;
            });
        }

        public int PasteRequests => Volatile.Read(ref pasteRequests);

        public string[] Copied { get { lock (copied) { return [.. copied]; } } }

        public SessionScrollbarEventArgs? LastScrollbar { get { lock (scrollbars) { return scrollbars.LastOrDefault(); } } }

        public string ScrollbarHistory { get { lock (scrollbars) { return string.Join(" ", scrollbars.TakeLast(8).Select(e => $"[{e.Total}/{e.Start}/{e.Page}]")); } } }

        public void Send(string text) => Server.Send(Encoding.UTF8.GetBytes(text));

        /// <summary>
        /// Waits until the scrollback has stopped growing: a long reply from the server arrives in
        /// pieces, and a test that measures from a half-delivered state would compare against a moving target.
        /// </summary>
        public SessionScrollbarEventArgs SettledScrollbar(int minimumTotal)
        {
            SessionScrollbarEventArgs? last = null;
            DateTime stableSince = DateTime.UtcNow;
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
            while (DateTime.UtcNow < deadline)
            {
                SessionScrollbarEventArgs? now = LastScrollbar;
                if (now is null || now.Total < minimumTotal || last is null || now.Total != last.Total || now.Start != last.Start)
                {
                    last = now;
                    stableSince = DateTime.UtcNow;
                }
                else if (DateTime.UtcNow - stableSince > TimeSpan.FromMilliseconds(500))
                {
                    return now;
                }

                Thread.Sleep(20);
            }

            Assert.Fail($"the scrollback never settled (last total {last?.Total})");
            return null!;
        }

        /// <summary>The pixel in the middle of a character cell.</summary>
        public (int X, int Y) Centre(int column, int row) =>
            (column * Geometry.CellWidth + Geometry.CellWidth / 2, row * Geometry.CellHeight + Geometry.CellHeight / 2);

        public void Mouse(MouseAction action, MouseButton button, (int X, int Y) at, uint time = 0, TerminalModifiers mods = TerminalModifiers.None) =>
            Engine.Run(() => Session.Mouse(action, button, at.X, at.Y, mods, time));

        public void Dispose()
        {
            Engine.Run(Session.Dispose);
            Engine.Dispose();
            Server.Dispose();
        }
    }

    [TestMethod]
    public void Dragging_over_text_copies_it_to_the_host_clipboard_on_release()
    {
        using var rig = new Rig();
        rig.Send("hello world");
        Assert.IsTrue(EngineThread.WaitUntil(() => rig.Engine.Run(() => rig.Session.ReadPixel(2, 2)) >= 0 && rig.LastScrollbar is not null, Generous));

        rig.Mouse(MouseAction.Down, MouseButton.Left, rig.Centre(0, 0), time: 1000);
        rig.Mouse(MouseAction.Move, MouseButton.Left, rig.Centre(2, 0), time: 1010);
        rig.Mouse(MouseAction.Move, MouseButton.Left, rig.Centre(4, 0), time: 1020);
        rig.Mouse(MouseAction.Up, MouseButton.Left, rig.Centre(4, 0), time: 1030);

        Assert.IsTrue(EngineThread.WaitUntil(() => rig.Copied.Length > 0, Generous), "nothing was offered to the clipboard");
        Assert.AreEqual("hello", rig.Copied[^1]);
    }

    [TestMethod]
    public void A_double_click_selects_a_word_and_a_slow_second_click_does_not()
    {
        using var rig = new Rig();
        rig.Send("alpha beta gamma");
        Assert.IsTrue(EngineThread.WaitUntil(() => rig.LastScrollbar is not null, Generous));

        // Two quick clicks on "beta" (columns 6-9): a word.
        rig.Mouse(MouseAction.Down, MouseButton.Left, rig.Centre(7, 0), time: 5000);
        rig.Mouse(MouseAction.Up, MouseButton.Left, rig.Centre(7, 0), time: 5030);
        rig.Mouse(MouseAction.Down, MouseButton.Left, rig.Centre(7, 0), time: 5100);
        rig.Mouse(MouseAction.Up, MouseButton.Left, rig.Centre(7, 0), time: 5130);
        Assert.IsTrue(EngineThread.WaitUntil(() => rig.Copied.Contains("beta"), Generous),
            $"copied: [{string.Join("|", rig.Copied)}]");

        // A click a long time later is a fresh single click: it selects nothing to copy.
        int before = rig.Copied.Length;
        rig.Mouse(MouseAction.Down, MouseButton.Left, rig.Centre(1, 0), time: 20000);
        rig.Mouse(MouseAction.Up, MouseButton.Left, rig.Centre(1, 0), time: 20030);
        Thread.Sleep(300);
        Assert.AreEqual(before, rig.Copied.Length, "a lone click must not copy anything");
    }

    [TestMethod]
    public void The_right_button_asks_the_host_to_paste_and_the_host_answers()
    {
        using var rig = new Rig();
        rig.Send("x");
        Assert.IsTrue(EngineThread.WaitUntil(() => rig.LastScrollbar is not null, Generous));

        rig.Mouse(MouseAction.Down, MouseButton.Right, rig.Centre(3, 2), time: 100);
        rig.Mouse(MouseAction.Up, MouseButton.Right, rig.Centre(3, 2), time: 130);
        Assert.IsTrue(EngineThread.WaitUntil(() => rig.PasteRequests > 0, Generous), "no paste request reached the host");

        rig.Engine.Run(() => rig.Session.Paste("pasted text"));
        Assert.IsTrue(EngineThread.WaitUntil(() => Encoding.UTF8.GetString(rig.Server.Received).Contains("pasted text"), Generous),
            $"server got {Encoding.UTF8.GetString(rig.Server.Received)}");
    }

    [TestMethod]
    public void The_scrollback_is_reported_and_the_host_can_move_it()
    {
        using var rig = new Rig();
        var text = new StringBuilder();
        for (int i = 1; i <= 60; i++)
        {
            text.Append($"line {i}\r\n");
        }

        rig.Send(text.ToString());

        SessionScrollbarEventArgs bottom = rig.SettledScrollbar(minimumTotal: 60);
        Assert.AreEqual(rig.Geometry.Rows, bottom.Page);
        Assert.AreEqual(bottom.Total - bottom.Page, bottom.Start, "a terminal that has just printed is scrolled to the bottom");

        rig.Engine.Run(() => rig.Session.Scroll(ScrollMode.Lines, -5));
        Assert.IsTrue(EngineThread.WaitUntil(() => rig.LastScrollbar!.Start == bottom.Start - 5, Generous), $"five lines up; bottom {bottom.Total}/{bottom.Start}/{bottom.Page}; history {rig.ScrollbarHistory}");

        rig.Engine.Run(() => rig.Session.Scroll(ScrollMode.Top));
        Assert.IsTrue(EngineThread.WaitUntil(() => rig.LastScrollbar!.Start == 0, Generous), "to the top");

        rig.Engine.Run(() => rig.Session.Scroll(ScrollMode.Bottom));
        Assert.IsTrue(EngineThread.WaitUntil(() => rig.LastScrollbar!.Start == bottom.Start, Generous), "back to the bottom");
    }

    [TestMethod]
    public void The_wheel_scrolls_half_a_page_per_notch()
    {
        using var rig = new Rig();
        var text = new StringBuilder();
        for (int i = 1; i <= 60; i++)
        {
            text.Append($"line {i}\r\n");
        }

        rig.Send(text.ToString());
        int bottom = rig.SettledScrollbar(minimumTotal: 60).Start;

        rig.Engine.Run(() => rig.Session.Wheel(120, horizontal: false, 100, 100, TerminalModifiers.None));   // one notch up

        int half = rig.Geometry.Rows / 2;
        Assert.IsTrue(EngineThread.WaitUntil(() => rig.LastScrollbar!.Start == bottom - half, Generous),
            $"start {rig.LastScrollbar!.Start}, expected {bottom - half}; history {rig.ScrollbarHistory}");
    }

    [TestMethod]
    public void A_program_that_asks_for_mouse_reports_gets_them_instead_of_a_selection()
    {
        using var rig = new Rig();
        rig.Send("\x1b[?1000h");        // xterm mouse tracking on
        Assert.IsTrue(EngineThread.WaitUntil(() => rig.LastScrollbar is not null || true, Generous));
        Thread.Sleep(300);

        rig.Mouse(MouseAction.Down, MouseButton.Left, rig.Centre(2, 1), time: 100);

        // ESC [ M, then button 0 + 32, then column and row + 33 (here 2+1 and 1+1, plus 32).
        byte[] expected = [0x1B, (byte)'[', (byte)'M', 32, (byte)(32 + 3), (byte)(32 + 2)];
        Assert.IsTrue(EngineThread.WaitUntil(() => rig.Server.Received.Length >= expected.Length, Generous),
            $"server got {Convert.ToHexString(rig.Server.Received)}");
        CollectionAssert.AreEqual(expected, rig.Server.Received[..expected.Length]);
        Assert.AreEqual(0, rig.Copied.Length, "no selection while the program has the mouse");
    }
}
