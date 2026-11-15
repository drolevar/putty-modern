using System.Runtime.InteropServices;
using Microsoft.UI;

namespace PuTTY.App;

/// <summary>
/// The icon the exe carries (<c>ApplicationIcon</c> in the project file; the build embeds it as the group icon resource 32512),
/// as an <see cref="IconId"/> for <c>AppWindow.SetIcon</c> (the window's icon in the taskbar and Alt+Tab). Reading it from the exe
/// means this icon needs no file next to the program, and no relative path that depends on the current directory. (The title
/// bar's icon is separate: the <c>TitleBar</c> control draws its <c>IconSource</c> only, an image file.)
/// </summary>
internal static partial class AppIcon
{
    private const int EmbeddedIconResourceId = 32512;

    /// <summary>The exe's icon, or <c>default</c> when the exe has none.</summary>
    public static IconId Get()
    {
        nint module = GetModuleHandleW(null);
        if (module == 0)
        {
            return default;
        }

        nint icon = LoadIconW(module, EmbeddedIconResourceId);
        return icon == 0 ? default : Win32Interop.GetIconIdFromIcon(icon);
    }

    [LibraryImport("kernel32.dll", StringMarshalling = StringMarshalling.Utf16)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial nint GetModuleHandleW(string? moduleName);

    [LibraryImport("user32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial nint LoadIconW(nint module, nint name);
}
