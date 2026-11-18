namespace PuTTY.Sessions;

/// <summary>
/// What the About dialog says, in one place so that a test can pin the wording. PuTTY Modern is its author's project and a large
/// share of what runs is Simon Tatham's PuTTY code, so the text names both, says that PuTTY is used under its own licence and
/// that this is an independent project (the wording was reviewed as a licensing question: see the bead putty-modern-d26).
/// The licence text the dialog shows is the one the installers show, made from the same files: see <see cref="LicencesText"/>.
/// The copyright sign and the en dash are written as themselves (UTF-8): edit this file with a tool that writes them literally,
/// not through a Windows PowerShell 5.1 script (it reads a script without a BOM as ANSI); <c>AboutTextTests</c> catch a damaged one.
/// </summary>
public static class AboutText
{
    /// <summary>The product's name, as every text that is not a window title writes it.</summary>
    public const string Product = "PuTTY Modern";

    /// <summary>The author's copyright line (U+00A9 is the copyright sign).</summary>
    public const string Copyright = "Copyright © 2026 Andrij Abyzov";

    /// <summary>What the program is and what of PuTTY it contains. "Core", not "backend": in PuTTY a backend is only the protocol layer.</summary>
    public const string Summary =
        "A Windows front end for PuTTY. It includes PuTTY's core (SSH, Telnet, Rlogin, raw and serial connections, the terminal " +
        "emulator and the settings) unmodified, and keyboard and drawing code adapted from PuTTY for Windows.";

    /// <summary>Simon Tatham's credit, as its own sentence, and the statement that PuTTY Modern is independent of the PuTTY team.</summary>
    public const string Credit =
        "PuTTY is copyright © 1997–2026 Simon Tatham and the PuTTY contributors, and is used under the MIT licence. " +
        "PuTTY Modern is an independent project, not made or endorsed by them.";

    /// <summary>The official PuTTY's home page.</summary>
    public const string OfficialPuttyUrl = "https://www.chiark.greenend.org.uk/~sgtatham/putty/";

    /// <summary>
    /// The text under Licences: PuTTY Modern's own licence, then the notices file (PuTTY's licence word for word and the other
    /// components'), then the .NET runtime's own third-party notices. The installers show the first two (<c>installer\scripts\MakeNoticesRtf.ps1</c>
    /// writes them as RTF; keep the order the same).
    /// </summary>
    public static string LicencesText(string ownLicence, string notices, string dotnetNotices) =>
        string.Join("\n\n",
            Product + "\n" + new string('=', Product.Length),
            Reflow(ownLicence),
            Reflow(notices),
            ".NET runtime: its own third-party notices\n=========================================",
            Normalize(dotnetNotices));   // the runtime's own file as it is: its layout (lists, long lines) is not ours to change

    /// <summary>
    /// The licence files are hard-wrapped (at 70 to 100 columns), which looks ragged in a box of another width. This joins the lines
    /// of a paragraph (every word stays, only the line breaks inside a paragraph go): a blank line, a heading (a line with a ====
    /// or ---- line under it, which stays with its underline) , a list item (a line starting with -, * or a number) or a smaller
    /// indent starts a new paragraph. <c>installer\Common\MakeNoticesRtf.ps1</c> does the same for the installers: keep them alike.
    /// </summary>
    public static string Reflow(string text)
    {
        string[] lines = text.Replace("\r\n", "\n").Split('\n');
        var result = new System.Text.StringBuilder();
        string? paragraph = null;
        int paragraphIndent = 0;

        void Flush()
        {
            if (paragraph is not null)
            {
                result.Append(paragraph).Append("\n\n");
                paragraph = null;
            }
        }

        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i];
            string trimmed = line.Trim();
            string next = i + 1 < lines.Length ? lines[i + 1].Trim() : "";

            if (trimmed.Length == 0 || IsRule(trimmed))
            {
                Flush();                                    // a blank line ends a paragraph; a rule nothing used is dropped
                continue;
            }

            if (IsRule(next))
            {
                Flush();
                result.Append(trimmed).Append('\n').Append(next).Append("\n\n");      // a heading with its underline
                i++;
                continue;
            }

            int indent = line.Length - line.TrimStart().Length;
            if (paragraph is not null && !IsItem(trimmed) && indent >= paragraphIndent)
            {
                paragraph += " " + trimmed;
            }
            else
            {
                Flush();
                paragraph = new string(' ', Math.Min(indent, 8)) + trimmed;
                paragraphIndent = indent;
            }
        }

        Flush();
        return result.ToString().TrimEnd('\n');
    }

    private static bool IsRule(string trimmed) => trimmed.Length >= 3 && trimmed.All(c => c is '=' or '-');

    private static bool IsItem(string trimmed) =>
        (trimmed.Length > 1 && trimmed[0] is '-' or '*' && trimmed[1] == ' ') ||
        System.Text.RegularExpressions.Regex.IsMatch(trimmed, @"^\d+[.)]\s");

    private static string Normalize(string text) => text.Replace("\r\n", "\n").Trim('\n');
}
