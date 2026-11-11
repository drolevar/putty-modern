using System.Text;
using PuTTY.Interop;

namespace PuTTY.Interop.Tests;

/// <summary>
/// The emulator drawing through the real TermWin onto an offscreen view: a server sends text and
/// escape sequences over a real TCP connection and the tests read pixels back. PuTTY's default
/// palette applies: foreground 187,187,187, background black, red 187,0,0, cursor green.
/// </summary>
[TestClass]
public sealed class TerminalViewTests
{
    private const string Font = "Consolas";
    private const uint DefaultForeground = 0xBBBBBB;
    private const uint Black = 0x000000;
    private const uint Red = 0xBB0000;
    private const uint CursorGreen = 0x00FF00;
    private static readonly TimeSpan Generous = TimeSpan.FromSeconds(5);

    private sealed class Rig : IDisposable
    {
        public readonly EngineThread Engine = new();
        public readonly LoopbackServer Server = new();
        public readonly PuttySession Session;
        public readonly int CellWidth;
        public readonly int CellHeight;

        /// <param name="registry">With one, the session is a saved one named "rig" with these settings (PuTTY's own registry
        /// value names): a quick connection has only the built-in defaults, and the default bell would play a sound.</param>
        public Rig(int width = 640, int height = 320, float size = 22, RegistrySandbox? registry = null,
            params (string Name, object Value)[] settings)
        {
            Session = Engine.Run(() =>
            {
                PuttySession session;
                if (registry is null)
                {
                    session = new PuttySession("127.0.0.1", Server.Port, SessionProtocol.Raw);
                }
                else
                {
                    registry.WriteSession("rig", "127.0.0.1", Server.Port, "raw", null, settings);
                    session = PuttySession.OpenSaved("rig");
                }

                session.Start();
                return session;
            });
            Assert.IsTrue(EngineThread.WaitUntil(() => Server.ClientCount == 1, Generous), "never connected");
            Engine.Run(() =>
            {
                Session.AttachOffscreenView(width, height, Font, size);
                // A raw session would hold typing and echo it; here only the server's bytes matter.
                Session.SetLocalEchoAndEditing(LocalMode.Off, LocalMode.Off);
            });
            TerminalGeometry g = Geometry;
            CellWidth = g.CellWidth;
            CellHeight = g.CellHeight;
        }

        public TerminalGeometry Geometry => Engine.Run(() => Session.Geometry);

        public uint Pixel(int x, int y) => Engine.Run(() => Session.ReadPixel(x, y));

        public void Send(string text) => Server.Send(Encoding.UTF8.GetBytes(text));

        /// <summary>Pixels of one cell that satisfy a test.</summary>
        public int CountInCell(int column, int row, Func<uint, bool> predicate) =>
            Engine.Run(() =>
            {
                int count = 0;
                for (int y = row * CellHeight; y < (row + 1) * CellHeight; y++)
                {
                    for (int x = column * CellWidth; x < (column + 1) * CellWidth; x++)
                    {
                        if (predicate(Session.ReadPixel(x, y)))
                        {
                            count++;
                        }
                    }
                }

                return count;
            });

        public bool WaitForCell(int column, int row, Func<uint, bool> predicate, int atLeast = 1) =>
            EngineThread.WaitUntil(() => CountInCell(column, row, predicate) >= atLeast, Generous);

        public void Dispose()
        {
            Engine.Run(Session.Dispose);
            Engine.Dispose();
            Server.Dispose();
        }
    }

    [TestMethod]
    public void The_terminal_is_sized_to_the_whole_cells_that_fit_the_view()
    {
        using var rig = new Rig(640, 320);

        TerminalGeometry g = rig.Geometry;

        Assert.IsTrue(g.CellWidth > 4 && g.CellHeight > 8, $"cell {g.CellWidth}x{g.CellHeight}");
        Assert.AreEqual(640 / g.CellWidth, g.Columns);
        Assert.AreEqual(320 / g.CellHeight, g.Rows);
    }

    [TestMethod]
    public void The_host_is_told_where_the_cursor_is_when_it_moves_to_another_cell()
    {
        var moves = new List<SessionCursorEventArgs>();
        using var rig = new Rig();
        rig.Engine.Run(() => rig.Session.CursorMoved += (_, e) => { lock (moves) { moves.Add(e); } });

        rig.Send("Hi");
        Assert.IsTrue(EngineThread.WaitUntil(() => { lock (moves) { return moves.Any(m => m.X == 2 * rig.CellWidth && m.Y == 0); } }, Generous),
            "the cursor should be reported after two characters, at x = " + 2 * rig.CellWidth);
        rig.Send("\r\n");
        Assert.IsTrue(EngineThread.WaitUntil(() => { lock (moves) { return moves.Any(m => m.X == 0 && m.Y == rig.CellHeight); } }, Generous),
            "the cursor should be reported at the start of the next line");
        lock (moves)
        {
            Assert.IsTrue(moves.All(m => m.Width == rig.CellWidth && m.Height == rig.CellHeight), "each report carries the cell size");
            int count = moves.Count;
            Assert.IsTrue(count <= 6, $"a report per cell moved to, not per blink or repaint ({count})");
        }
    }

    [TestMethod]
    public void Text_from_the_server_is_drawn_in_the_default_colours()
    {
        using var rig = new Rig();

        rig.Send("Hi");

        Assert.IsTrue(rig.WaitForCell(0, 0, p => p != Black, atLeast: 10), "nothing was drawn for 'H'");
        Assert.IsTrue(rig.CountInCell(1, 0, p => p != Black) >= 3, "nothing was drawn for 'i'");
        Assert.IsTrue(rig.CountInCell(0, 0, p => p == DefaultForeground) > 0, "no fully covered pixel in the default foreground colour");
        Assert.AreEqual(0, rig.CountInCell(5, 3, p => p != Black), "an untouched cell must stay background");
    }

    [TestMethod]
    public void A_colour_escape_changes_the_colour_of_what_follows()
    {
        using var rig = new Rig();

        rig.Send("\x1b[31mX");       // red

        Assert.IsTrue(rig.WaitForCell(0, 0, p => p == Red, atLeast: 1), "no pixel in PuTTY's red");
    }

    [TestMethod]
    public void Reverse_video_fills_the_cell_with_the_foreground_colour()
    {
        using var rig = new Rig();

        rig.Send("\x1b[7m \x1b[0m.");  // a reversed space, then a dot

        Assert.IsTrue(rig.WaitForCell(0, 0, p => p == DefaultForeground, atLeast: rig.CellWidth * rig.CellHeight),
            "a reversed space should be solid foreground colour");
    }

    [TestMethod]
    public void The_active_cursor_is_a_block_in_the_cursor_colour_after_the_text()
    {
        using var rig = new Rig();

        rig.Send("Hi");

        Assert.IsTrue(rig.WaitForCell(2, 0, p => p == CursorGreen, atLeast: rig.CellWidth * rig.CellHeight / 2),
            "the cell after the text should be the green cursor block");
    }

    [TestMethod]
    public void A_title_sequence_reaches_the_host()
    {
        using var rig = new Rig();
        var titles = new List<string>();
        rig.Engine.Run(() => rig.Session.TitleChanged += (_, e) => { lock (titles) { titles.Add(e.Title); } });

        rig.Send("\x1b]0;Build server\x07");

        Assert.IsTrue(EngineThread.WaitUntil(() => { lock (titles) { return titles.Contains("Build server"); } }, Generous),
            "the title never arrived");
    }

    /// <summary>The screen text, once it is the expected text (the paint comes a moment after the server's bytes).</summary>
    private static string WaitForScreenText(Rig rig, string expected)
    {
        string last = string.Empty;
        EngineThread.WaitUntil(() =>
        {
            last = rig.Engine.Run(() => rig.Session.GetScreenText());
            return last == expected;
        }, Generous);
        return last;
    }

    [TestMethod]
    public void The_screen_text_is_what_the_terminal_shows_row_by_row_with_the_blanks_at_the_ends_left_out()
    {
        using var rig = new Rig();

        rig.Send("Hello   \r\n\r\nWorld");

        Assert.AreEqual("Hello\n\nWorld", WaitForScreenText(rig, "Hello\n\nWorld"));
    }

    [TestMethod]
    public void The_screen_text_follows_a_cleared_screen_and_new_text()
    {
        using var rig = new Rig();
        rig.Send("old text");
        Assert.AreEqual("old text", WaitForScreenText(rig, "old text"));

        rig.Send("\x1b[2J\x1b[HNext");

        Assert.AreEqual("Next", WaitForScreenText(rig, "Next"));
    }

    [TestMethod]
    public void The_screen_text_holds_wide_characters_and_characters_outside_the_basic_plane_whole()
    {
        using var rig = new Rig();

        rig.Send("a\u4F60\u597D b\U0001F600");

        Assert.AreEqual("a\u4F60\u597D b\U0001F600", WaitForScreenText(rig, "a\u4F60\u597D b\U0001F600"));
    }

    // The bell is the visual one (Beep = 2) in these tests, so that no sound is played on the machine that runs them.
    private const int BellVisual = 2;
    private const int BellDisabled = 0;

    [TestMethod]
    public void The_bell_reaches_the_host_with_the_sessions_indicator_setting()
    {
        using var registry = new RegistrySandbox();
        using var rig = new Rig(registry: registry, settings: [("Beep", BellVisual), ("BeepInd", 2)]);
        var bells = new List<BellIndicator>();
        rig.Engine.Run(() => rig.Session.Bell += (_, e) => { lock (bells) { bells.Add(e.Indicator); } });

        rig.Send("\a");

        Assert.IsTrue(EngineThread.WaitUntil(() => { lock (bells) { return bells.Count > 0; } }, Generous), "the bell never arrived");
        lock (bells)
        {
            Assert.AreEqual(BellIndicator.Steady, bells[0]);
        }
    }

    [TestMethod]
    public void A_disabled_bell_does_not_reach_the_host()
    {
        using var registry = new RegistrySandbox();
        using var rig = new Rig(registry: registry, settings: [("Beep", BellDisabled)]);
        int bells = 0;
        rig.Engine.Run(() => rig.Session.Bell += (_, _) => Interlocked.Increment(ref bells));

        rig.Send("\a");
        rig.Send("done");

        Assert.IsTrue(rig.WaitForCell(0, 0, p => p != Black, atLeast: 10), "the text after the bell never arrived");
        Assert.AreEqual(0, Volatile.Read(ref bells));
    }

    [TestMethod]
    public void The_font_a_session_asks_for_is_the_one_in_its_settings()
    {
        using (var rig = new Rig())
        {
            SettingsFont builtIn = rig.Engine.Run(() => rig.Session.GetFont());
            Assert.AreEqual("Courier New", builtIn.Name, "PuTTY's default font");
            Assert.AreEqual(10, builtIn.Height);
            Assert.IsFalse(builtIn.Bold);
        }

        using var registry = new RegistrySandbox();
        using var saved = new Rig(registry: registry, settings: [("Font", "Cascadia Mono"), ("FontHeight", 13), ("FontIsBold", 1), ("FontCharSet", 0)]);
        SettingsFont font = saved.Engine.Run(() => saved.Session.GetFont());
        Assert.AreEqual("Cascadia Mono", font.Name);
        Assert.AreEqual(13, font.Height);
        Assert.IsTrue(font.Bold);
    }

    [TestMethod]
    public void Resizing_the_view_refits_the_terminal_and_repaints_the_text()
    {
        using var rig = new Rig(640, 320);
        rig.Send("Hi");
        Assert.IsTrue(rig.WaitForCell(0, 0, p => p != Black, atLeast: 10));

        rig.Engine.Run(() => rig.Session.ResizeView(320, 160));

        TerminalGeometry g = rig.Geometry;
        Assert.AreEqual(320 / g.CellWidth, g.Columns);
        Assert.AreEqual(160 / g.CellHeight, g.Rows);
        Assert.IsTrue(rig.WaitForCell(0, 0, p => p != Black, atLeast: 10), "the text was not repainted after the resize");
    }

    [TestMethod]
    public void A_wide_character_spreads_over_two_cells()
    {
        using var rig = new Rig();

        rig.Send("日");          // a CJK ideograph: two cells wide

        Assert.IsTrue(rig.WaitForCell(0, 0, p => p != Black, atLeast: 10), "nothing drawn in the first cell");
        Assert.IsTrue(rig.CountInCell(1, 0, p => p != Black) > 0, "the glyph should reach into the second cell");
    }

    [TestMethod]
    public void A_font_that_is_not_installed_is_refused_and_leaves_the_session_usable()
    {
        using var engine = new EngineThread();
        using var server = new LoopbackServer();
        PuttySession session = engine.Run(() =>
        {
            var s = new PuttySession("127.0.0.1", server.Port, SessionProtocol.Raw);
            s.Start();
            return s;
        });

        engine.Run(() => Assert.Throws<InvalidOperationException>(
            () => session.AttachOffscreenView(320, 160, "No Such Font Family 42", 16)));
        engine.Run(() => session.AttachOffscreenView(320, 160, Font, 16));   // a later attach works

        engine.Run(session.Dispose);
    }
}
