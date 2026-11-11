namespace PuTTY.App.Settings;

/// <summary>
/// The words of the Figma sketch (frames "Session configuration" 9:3 and "Terminal preferences" 9:4) where
/// PuTTY's own differ, as a layer over the generic renderer: PuTTY's group titles are whole sentences ("Specify
/// the destination you want to connect to"), the sketch has short card titles with a line of explanation. A page
/// or group that is not listed here keeps PuTTY's own text, so a page the sketch does not draw is still complete.
/// </summary>
/// <remarks>
/// Cards are keyed by the page's path and PuTTY's own group title, which the engine reports unchanged
/// (<c>config.c</c>'s <c>ctrl_getset</c> calls); a group with no title is keyed by the empty string. A group can
/// become several cards (the Answerback box is a card of its own in the sketch, though PuTTY puts it with the
/// terminal options) by naming the label of the control that starts the next card.
/// </remarks>
internal static class Presentation
{
    /// <summary>One card: where it starts in its group, what it says, and how its control sits.</summary>
    /// <param name="FromLabel">The label (as PuTTY words it, without accelerator marks) of the first control of the card; null for the first card of a group.</param>
    /// <param name="Title">The card's title; empty for none.</param>
    /// <param name="Subtitle">A line under the title, or null.</param>
    /// <param name="Side">True: the title and line are on the left and the control on the right, as for a single setting.</param>
    /// <param name="HiddenLabels">Labels of controls whose own label would repeat the card's title.</param>
    internal sealed record Card(string? FromLabel, string Title, string? Subtitle = null, bool Side = false, string[]? HiddenLabels = null);

    private static readonly Dictionary<string, string> PageSubtitles = new(StringComparer.Ordinal)
    {
        ["Session"] = "Connect to a host or manage your saved sessions.",
        ["Terminal"] = "Control terminal emulation, input and remote printing.",
    };

    private static readonly Dictionary<(string Page, string Group), Card[]> Cards = new()
    {
        [("Session", "Specify the destination you want to connect to")] = [new Card(null, "Connection")],
        [("Session", "Load, save or delete a stored session")] =
        [
            new Card(null, "Saved sessions", "Load an existing profile or save this connection for later.", HiddenLabels: ["Saved Sessions"]),
        ],
        [("Session", string.Empty)] =
        [
            new Card(null, "Close window on exit", "Choose what happens when the connection ends.", HiddenLabels: ["Close window on exit:"]),
        ],
        [("Terminal", "Set various terminal options")] =
        [
            new Card(null, "Terminal emulation"),
            new Card("Answerback to ^E:", "Answerback to ^E", "Response sent when the remote host requests identification.", Side: true, HiddenLabels: ["Answerback to ^E:"]),
        ],
        [("Terminal", "Line discipline options")] = [new Card(null, "Line discipline")],
        [("Terminal", "Remote-controlled printing")] =
        [
            new Card(null, "Remote-controlled printing", "Printer for ANSI printer output", Side: true, HiddenLabels: ["Printer to send ANSI printer output to:"]),
        ],
    };

    private static readonly Dictionary<string, string> Captions = new(StringComparer.Ordinal)
    {
        ["Host Name (or IP address)"] = "Host name (or IP address)",
    };

    /// <summary>The sketch's line under a page's title, or null to keep PuTTY's own.</summary>
    public static string? PageSubtitle(string path) => PageSubtitles.GetValueOrDefault(path);

    /// <summary>The sketch's cards for a group, or null to make one card with PuTTY's title.</summary>
    public static Card[]? CardsFor(string path, string groupTitle) => Cards.GetValueOrDefault((path, groupTitle));

    /// <summary>
    /// The text above a control as the sketch has it: no colon at the end ("Connection type", not "Connection type:"),
    /// and the few labels it words differently.
    /// </summary>
    public static string Caption(string plainLabel)
    {
        string text = plainLabel.TrimEnd();
        if (Captions.TryGetValue(text, out string? words))
        {
            return words;
        }

        return text.EndsWith(':') ? text[..^1] : text;
    }

    /// <summary>
    /// What an editable drop-down shows for the text PuTTY holds. The printer box is empty by default, because PuTTY's
    /// handler only puts "None (printing disabled)" in it for a NULL setting and the default is an empty string; the
    /// sketch shows the words, which are also the first entry of the list and which PuTTY turns back into "no printer"
    /// when chosen. Nothing is written: this is only what is shown.
    /// </summary>
    public static string ShownText(string plainLabel, string text) =>
        text.Length == 0 && plainLabel == "Printer to send ANSI printer output to:" ? "None (printing disabled)" : text;

    /// <summary>True for the list of saved sessions: its rows carry the host and protocol, as in the sketch.</summary>
    public static bool IsSavedSessionList(string path, string groupTitle) =>
        path == "Session" && groupTitle == "Load, save or delete a stored session";
}
