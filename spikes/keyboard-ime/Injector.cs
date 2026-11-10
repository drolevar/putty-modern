using static KeyboardIme.Native;

namespace KeyboardIme;

/// <summary>A physical key: scan code (set 1) plus the extended-key prefix (E0).</summary>
internal readonly record struct K(ushort Scan, bool Ext);

/// <summary>
/// Sends scan-code keystrokes with SendInput so the whole real pipeline runs (keyboard layout,
/// dead-key state, TranslateMessage, TSF). SendInput goes to whatever window is in the
/// foreground, so every key is guarded: the spike window must be the foreground window and the
/// expected XAML element must hold focus, otherwise the run aborts and releases everything.
/// </summary>
internal sealed class Injector(nint topHwnd, Func<bool> focusOk, Func<Task<bool>> recoverFocus)
{
    private const int InputSize = 40;   // sizeof(INPUT) on x64

    /// <summary>
    /// Layout of the keys being typed. With <see cref="UseVk"/> the virtual key for each typing key
    /// is computed from this layout and sent as wVk, because injecting bare scan codes was found
    /// to be converted to virtual keys with a different layout than the one the thread has active.
    /// </summary>
    public nint Hkl { get; set; }

    public bool UseVk { get; set; }

    public int Recoveries { get; private set; }

    private static HashSet<K>? typingKeys;

    /// <summary>Letters, digits and OEM punctuation: the keys whose virtual key depends on the layout.</summary>
    private static HashSet<K> TypingKeys => typingKeys ??= Scenarios.TypingNames.Select(n => Keys[n]).ToHashSet();

    public const int HoldMs = 12;
    public const int GapMs = 8;
    public const int SettleMs = 35;

    public static readonly Dictionary<string, K> Keys = BuildKeys();
    private static readonly Dictionary<K, string> Names = Keys.GroupBy(p => p.Value).ToDictionary(g => g.Key, g => g.First().Key);

    private readonly HashSet<K> down = [];

    public string? AbortReason { get; private set; }

    public bool Aborted => AbortReason is not null;

    public int KeysSent { get; private set; }

    public static string NameOf(K key) => Names.TryGetValue(key, out string? n) ? n : $"sc{key.Scan:X2}{(key.Ext ? "e" : "")}";

    private static Dictionary<string, K> BuildKeys()
    {
        var d = new Dictionary<string, K>(StringComparer.OrdinalIgnoreCase);
        void Add(string name, int scan, bool ext = false) => d[name] = new K((ushort)scan, ext);

        // Letters and digits by physical position on a US QWERTY keyboard.
        string rows = "qwertyuiop";
        for (int i = 0; i < rows.Length; i++) Add(rows[i].ToString(), 0x10 + i);
        rows = "asdfghjkl";
        for (int i = 0; i < rows.Length; i++) Add(rows[i].ToString(), 0x1E + i);
        rows = "zxcvbnm";
        for (int i = 0; i < rows.Length; i++) Add(rows[i].ToString(), 0x2C + i);
        for (int i = 1; i <= 9; i++) Add(i.ToString(), 0x01 + i);
        Add("0", 0x0B);

        Add("minus", 0x0C); Add("equal", 0x0D); Add("lbracket", 0x1A); Add("rbracket", 0x1B);
        Add("semicolon", 0x27); Add("quote", 0x28); Add("grave", 0x29); Add("backslash", 0x2B);
        Add("comma", 0x33); Add("period", 0x34); Add("slash", 0x35); Add("oem102", 0x56);

        Add("esc", 0x01); Add("backspace", 0x0E); Add("tab", 0x0F); Add("enter", 0x1C); Add("space", 0x39);
        Add("capslock", 0x3A); Add("numlock", 0x45);
        for (int i = 0; i < 10; i++) Add($"f{i + 1}", 0x3B + i);
        Add("f11", 0x57); Add("f12", 0x58);

        Add("num7", 0x47); Add("num8", 0x48); Add("num9", 0x49); Add("numminus", 0x4A);
        Add("num4", 0x4B); Add("num5", 0x4C); Add("num6", 0x4D); Add("numplus", 0x4E);
        Add("num1", 0x4F); Add("num2", 0x50); Add("num3", 0x51); Add("num0", 0x52); Add("numdot", 0x53);
        Add("nummul", 0x37); Add("numdiv", 0x35, true); Add("numenter", 0x1C, true);

        Add("insert", 0x52, true); Add("delete", 0x53, true); Add("home", 0x47, true); Add("end", 0x4F, true);
        Add("pgup", 0x49, true); Add("pgdn", 0x51, true); Add("up", 0x48, true); Add("down", 0x50, true);
        Add("left", 0x4B, true); Add("right", 0x4D, true); Add("apps", 0x5D, true);

        Add("lshift", 0x2A); Add("rshift", 0x36); Add("lctrl", 0x1D); Add("rctrl", 0x1D, true);
        Add("lalt", 0x38); Add("ralt", 0x38, true);
        return d;
    }

    private static K Mod(string name) => name.ToLowerInvariant() switch
    {
        "shift" => Keys["lshift"],
        "rshift" => Keys["rshift"],
        "ctrl" => Keys["lctrl"],
        "rctrl" => Keys["rctrl"],
        "alt" => Keys["lalt"],
        "altgr" => Keys["ralt"],
        _ => throw new ArgumentException($"unknown modifier '{name}'"),
    };

    private bool Guard()
    {
        if (Aborted)
        {
            return false;
        }

        nint foreground = GetForegroundWindow();
        if (foreground != topHwnd)
        {
            Abort($"foreground window changed (0x{foreground:X}, expected 0x{topHwnd:X})");
            return false;
        }

        if (!focusOk())
        {
            Abort("XAML focus left the expected element");
            return false;
        }

        return true;
    }

    private void Abort(string reason)
    {
        AbortReason = reason;
        ReleaseAll();
    }

    private void SendScan(K key, bool up)
    {
        INPUT input = default;
        input.type = 1;
        input.wScan = key.Scan;
        uint vk = UseVk && Hkl != 0 && TypingKeys.Contains(key) ? MapVirtualKeyExW(key.Scan, 3 /* MAPVK_VSC_TO_VK_EX */, Hkl) : 0;
        if (vk != 0)
        {
            input.wVk = (ushort)vk;
            input.dwFlags = (key.Ext ? KEYEVENTF_EXTENDEDKEY : 0) | (up ? KEYEVENTF_KEYUP : 0);
        }
        else
        {
            input.dwFlags = KEYEVENTF_SCANCODE | (key.Ext ? KEYEVENTF_EXTENDEDKEY : 0) | (up ? KEYEVENTF_KEYUP : 0);
        }

        unsafe { SendInput(1, &input, InputSize); }
        KeysSent++;
    }

    private bool Down(K key)
    {
        if (!Guard())
        {
            return false;
        }

        SendScan(key, up: false);
        down.Add(key);
        return true;
    }

    /// <summary>Releasing is always allowed, even after an abort: it must never leave a key stuck.</summary>
    private void Up(K key)
    {
        SendScan(key, up: true);
        down.Remove(key);
    }

    public void ReleaseAll()
    {
        foreach (K key in down.ToArray())
        {
            Up(key);
        }
    }

    private bool SendUnicode(ushort unit)
    {
        if (!Guard())
        {
            return false;
        }

        INPUT input = default;
        input.type = 1;
        input.wScan = unit;
        input.dwFlags = KEYEVENTF_UNICODE;
        unsafe { SendInput(1, &input, InputSize); }
        input.dwFlags = KEYEVENTF_UNICODE | KEYEVENTF_KEYUP;
        unsafe { SendInput(1, &input, InputSize); }
        KeysSent += 2;
        return true;
    }

    /// <summary>
    /// One token: "key", "mod+mod+key", or "@command:args" for hold, unicode, numlock, capslock,
    /// altnum and wait.
    /// </summary>
    public async Task Tap(string token)
    {
        if (Aborted)
        {
            return;
        }

        // A previous key may have opened something that took focus (a menu, a flyout): get it back.
        if (!focusOk() && GetForegroundWindow() == topHwnd && await recoverFocus())
        {
            Recoveries++;
            Rec.Add("harness", "focus-recovered", extra: Rec.Step);
        }

        if (token.StartsWith('@'))
        {
            await Command(token[1..]);
            await Task.Delay(SettleMs);
            return;
        }

        string[] parts = token.Split('+');
        K key = Keys[parts[^1]];
        K[] mods = parts[..^1].Select(Mod).ToArray();

        foreach (K m in mods)
        {
            if (!Down(m)) { return; }
            await Task.Delay(GapMs);
        }

        if (Down(key))
        {
            await Task.Delay(HoldMs);
            Up(key);
        }

        await Task.Delay(GapMs);
        foreach (K m in mods.Reverse())
        {
            Up(m);
            await Task.Delay(GapMs);
        }

        await Task.Delay(SettleMs);
    }

    private async Task Command(string command)
    {
        string[] a = command.Split(':');
        switch (a[0])
        {
            case "hold":      // a:key:count, auto-repeat: repeated key-downs, then one key-up
            {
                K key = Keys[a[1]];
                int count = int.Parse(a[2]);
                for (int i = 0; i < count; i++)
                {
                    if (!Down(key)) { return; }
                    await Task.Delay(25);
                }

                Up(key);
                break;
            }

            case "unicode":   // hex UTF-16 code units, comma separated, sent as VK_PACKET
                foreach (string unit in a[1].Split(','))
                {
                    if (!SendUnicode(Convert.ToUInt16(unit, 16))) { return; }
                    await Task.Delay(GapMs);
                }

                await Task.Delay(150);

                break;

            case "numlock":
                await Toggle("numlock", VK_NUMLOCK, a[1] == "on");
                break;

            case "capslock":
                await Toggle("capslock", VK_CAPITAL, a[1] == "on");
                break;

            case "altnum":    // hold left Alt, type the digits on the numeric keypad, release Alt
            {
                K alt = Keys["lalt"];
                if (!Down(alt)) { return; }
                await Task.Delay(GapMs);
                foreach (char digit in a[1])
                {
                    K key = Keys[$"num{digit}"];
                    if (!Down(key)) { return; }
                    await Task.Delay(HoldMs);
                    Up(key);
                    await Task.Delay(GapMs);
                }

                Up(alt);
                break;
            }

            case "wait":
                await Task.Delay(int.Parse(a[1]));
                break;

            default:
                throw new ArgumentException($"unknown command '{command}'");
        }
    }

    /// <summary>
    /// Restores a lock key's state at the end of a run, even after an abort. NumLock and CapsLock
    /// do nothing harmful in any window, so this one is not guarded.
    /// </summary>
    public async Task ForceToggle(string keyName, int vk, bool on)
    {
        if (((GetKeyState(vk) & 1) != 0) != on)
        {
            K key = Keys[keyName];
            SendScan(key, up: false);
            await Task.Delay(HoldMs);
            SendScan(key, up: true);
            await Task.Delay(SettleMs);
        }
    }

    public async Task Toggle(string keyName, int vk, bool on)
    {
        bool isOn = (GetKeyState(vk) & 1) != 0;
        if (isOn != on)
        {
            await Tap(keyName);
        }
    }
}
