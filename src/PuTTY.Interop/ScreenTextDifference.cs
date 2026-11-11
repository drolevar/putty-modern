namespace PuTTY.Interop;

/// <summary>
/// What was added to the terminal's screen text between two looks at it, for announcing to a screen reader: text appended
/// to what was there (typing, output that did not scroll), or the new lines when the output scrolled. Nothing for anything
/// else (a cleared screen, a full-screen program redrawing: announcing the whole screen again would only be noise).
/// </summary>
public static class ScreenTextDifference
{
    public static string Added(string before, string after)
    {
        if (after.Length == 0 || string.Equals(before, after, StringComparison.Ordinal))
        {
            return string.Empty;
        }

        // The usual case: the same text with more at the end.
        if (after.StartsWith(before, StringComparison.Ordinal))
        {
            return after[before.Length..].Trim();
        }

        // Output scrolled: the lines that remain of the old text are the first lines of the new text; the rest is new.
        // The old last line is left out of the comparison, because it is the one that was still growing.
        string[] oldLines = before.Split('\n');
        string[] newLines = after.Split('\n');
        for (int scrolled = 1; scrolled < oldLines.Length; scrolled++)
        {
            int overlap = oldLines.Length - scrolled - 1;      // old lines kept, not counting the last one
            if (overlap < 1 || overlap > newLines.Length)
            {
                continue;
            }

            bool same = true;
            for (int i = 0; i < overlap; i++)
            {
                if (!string.Equals(oldLines[scrolled + i], newLines[i], StringComparison.Ordinal))
                {
                    same = false;
                    break;
                }
            }

            if (same)
            {
                return string.Join("\n", newLines[overlap..]).Trim();
            }
        }

        return string.Empty;
    }
}
