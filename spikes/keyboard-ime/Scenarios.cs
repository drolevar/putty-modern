using static KeyboardIme.Native;

namespace KeyboardIme;

/// <summary>A named run of taps. <see cref="TextBox"/> marks groups whose text the proxy TextBox is meant to supply.</summary>
internal sealed record Scenario(string Group, string[] Taps, bool TextBox = false);

internal static unsafe class Scenarios
{
    public static string[] TypingNames => Typing;

    private static readonly string[] Typing =
    [
        .. "qwertyuiop".Select(c => c.ToString()),
        .. "asdfghjkl".Select(c => c.ToString()),
        .. "zxcvbnm".Select(c => c.ToString()),
        .. "1234567890".Select(c => c.ToString()),
        "minus", "equal", "lbracket", "rbracket", "semicolon", "quote", "grave", "backslash", "comma", "period", "slash", "oem102",
    ];

    private static readonly string[] Navigation =
        ["up", "down", "left", "right", "home", "end", "pgup", "pgdn", "insert", "delete"];

    private static readonly string[] Numpad =
        ["num0", "num1", "num2", "num3", "num4", "num5", "num6", "num7", "num8", "num9",
         "numdot", "numplus", "numminus", "nummul", "numdiv", "numenter"];

    public static IEnumerable<Scenario> For(nint hkl, string layout, bool quick)
    {
        if (layout.StartsWith("E0", StringComparison.OrdinalIgnoreCase))
        {
            // An IME is the active input method (E0xx = a text service profile). Romaji or pinyin goes in
            // on the ordinary keys; Enter commits the composition, Space converts it.
            yield return new("ime-compose", ["a", "@wait:300", "enter", "@wait:300",
                "k", "a", "@wait:300", "enter", "@wait:300",
                "n", "i", "h", "a", "o", "@wait:300", "space", "@wait:300", "enter", "@wait:300",
                "x", "@wait:300", "esc", "@wait:300", "a", "@wait:300", "enter"], TextBox: true);
            yield break;
        }

        string[] typing = quick ? Typing.Where((_, i) => i % 4 == 0).ToArray() : Typing;

        // Every typing key under every modifier combination that matters to a terminal.
        yield return new("sweep-none", typing, TextBox: true);
        yield return new("sweep-shift", typing.Select(k => $"shift+{k}").ToArray(), TextBox: true);
        yield return new("sweep-altgr", typing.Select(k => $"ctrl+altgr+{k}").ToArray(), TextBox: true);   // as the driver sends it: Ctrl, then the right Alt
        yield return new("sweep-ctrl", typing.Select(k => $"ctrl+{k}").ToArray());
        yield return new("sweep-alt", typing.Select(k => $"alt+{k}").ToArray());
        yield return new("sweep-ctrlalt", typing.Select(k => $"ctrl+alt+{k}").ToArray());

        yield return new("nav", [.. Navigation, .. Navigation.Select(k => $"shift+{k}"),
            .. Navigation.Select(k => $"ctrl+{k}"), .. Navigation.Select(k => $"alt+{k}")]);
        yield return new("fkeys", [.. Enumerable.Range(1, 12).Select(i => $"f{i}"),
            "shift+f1", "ctrl+f1", "alt+f1", "shift+f5", "ctrl+f5", "shift+f10", "ctrl+f12"]);

        // The numeric keypad with NumLock on (digits) and off (navigation keys on the same scan codes).
        yield return new("numpad-on", ["@numlock:on", .. Numpad, "shift+num1", "ctrl+num1", "alt+num1"]);
        yield return new("numpad-off", ["@numlock:off", .. Numpad, "shift+num1", "ctrl+num1", "alt+num1", "@numlock:on"]);

        // Keys that Windows or XAML may claim for themselves. Each one that can open a menu is followed
        // by Esc and an ordinary key, so the next entry shows whether the control still gets input.
        yield return new("edit", ["enter", "backspace", "tab", "shift+tab", "ctrl+tab", "esc", "space", "shift+space",
            "ctrl+space", "alt+enter",
            "f10", "@wait:200", "esc", "a",
            "lalt", "@wait:200", "esc", "a",
            "apps", "@wait:200", "esc", "a",
            "shift+f10", "@wait:200", "esc", "a",
            "alt+space", "@wait:200", "esc", "esc", "a"]);

        yield return new("altnum", ["@numlock:on", "@altnum:0233", "@altnum:233", "@altnum:65"]);
        yield return new("unicode", ["@unicode:00E9", "@unicode:65E5", "@unicode:D83D,DE00"], TextBox: true);
        yield return new("repeat", ["@hold:a:5", "@hold:left:4"]);
        yield return new("caps", ["@capslock:on", "a", "shift+a", "@capslock:off", "a"], TextBox: true);

        // Dead keys: found by asking the layout, then each is followed by letters, space and itself.
        foreach (var (scan, state, token) in DeadKeys(hkl).GroupBy(d => (d.scan, d.state)).Select(g => g.First()).Take(quick ? 2 : 6))
        {
            yield return new($"dead-{scan:X2}-{state}",
                [token, "a", token, "space", token, token, token, "e", "a"], TextBox: true);
        }
    }

    /// <summary>Keys that produce no character on their own in this layout (ToUnicodeEx returns a negative count).</summary>
    public static List<(int scan, string state, string token)> DeadKeys(nint hkl)
    {
        var found = new List<(int, string, string)>();
        byte* st = stackalloc byte[256];
        char* buffer = stackalloc char[8];
        foreach (var (stateName, prefix) in new[] { ("none", ""), ("shift", "shift+"), ("altgr", "ctrl+altgr+") })
        {
            foreach (string name in Typing)
            {
                K key = Injector.Keys[name];
                new Span<byte>(st, 256).Clear();
                if (stateName == "shift") { st[VK_SHIFT] = 0x80; }
                if (stateName == "altgr") { st[VK_CONTROL] = 0x80; st[VK_MENU] = 0x80; st[VK_LCONTROL] = 0x80; st[VK_RMENU] = 0x80; }

                uint vk = MapVirtualKeyExW(key.Scan, 3 /* MAPVK_VSC_TO_VK_EX */, hkl);
                // Flag 4 (Windows 10 1607 and later): query only, leave the dead-key state alone.
                int r = ToUnicodeEx(vk, key.Scan, st, buffer, 8, 4, hkl);
                if (r < 0)
                {
                    found.Add((key.Scan, stateName, prefix + name));
                }
            }
        }

        return found;
    }
}
