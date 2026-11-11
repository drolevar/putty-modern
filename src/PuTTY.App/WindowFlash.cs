using System.Runtime.InteropServices;
using PuTTY.Interop;

namespace PuTTY.App;

/// <summary>
/// Draws attention to the main window when a terminal rings its bell and the window is not the one in use: the
/// session's "bell indicator" setting, as PuTTY's flash_window does it (flash until the window is used, or two flashes
/// and then stay lit).
/// </summary>
internal static partial class WindowFlash
{
    private const uint FlashAll = 3, FlashTimer = 4, FlashTimerNoForeground = 12;

    [StructLayout(LayoutKind.Sequential)]
    private struct FlashInfo
    {
        public uint Size;
        public nint Window;
        public uint Flags;
        public uint Count;
        public uint Timeout;
    }

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool FlashWindowEx(ref FlashInfo info);

    [LibraryImport("user32.dll")]
    private static partial nint GetForegroundWindow();

    /// <summary>Flashes <paramref name="window"/> (a window handle) as the indicator says, unless it is in front already.</summary>
    internal static void ForBell(nint window, BellIndicator indicator)
    {
        if (indicator == BellIndicator.Disabled || window == 0 || GetForegroundWindow() == window)
        {
            return;
        }

        var info = new FlashInfo
        {
            Size = (uint)System.Runtime.CompilerServices.Unsafe.SizeOf<FlashInfo>(),
            Window = window,
            // PuTTY: "flash" flashes until the window is used (count 0 = without end), "steady" twice
            Flags = indicator == BellIndicator.Flash ? FlashAll | FlashTimerNoForeground : FlashAll | FlashTimer,
            Count = indicator == BellIndicator.Flash ? 0u : 2u,
            Timeout = 0,
        };
        FlashWindowEx(ref info);
    }
}
