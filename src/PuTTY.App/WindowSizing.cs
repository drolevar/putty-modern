using Microsoft.UI.Windowing;
using Windows.Graphics;

namespace PuTTY.App;

/// <summary>Sizes a window as the sketch has it, but never larger than the display it opens on (a laptop's 1366x768 cannot show 1440x940).</summary>
internal static class WindowSizing
{
    /// <summary>Resizes to <paramref name="width"/> by <paramref name="height"/> physical pixels, or to the work area of the nearest display if that is smaller.</summary>
    public static void Resize(AppWindow window, int width, int height)
    {
        RectInt32 work = DisplayArea.GetFromWindowId(window.Id, DisplayAreaFallback.Nearest).WorkArea;
        window.Resize(new SizeInt32(Math.Min(width, work.Width), Math.Min(height, work.Height)));
    }
}
