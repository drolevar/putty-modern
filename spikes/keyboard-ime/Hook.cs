using System.Runtime.InteropServices;
using static KeyboardIme.Native;

namespace KeyboardIme;

/// <summary>
/// A WH_GETMESSAGE hook on the UI thread. It sees every message the thread retrieves before it
/// is translated or dispatched, so it records the raw WM_KEYDOWN/WM_CHAR/WM_IME_* stream. When
/// <see cref="Swallow"/> is on and the terminal has focus it also behaves like PuTTY's window
/// procedure: it handles key messages itself (calling ToUnicodeEx exactly as TranslateKey does)
/// and turns them into WM_NULL, so TranslateMessage never runs and XAML never sees them.
/// </summary>
internal static unsafe class Hook
{
    public static bool Swallow;
    public static bool FocusIsTerminal;
    public static int Calls;

    private static nint handle;
    private static readonly Dictionary<nint, string> ClassNames = [];

    public static bool Install()
    {
        handle = SetWindowsHookExW(WH_GETMESSAGE, &Proc, 0, GetCurrentThreadId());
        return handle != 0;
    }

    public static void Remove()
    {
        if (handle != 0)
        {
            UnhookWindowsHookEx(handle);
            handle = 0;
        }

        if (sentHandle != 0)
        {
            UnhookWindowsHookEx(sentHandle);
            sentHandle = 0;
        }
    }

    private static nint sentHandle;

    /// <summary>WH_CALLWNDPROC (4): sees messages that are sent, not posted, such as the WM_IME_* family.</summary>
    public static bool InstallSent()
    {
        sentHandle = SetWindowsHookExW(4, &SentProc, 0, GetCurrentThreadId());
        return sentHandle != 0;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CWPSTRUCT
    {
        public nint lParam;
        public nuint wParam;
        public uint message;
        public nint hwnd;
    }

    [UnmanagedCallersOnly]
    private static nint SentProc(int code, nint wParam, nint lParam)
    {
        try
        {
            if (code == 0)
            {
                CWPSTRUCT* c = (CWPSTRUCT*)lParam;
                if (c->message is WM_IME_STARTCOMPOSITION or WM_IME_ENDCOMPOSITION or WM_IME_COMPOSITION or WM_IME_SETCONTEXT or WM_IME_NOTIFY or WM_IME_CHAR or WM_INPUTLANGCHANGE)
                {
                    Rec.Add("sent", Name(c->message), extra: $"{Class(c->hwnd)} wp=0x{c->wParam:X} lp=0x{c->lParam:X}");
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"sent hook: {ex}");
        }

        return CallNextHookEx(0, code, wParam, lParam);
    }

    [UnmanagedCallersOnly]
    private static nint Proc(int code, nint wParam, nint lParam)
    {
        try
        {
            if (code == 0 && wParam == 1 /* PM_REMOVE */)
            {
                Calls++;
                Handle((MSG*)lParam);
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"hook: {ex}");
        }

        return CallNextHookEx(0, code, wParam, lParam);
    }

    private static string Class(nint hwnd)
    {
        if (!ClassNames.TryGetValue(hwnd, out string? name))
        {
            name = ClassName(hwnd);
            ClassNames[hwnd] = name;
        }

        return name;
    }

    private static bool IsKey(uint m) => m is WM_KEYDOWN or WM_KEYUP or WM_SYSKEYDOWN or WM_SYSKEYUP;

    private static bool IsChar(uint m) => m is WM_CHAR or WM_DEADCHAR or WM_SYSCHAR or WM_SYSDEADCHAR or WM_UNICHAR;

    private static bool IsIme(uint m) => m is WM_IME_STARTCOMPOSITION or WM_IME_ENDCOMPOSITION or WM_IME_COMPOSITION
        or WM_IME_SETCONTEXT or WM_IME_NOTIFY or WM_IME_CHAR or WM_IME_KEYDOWN;

    private static string Name(uint m) => m switch
    {
        WM_KEYDOWN => "WM_KEYDOWN",
        WM_KEYUP => "WM_KEYUP",
        WM_SYSKEYDOWN => "WM_SYSKEYDOWN",
        WM_SYSKEYUP => "WM_SYSKEYUP",
        WM_CHAR => "WM_CHAR",
        WM_DEADCHAR => "WM_DEADCHAR",
        WM_SYSCHAR => "WM_SYSCHAR",
        WM_SYSDEADCHAR => "WM_SYSDEADCHAR",
        WM_UNICHAR => "WM_UNICHAR",
        WM_INPUTLANGCHANGE => "WM_INPUTLANGCHANGE",
        WM_IME_STARTCOMPOSITION => "WM_IME_STARTCOMPOSITION",
        WM_IME_ENDCOMPOSITION => "WM_IME_ENDCOMPOSITION",
        WM_IME_COMPOSITION => "WM_IME_COMPOSITION",
        WM_IME_SETCONTEXT => "WM_IME_SETCONTEXT",
        WM_IME_NOTIFY => "WM_IME_NOTIFY",
        WM_IME_CHAR => "WM_IME_CHAR",
        WM_IME_KEYDOWN => "WM_IME_KEYDOWN",
        _ => $"0x{m:X}",
    };

    private static void Handle(MSG* m)
    {
        uint msg = m->message;
        nint lp = m->lParam;
        if (IsKey(msg))
        {
            Rec.Add("raw", Name(msg), vk: (int)m->wParam, scan: (int)((lp >> 16) & 0xFF), ext: ((lp >> 24) & 1) != 0,
                alt: ((lp >> 29) & 1) != 0, was: ((lp >> 30) & 1) != 0, up: ((lp >> 31) & 1) != 0,
                rep: (int)(lp & 0xFFFF), extra: Class(m->hwnd));
            if (Swallow && FocusIsTerminal && (int)m->wParam is not (VK_PROCESSKEY or VK_PACKET))
            {
                Ported.Translate(m);
                m->message = WM_NULL;
            }
        }
        else if (IsChar(msg))
        {
            Rec.Add("raw", Name(msg), ch: (int)m->wParam, scan: (int)((lp >> 16) & 0xFF), ext: ((lp >> 24) & 1) != 0,
                alt: ((lp >> 29) & 1) != 0, extra: Class(m->hwnd));
        }
        else if (IsIme(msg) || msg == WM_INPUTLANGCHANGE)
        {
            Rec.Add("raw", Name(msg), extra: $"{Class(m->hwnd)} wp=0x{m->wParam:X} lp=0x{m->lParam:X}");
        }
    }
}

/// <summary>
/// The part of PuTTY's TranslateKey (windows/window.c) that turns an ordinary key into text,
/// reproduced faithfully: GetKeyboardState, the RMENU and Ctrl-Alt adjustments, then ToUnicodeEx
/// with flags 0 on every key message, down and up, passing HIWORD(lParam) and (KF_UP|KF_EXTENDED|0xFF)
/// as the scan code. Keys that TranslateKey handles earlier with its own tables are skipped here.
/// </summary>
internal static unsafe class Ported
{
    public static bool IsSpecial(int vk) =>
        (vk >= VK_F1 && vk <= VK_F24) || (vk >= VK_PRIOR && vk <= VK_DOWN) || vk is VK_INSERT or VK_DELETE
        || (vk >= VK_NUMPAD0 && vk <= VK_DIVIDE) || vk is VK_RETURN or VK_BACK or VK_SHIFT or VK_CONTROL;

    public static void Translate(MSG* m)
    {
        int vk = (int)m->wParam;
        nint lp = m->lParam;
        uint hiword = (uint)((lp >> 16) & 0xFFFF);
        bool ext = (hiword & 0x0100) != 0;
        bool up = (hiword & 0x8000) != 0;

        byte* state = stackalloc byte[256];
        if (!GetKeyboardState(state))
        {
            new Span<byte>(state, 256).Clear();
        }
        else if (vk == VK_MENU && ext)
        {
            state[VK_RMENU] = state[VK_MENU];
        }

        bool leftAlt = (hiword & 0x2000) != 0 && (state[VK_RMENU] & 0x80) == 0;
        if (leftAlt && (state[VK_CONTROL] & 0x80) != 0)
        {
            // ctrlaltkeys is off by default: make Ctrl+Alt behave like AltGr for ToUnicodeEx.
            state[VK_RMENU] = 0x80;
            leftAlt = false;
        }

        if (IsSpecial(vk))
        {
            Rec.Add("ported", "special", vk: vk, scan: (int)(hiword & 0xFF), ext: ext, up: up, extra: $"leftAlt={leftAlt}");
            return;
        }

        uint scan = hiword & 0x81FF;
        char* buffer = stackalloc char[3];
        nint hkl = GetKeyboardLayout(0);
        int r = ToUnicodeEx((uint)vk, scan, state, buffer, 3, 0, hkl);
        if (r > 0)
        {
            for (int i = 0; i < r; i++)
            {
                Rec.Add("ported", "text", vk: vk, scan: (int)(hiword & 0xFF), ext: ext, up: up, ch: buffer[i],
                    extra: $"r={r} leftAlt={leftAlt}");
            }
        }
        else
        {
            Rec.Add("ported", r < 0 ? "dead" : "none", vk: vk, scan: (int)(hiword & 0xFF), ext: ext, up: up,
                extra: $"r={r} leftAlt={leftAlt}");
        }
    }
}
