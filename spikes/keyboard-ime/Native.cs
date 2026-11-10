using System.Runtime.InteropServices;

namespace KeyboardIme;

/// <summary>The few Win32 calls the spike needs: a thread message hook, SendInput, layouts, ToUnicodeEx.</summary>
internal static unsafe partial class Native
{
    public const int WH_GETMESSAGE = 3;

    public const uint WM_NULL = 0x0000;
    public const uint WM_INPUTLANGCHANGE = 0x0051;
    public const uint WM_KEYDOWN = 0x0100;
    public const uint WM_KEYUP = 0x0101;
    public const uint WM_CHAR = 0x0102;
    public const uint WM_DEADCHAR = 0x0103;
    public const uint WM_SYSKEYDOWN = 0x0104;
    public const uint WM_SYSKEYUP = 0x0105;
    public const uint WM_SYSCHAR = 0x0106;
    public const uint WM_SYSDEADCHAR = 0x0107;
    public const uint WM_UNICHAR = 0x0109;
    public const uint WM_IME_STARTCOMPOSITION = 0x010D;
    public const uint WM_IME_ENDCOMPOSITION = 0x010E;
    public const uint WM_IME_COMPOSITION = 0x010F;
    public const uint WM_IME_SETCONTEXT = 0x0281;
    public const uint WM_IME_NOTIFY = 0x0282;
    public const uint WM_IME_CHAR = 0x0286;
    public const uint WM_IME_KEYDOWN = 0x0290;

    public const int VK_BACK = 0x08, VK_TAB = 0x09, VK_RETURN = 0x0D, VK_SHIFT = 0x10, VK_CONTROL = 0x11,
        VK_MENU = 0x12, VK_CAPITAL = 0x14, VK_PRIOR = 0x21, VK_NEXT = 0x22, VK_END = 0x23, VK_HOME = 0x24,
        VK_LEFT = 0x25, VK_UP = 0x26, VK_RIGHT = 0x27, VK_DOWN = 0x28, VK_INSERT = 0x2D, VK_DELETE = 0x2E,
        VK_NUMPAD0 = 0x60, VK_DIVIDE = 0x6F, VK_F1 = 0x70, VK_F24 = 0x87, VK_NUMLOCK = 0x90,
        VK_LSHIFT = 0xA0, VK_RSHIFT = 0xA1, VK_LCONTROL = 0xA2, VK_RCONTROL = 0xA3, VK_LMENU = 0xA4,
        VK_RMENU = 0xA5, VK_PROCESSKEY = 0xE5, VK_PACKET = 0xE7;

    public const uint KEYEVENTF_EXTENDEDKEY = 0x1, KEYEVENTF_KEYUP = 0x2, KEYEVENTF_UNICODE = 0x4, KEYEVENTF_SCANCODE = 0x8;

    [StructLayout(LayoutKind.Sequential)]
    public struct MSG
    {
        public nint hwnd;
        public uint message;
        public nuint wParam;
        public nint lParam;
        public uint time;
        public int x;
        public int y;
        public uint lPrivate;
    }

    /// <summary>INPUT with the KEYBDINPUT arm of the union (40 bytes on x64).</summary>
    [StructLayout(LayoutKind.Explicit, Size = 40)]
    public struct INPUT
    {
        [FieldOffset(0)] public uint type;          // 1 = keyboard
        [FieldOffset(8)] public ushort wVk;
        [FieldOffset(10)] public ushort wScan;
        [FieldOffset(12)] public uint dwFlags;
        [FieldOffset(16)] public uint time;
        [FieldOffset(24)] public nuint dwExtraInfo;
    }

    [LibraryImport("user32.dll", SetLastError = true)]
    public static partial nint SetWindowsHookExW(int idHook, delegate* unmanaged<int, nint, nint, nint> proc, nint module, uint threadId);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool UnhookWindowsHookEx(nint hook);

    [LibraryImport("user32.dll")]
    public static partial nint CallNextHookEx(nint hook, int code, nint wParam, nint lParam);

    [LibraryImport("kernel32.dll")]
    public static partial uint GetCurrentThreadId();

    [LibraryImport("user32.dll")]
    public static partial uint SendInput(uint count, INPUT* inputs, int size);

    [LibraryImport("user32.dll")]
    public static partial nint GetForegroundWindow();

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetForegroundWindow(nint hwnd);

    [LibraryImport("user32.dll")]
    public static partial short GetKeyState(int vk);

    [LibraryImport("user32.dll")]
    public static partial short GetAsyncKeyState(int vk);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetKeyboardState(byte* state);

    [LibraryImport("user32.dll")]
    public static partial nint GetKeyboardLayout(uint threadId);

    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)]
    public static partial nint LoadKeyboardLayoutW(string klid, uint flags);

    [LibraryImport("user32.dll")]
    public static partial nint ActivateKeyboardLayout(nint hkl, uint flags);

    [LibraryImport("user32.dll")]
    public static partial uint MapVirtualKeyExW(uint code, uint mapType, nint hkl);

    [LibraryImport("user32.dll")]
    public static partial int ToUnicodeEx(uint vk, uint scan, byte* state, char* buffer, int length, uint flags, nint hkl);

    [LibraryImport("user32.dll")]
    public static partial int GetClassNameW(nint hwnd, char* buffer, int max);

    [LibraryImport("user32.dll")]
    public static partial nint GetAncestor(nint hwnd, uint flags);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool PostMessageW(nint hwnd, uint message, nuint wParam, nint lParam);

    [LibraryImport("user32.dll")]
    public static partial nint GetFocus();

    [LibraryImport("user32.dll")]
    public static partial uint GetWindowThreadProcessId(nint hwnd, uint* processId);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool AttachThreadInput(uint idAttach, uint idAttachTo, [MarshalAs(UnmanagedType.Bool)] bool attach);

    [LibraryImport("user32.dll")]
    public static partial void keybd_event(byte vk, byte scan, uint flags, nuint extra);

    /// <summary>
    /// Becomes the foreground window even when launched from a service-started process (as
    /// `wsb exec` does), which Windows' foreground lock refuses. Only used with --force-foreground,
    /// inside the disposable Sandbox: the Alt tap goes to whatever is in front.
    /// </summary>
    public static bool ForceForeground(nint hwnd)
    {
        if (GetForegroundWindow() == hwnd)
        {
            return true;
        }

        nint front = GetForegroundWindow();
        uint frontThread = front == 0 ? 0 : GetWindowThreadProcessId(front, null);
        uint thisThread = GetCurrentThreadId();
        if (frontThread != 0 && frontThread != thisThread)
        {
            AttachThreadInput(thisThread, frontThread, true);
            SetForegroundWindow(hwnd);
            AttachThreadInput(thisThread, frontThread, false);
        }
        else
        {
            SetForegroundWindow(hwnd);
        }

        if (GetForegroundWindow() != hwnd)
        {
            keybd_event(0x12, 0, 0, 0);        // the classic Alt tap that lifts the foreground lock
            keybd_event(0x12, 0, KEYEVENTF_KEYUP, 0);
            SetForegroundWindow(hwnd);
        }

        return GetForegroundWindow() == hwnd;
    }

    public static bool IsDown(int vk) => GetKeyState(vk) < 0;

    public static string ClassName(nint hwnd)
    {
        char* buffer = stackalloc char[128];
        int n = GetClassNameW(hwnd, buffer, 128);
        return n > 0 ? new string(buffer, 0, n) : "?";
    }
}
