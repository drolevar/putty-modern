using PuTTY.Interop;

namespace PuTTY.Interop.Tests;

/// <summary>What is announced to a screen reader when the terminal's screen text changes.</summary>
[TestClass]
public sealed class ScreenTextDifferenceTests
{
    [TestMethod]
    public void Text_appended_to_the_screen_is_what_is_announced()
    {
        Assert.AreEqual("hello", ScreenTextDifference.Added("$ ", "$ hello"));
        Assert.AreEqual("two\nthree", ScreenTextDifference.Added("one", "one\ntwo\nthree"),"the lines after what was there");
    }

    [TestMethod]
    public void Output_that_scrolled_the_screen_announces_its_new_lines_only()
    {
        string before = "line1\nline2\nline3\n$ ";
        string after = "line2\nline3\nline4\nline5\n$ ";

        Assert.AreEqual("line4\nline5\n$", ScreenTextDifference.Added(before, after));
    }

    [TestMethod]
    public void A_redraw_nothing_is_announced_for()
    {
        Assert.AreEqual(string.Empty, ScreenTextDifference.Added("a long screen\nof something", "completely different"));
        Assert.AreEqual(string.Empty, ScreenTextDifference.Added("something", string.Empty), "a cleared screen");
        Assert.AreEqual(string.Empty, ScreenTextDifference.Added("same", "same"));
    }
}
