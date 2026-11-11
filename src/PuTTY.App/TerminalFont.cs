using PuTTY.Interop;

namespace PuTTY.App;

/// <summary>
/// Which font a terminal uses. The session's own setting (PuTTY's Window > Appearance, saved with the session) wins,
/// except when it is PuTTY's built-in default (Courier New, 10 point): that is what every session has that never
/// chose one, and the app's own choice, Cascadia Mono (which ships with Windows 11) or Consolas, looks better and
/// matches the rest of the app. A font that is not installed falls back to those two.
/// </summary>
internal static class TerminalFont
{
    /// <summary>The size when the session does not say: 15 device-independent pixels, 11.25 points.</summary>
    internal const float DefaultDips = 15.0f;

    // Tried in order; Cascadia Mono ships with Windows 11 and Terminal, Consolas with every Windows.
    private static readonly string[] Fallbacks = ["Cascadia Mono", "Consolas"];

    /// <summary>The families to try, in order, and the size in device-independent pixels (96 to the inch).</summary>
    internal static (string[] Families, float Dips) For(PuttySession session)
    {
        try
        {
            return Resolve(session.GetFont());
        }
        catch (InvalidOperationException)
        {
            return (Fallbacks, DefaultDips);
        }
    }

    internal static (string[] Families, float Dips) Resolve(SettingsFont font)
    {
        if (string.IsNullOrWhiteSpace(font.Name) || IsPuttyDefault(font))
        {
            return (Fallbacks, DefaultDips);
        }

        // A positive height is points, a negative one pixels (as in a LOGFONT); 0 means "the default size".
        float dips = font.Height > 0 ? font.Height * 96.0f / 72.0f : font.Height < 0 ? -font.Height : DefaultDips;
        return ([font.Name, .. Fallbacks], dips);
    }

    private static bool IsPuttyDefault(SettingsFont font) =>
        string.Equals(font.Name, "Courier New", StringComparison.OrdinalIgnoreCase) && font.Height == 10 && !font.Bold;
}
