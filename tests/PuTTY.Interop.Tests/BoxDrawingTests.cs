using System.Text;
using PuTTY.Interop;

namespace PuTTY.Interop.Tests;

/// <summary>
/// Line-drawing and block characters (U+2500..U+259F) are drawn by the renderer itself so that neighbouring cells meet. A
/// font's own glyphs leave a one-pixel gap between rows or columns with some fonts and sizes (the cell is a whole number of
/// pixels, the glyph spans the font's unrounded ascent and descent): Consolas at 20 px and Courier New at 15 px did.
/// </summary>
[TestClass]
public sealed class BoxDrawingTests
{
    private const uint Foreground = 0xBBBBBB;
    private static readonly TimeSpan Generous = TimeSpan.FromSeconds(10);

    private sealed class Screen : IDisposable
    {
        private readonly LoopbackServer server = new();
        private readonly EngineThread engine = new();
        private readonly PuttySession session;
        public readonly int CellWidth;
        public readonly int CellHeight;

        public Screen(string font, float size, string text)
        {
            session = engine.Run(() =>
            {
                var s = new PuttySession("127.0.0.1", server.Port, SessionProtocol.Raw);
                s.Start();
                return s;
            });
            Assert.IsTrue(EngineThread.WaitUntil(() => server.ClientCount == 1, Generous), "never connected");
            engine.Run(() =>
            {
                session.AttachOffscreenView(320, 240, font, size);
                session.SetLocalEchoAndEditing(LocalMode.Off, LocalMode.Off);
            });
            TerminalGeometry g = engine.Run(() => session.Geometry);
            CellWidth = g.CellWidth;
            CellHeight = g.CellHeight;
            server.Send(Encoding.UTF8.GetBytes(text + "\u0001"));       // a marker the screen text will not show
            Assert.IsTrue(EngineThread.WaitUntil(() => engine.Run(() => session.GetScreenText()).Length >= text.Replace("\r\n", "\n").Length, Generous),
                "the text never reached the screen");
            Thread.Sleep(200);                                          // let the last repaint land
        }

        public uint Pixel(int x, int y) => engine.Run(() => session.ReadPixel(x, y));

        public void Dispose()
        {
            engine.Run(session.Dispose);
            engine.Dispose();
            server.Dispose();
        }
    }

    public static IEnumerable<object[]> Fonts =>
    [
        ["Consolas", 14.7f],
        ["Consolas", 20f],           // a gap of one pixel at each row boundary before the characters were drawn by the renderer
        ["Courier New", 15f],        // the same
        ["Cascadia Mono", 15f],
    ];

    [TestMethod]
    [DynamicData(nameof(Fonts))]
    public void A_column_of_vertical_bars_has_no_gap_at_the_row_boundaries(string font, float size)
    {
        using var screen = new Screen(font, size, "│\r\n│\r\n│");
        int rows = 3 * screen.CellHeight;

        bool continuous = false;
        for (int x = 0; x < screen.CellWidth && !continuous; x++)
        {
            continuous = Enumerable.Range(0, rows).All(y => screen.Pixel(x, y) == Foreground);
        }

        Assert.IsTrue(continuous, $"no pixel column of the bar is solid over three rows in {font} {size} (cell {screen.CellWidth}x{screen.CellHeight})");
    }

    [TestMethod]
    [DynamicData(nameof(Fonts))]
    public void A_row_of_horizontal_bars_has_no_gap_at_the_column_boundaries(string font, float size)
    {
        using var screen = new Screen(font, size, "───");
        int columns = 3 * screen.CellWidth;

        bool continuous = false;
        for (int y = 0; y < screen.CellHeight && !continuous; y++)
        {
            continuous = Enumerable.Range(0, columns).All(x => screen.Pixel(x, y) == Foreground);
        }

        Assert.IsTrue(continuous, $"no pixel row of the bar is solid over three cells in {font} {size}");
    }

    [TestMethod]
    [DynamicData(nameof(Fonts))]
    public void A_double_line_frame_is_closed_and_has_no_gaps(string font, float size)
    {
        // The frame of a dialog: corners, a double bar on each side and a double bar across. Every pixel row of the left
        // side, and every pixel column of the top side, must have ink, from corner to corner (a corner character starts at
        // the middle of its cell, so the first and last half cells are left out).
        using var screen = new Screen(font, size, "╔═╗\r\n║ ║\r\n╚═╝");
        int cw = screen.CellWidth, ch = screen.CellHeight;

        for (int y = ch / 2 + 2; y < 5 * ch / 2 - 2; y++)
        {
            bool ink = Enumerable.Range(0, cw).Any(x => screen.Pixel(x, y) == Foreground);
            Assert.IsTrue(ink, $"the left side of the frame has a gap at pixel row {y} in {font} {size}");
        }

        for (int x = cw / 2 + 2; x < 5 * cw / 2 - 2; x++)
        {
            bool ink = Enumerable.Range(0, ch).Any(y => screen.Pixel(x, y) == Foreground);
            Assert.IsTrue(ink, $"the top of the frame has a gap at pixel column {x} in {font} {size}");
        }

        // A double line is two lines with a gap between them, and not a solid bar.
        int solidColumns = Enumerable.Range(0, cw).Count(x => Enumerable.Range(ch, ch).All(y => screen.Pixel(x, y) == Foreground));
        Assert.AreEqual(2, solidColumns, "the double bar should be two lines");
    }

    [TestMethod]
    public void Block_elements_fill_exactly_the_part_of_the_cell_they_name()
    {
        using var screen = new Screen("Consolas", 20f, "█▀▄▌▐░▒▓");
        int cw = screen.CellWidth, ch = screen.CellHeight;

        Assert.IsTrue(Cell(0, (x, y) => true), "the full block must fill the whole cell");
        Assert.IsTrue(Cell(1, (x, y) => y < (ch + 1) / 2) && Cell(1, (x, y) => y >= (ch + 1) / 2, ink: false), "the upper half block");
        Assert.IsTrue(Cell(2, (x, y) => y >= (ch + 1) / 2) && Cell(2, (x, y) => y < (ch + 1) / 2, ink: false), "the lower half block");
        Assert.IsTrue(Cell(3, (x, y) => x < (cw + 1) / 2) && Cell(3, (x, y) => x >= (cw + 1) / 2, ink: false), "the left half block");
        Assert.IsTrue(Cell(4, (x, y) => x >= (cw + 1) / 2) && Cell(4, (x, y) => x < (cw + 1) / 2, ink: false), "the right half block");

        // The shades are the foreground at 25, 50 and 75 percent over the background: darker than it, lighter than the background, in that order.
        uint light = screen.Pixel(5 * cw + 1, 1), medium = screen.Pixel(6 * cw + 1, 1), dark = screen.Pixel(7 * cw + 1, 1);
        Assert.IsTrue(light > 0 && light < medium && medium < dark && dark < Foreground, $"shades {light:X6} {medium:X6} {dark:X6}");

        bool Cell(int index, Func<int, int, bool> selector, bool ink = true)
        {
            for (int y = 0; y < ch; y++)
            {
                for (int x = 0; x < cw; x++)
                {
                    if (selector(x, y) && (screen.Pixel(index * cw + x, y) == Foreground) != ink)
                    {
                        return false;
                    }
                }
            }

            return true;
        }
    }
}
