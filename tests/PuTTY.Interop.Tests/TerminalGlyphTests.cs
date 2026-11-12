using System.Runtime.InteropServices;
using PuTTY.Interop;

namespace PuTTY.Interop.Tests;

/// <summary>
/// Glyph drawing that PuTTY's own front end does and the engine's renderer had to learn: the Powerline separators
/// (U+E0B0..U+E0BF, drawn by the renderer so that they fill the cell with any font), double-width and double-height lines
/// (DECDWL, DECDHL: the glyph is stretched, not just spread over more cells) and "Use system colours".
/// </summary>
[TestClass]
public sealed partial class TerminalGlyphTests
{
    private const uint Foreground = 0xBBBBBB;
    private const string Font = "Consolas";

    [LibraryImport("user32.dll")]
    private static partial uint GetSysColor(int index);

    private static uint Rgb(uint colorRef) => ((colorRef & 0xFF) << 16) | (colorRef & 0xFF00) | ((colorRef >> 16) & 0xFF);

    [TestMethod]
    [DataRow(14.7f)]
    [DataRow(20f)]
    public void Powerline_separators_fill_their_cell_and_meet_the_next_row(float size)
    {
        // Cells: 0 lower-left triangle, 1 right-pointing triangle, 2 right half circle, 3 left-pointing triangle, 4 slash.
        const string Row = "\uE0B8\uE0B0\uE0B4\uE0B2\uE0BB";
        using var screen = new TextScreen(Font, size, Row + "\r\n" + Row, "\uE0BB");
        int cw = screen.CellWidth, ch = screen.CellHeight;
        uint[] px = screen.Snapshot(5 * cw, 2 * ch);
        uint At(int cell, int x, int y) => px[y * 5 * cw + cell * cw + x];
        bool Ink(int cell, int x, int y) => At(cell, x, y) == Foreground;
        bool AnyInk(int cell, int x, int y) => At(cell, x, y) != 0;
        // The shapes end in points at the cell's corners, which are covered by part of a pixel, so the checks that need
        // full coverage keep away from them; what matters is that they reach the edges of the cell.
        IEnumerable<int> Middle(int top, int height) => Enumerable.Range(top + height / 4, height / 2);
        bool Reaches(int cell, int x, int row) => AnyInk(cell, x, row * ch) && AnyInk(cell, x, row * ch + ch - 1);

        // The lower-left triangle: full along its left edge below the first few rows and along the whole bottom row, and
        // nothing in the upper right corner.
        Assert.IsTrue(Enumerable.Range(ch / 3, ch - ch / 3).All(y => Ink(0, 0, y)), "the left edge of the lower-left triangle");
        Assert.IsTrue(Enumerable.Range(2, cw - 4).All(x => Ink(0, x, ch - 1)), "the bottom edge of the lower-left triangle");
        Assert.IsFalse(AnyInk(0, cw - 1, 0), "the upper right corner is empty");

        // The right-pointing triangle: its flat side is solid in the middle of each row and reaches the top and bottom
        // of the cell, the tip touches the right edge, the corners next to the tip are empty.
        Assert.IsTrue(Middle(0, ch).All(y => Ink(1, 0, y)) && Middle(ch, ch).All(y => Ink(1, 0, y)), "the triangle's flat side");
        Assert.IsTrue(Reaches(1, 0, 0) && Reaches(1, 0, 1), "the triangle does not reach the top and bottom of its cell");
        Assert.IsTrue(AnyInk(1, cw - 1, ch / 2) || AnyInk(1, cw - 1, ch / 2 - 1), "the tip reaches the right edge");
        Assert.IsFalse(AnyInk(1, cw - 1, 0), "the corners next to the tip are empty");

        // The half circle: the same, with a rounded side.
        Assert.IsTrue(Middle(0, ch).All(y => Ink(2, 0, y)) && Middle(ch, ch).All(y => Ink(2, 0, y)), "the half circle's flat side");
        Assert.IsTrue(Reaches(2, 0, 0) && Reaches(2, 0, 1), "the half circle does not reach the top and bottom of its cell");
        Assert.IsTrue(AnyInk(2, cw - 1, ch / 2) || AnyInk(2, cw - 1, ch / 2 - 1), "the half circle reaches the right edge");
        Assert.IsFalse(AnyInk(2, cw - 1, 0), "the half circle does not fill the corner");

        // The left-pointing triangle is the mirror image of the right-pointing one.
        Assert.IsTrue(Middle(0, ch).All(y => Ink(3, cw - 1, y)) && Reaches(3, cw - 1, 0), "the left-pointing triangle's flat side");

        // The slash goes from the lower left to the upper right of its cell.
        Assert.IsTrue(Enumerable.Range(0, 3).Any(d => AnyInk(4, cw - 1 - d, d)), "the slash does not reach the upper right");
        Assert.IsTrue(Enumerable.Range(0, 3).Any(d => AnyInk(4, d, ch - 1 - d)), "the slash does not reach the lower left");
    }

    /// <summary>The rows of the area in which a pixel has the text colour (the cursor has its own colour, and is not text).</summary>
    private static int InkRows(uint[] px, int width, int firstRow, int rows)
    {
        int count = 0;
        for (int y = firstRow; y < firstRow + rows; y++)
        {
            count += Enumerable.Range(0, width).Any(x => px[y * width + x] == Foreground) ? 1 : 0;
        }

        return count;
    }

    private static int InkWidth(uint[] px, int width, int firstRow, int rows)
    {
        int first = -1, last = -1;
        for (int x = 0; x < width; x++)
        {
            if (Enumerable.Range(firstRow, rows).Any(y => px[y * width + x] == Foreground))
            {
                first = first < 0 ? x : first;
                last = x;
            }
        }

        return first < 0 ? 0 : last - first + 1;
    }

    [TestMethod]
    public void A_double_width_line_has_glyphs_twice_as_wide()
    {
        // ESC # 6 makes the line double width. The same letter on a normal line, as the reference.
        using var screen = new TextScreen(Font, 20f, "H\r\n\u001b#6H", "H\nH");
        int cw = screen.CellWidth, ch = screen.CellHeight, width = 6 * cw;
        uint[] px = screen.Snapshot(width, 2 * ch);
        int normal = InkWidth(px, width, 0, ch);
        int wide = InkWidth(px, width, ch, ch);
        Assert.IsGreaterThan(0, normal);
        Assert.IsGreaterThanOrEqualTo(normal * 17 / 10, wide, $"the letter is {wide} px wide on the double-width line and {normal} px on the normal one");
        Assert.IsLessThanOrEqualTo(normal * 23 / 10, wide, $"{wide} px against {normal}");
        Assert.AreEqual(InkRows(px, width, 0, ch), InkRows(px, width, ch, ch), 1, "a double-width line is not higher");
    }

    [TestMethod]
    public void A_double_height_line_pair_shows_the_two_halves_of_a_glyph_twice_the_size()
    {
        // ESC # 3 and ESC # 4 mark the top and bottom half of a double-height line; the same text goes on both.
        using var screen = new TextScreen(Font, 20f, "H\r\n\u001b#3H\r\n\u001b#4H", "H\nH\nH");
        int cw = screen.CellWidth, ch = screen.CellHeight, width = 6 * cw;
        uint[] px = screen.Snapshot(width, 3 * ch);
        int normal = InkRows(px, width, 0, ch);
        int doubled = InkRows(px, width, ch, 2 * ch);
        Assert.IsGreaterThan(0, normal);
        Assert.IsGreaterThanOrEqualTo(normal * 18 / 10, doubled, $"the letter is {doubled} rows high over two lines and {normal} on a normal one");
        Assert.IsLessThanOrEqualTo(normal * 22 / 10, doubled, $"{doubled} rows against {normal}");

        // The halves meet: some pixel column is inked on the last row of the top line and the first row of the bottom line.
        int boundary = 2 * ch;
        Assert.IsTrue(Enumerable.Range(0, width).Any(x => px[(boundary - 1) * width + x] == Foreground && px[boundary * width + x] == Foreground),
            "the two halves of the letter do not meet");
        Assert.IsTrue(InkRows(px, width, ch, ch) > 0 && InkRows(px, width, 2 * ch, ch) > 0, "both rows show part of the letter");
    }

    [TestMethod]
    public void Use_system_colours_takes_the_foreground_and_background_from_Windows()
    {
        using var registry = new RegistrySandbox();
        using var screen = new TextScreen(Font, 20f, "█ ", "█", registry, ("UseSystemColours", 1));

        uint text = Rgb(GetSysColor(8 /* COLOR_WINDOWTEXT */));
        uint window = Rgb(GetSysColor(5 /* COLOR_WINDOW */));
        Assert.AreEqual(text, screen.Pixel(1, 1), "the foreground is the system's window text colour");
        Assert.AreEqual(window, screen.Pixel(screen.CellWidth + 1, 1), "the background is the system's window colour");
    }
}
