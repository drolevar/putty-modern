using PuTTY.Interop;

namespace PuTTY.Interop.Tests;

/// <summary>
/// The terminal renderer, driven offscreen (a software WIC bitmap): no window, no GPU, no engine.
/// It checks the grid the renderer promises: a cell size from the font, the background filled, each
/// glyph drawn inside its own cell and only there.
/// </summary>
[TestClass]
public sealed class RenderTests
{
    private const string Font = "Consolas";

    [TestMethod]
    public void The_cell_size_comes_from_the_font_and_scales_with_it()
    {
        var small = PuttyEngine.Diagnostics.RenderText(Font, 14, "M", 0xFFFFFF, 0x000000);
        var large = PuttyEngine.Diagnostics.RenderText(Font, 28, "M", 0xFFFFFF, 0x000000);

        Assert.IsTrue(small.CellWidth >= 5 && small.CellHeight >= 10, $"cell {small.CellWidth}x{small.CellHeight}");
        Assert.IsTrue(large.CellWidth > small.CellWidth && large.CellHeight > small.CellHeight);
    }

    [TestMethod]
    public void The_background_is_filled_and_each_glyph_lands_in_its_own_cell()
    {
        var probe = PuttyEngine.Diagnostics.RenderText(Font, 20, " W i", 0xFFFFFF, 0x102030);

        Assert.AreEqual(0x102030u, probe.CornerRgb, "the background colour");
        Assert.AreEqual(4, probe.Ink.Length);
        Assert.AreEqual(0, probe.Ink[0], "an empty cell must stay background");
        Assert.AreEqual(0, probe.Ink[2], "an empty cell must stay background");
        Assert.IsTrue(probe.Ink[1] > 30, $"W drew {probe.Ink[1]} pixels");
        Assert.IsTrue(probe.Ink[3] > 5, $"i drew {probe.Ink[3]} pixels");
        Assert.IsTrue(probe.Ink[1] > probe.Ink[3], "a W has more ink than an i");
    }

    [TestMethod]
    public void Bold_draws_more_ink_than_regular()
    {
        var regular = PuttyEngine.Diagnostics.RenderText(Font, 20, "H", 0xFFFFFF, 0x000000);
        var bold = PuttyEngine.Diagnostics.RenderText(Font, 20, "H", 0xFFFFFF, 0x000000, bold: true);

        Assert.AreEqual(regular.CellWidth, bold.CellWidth, "bold must not change the cell");
        Assert.IsTrue(bold.Ink[0] > regular.Ink[0], $"bold {bold.Ink[0]} vs regular {regular.Ink[0]}");
    }

    [TestMethod]
    public void A_font_that_is_not_installed_is_refused()
    {
        Assert.Throws<InvalidOperationException>(
            () => PuttyEngine.Diagnostics.RenderText("No Such Font Family 42", 14, "M", 0xFFFFFF, 0x000000));
    }
}
