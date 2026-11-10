using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using PuTTY.Interop;
using WinRT;

namespace PuTTY.App;

/// <summary>
/// Hosts one terminal in a <see cref="UserControl"/>: a <see cref="SwapChainPanel"/> the engine's
/// renderer draws into, sized to the whole cells that fit, refitted when the view or the DPI scale
/// changes, and wired to the keyboard hook while it has focus.
/// </summary>
/// <remarks>
/// A controller object attached to a plain control, not a subclass of one, on purpose: it keeps the
/// view free of the NativeAOT rules for derived controls (a subclass must be <c>partial</c>, see
/// CLAUDE.md), and a terminal needs no XAML of its own. Everything here runs on the UI thread, which
/// is also the engine thread.
/// </remarks>
internal sealed class TerminalViewController : IDisposable
{
    // Tried in order; Cascadia Mono ships with Windows 11 and Terminal, Consolas with every Windows.
    private static readonly string[] FontFamilies = ["Cascadia Mono", "Consolas"];

    private const float FontSizeDips = 15.0f;

    private readonly UserControl host;
    private readonly SwapChainPanel panel = new();
    private PuttySession? session;
    private string? fontFamily;
    private bool attached;
    private bool disposed;

    public TerminalViewController(UserControl host)
    {
        this.host = host;
        host.IsTabStop = true;
        host.AllowFocusOnInteraction = true;
        host.UseSystemFocusVisuals = false;
        host.GotFocus += (_, _) => UpdateKeyTarget();
        host.LostFocus += (_, _) => PuttySession.SetKeyboardTarget(null);
        host.PointerPressed += (_, _) => host.Focus(FocusState.Pointer);

        panel.SizeChanged += (_, _) => OnPanelSizeChanged();
        panel.CompositionScaleChanged += (_, _) => OnPanelSizeChanged();
    }

    /// <summary>The session shown, or null.</summary>
    public PuttySession? Session => session;

    /// <summary>The terminal's size in cells, or null before a view is attached.</summary>
    public TerminalGeometry? Geometry => attached && session is not null ? session.Geometry : null;

    /// <summary>
    /// Shows <paramref name="newSession"/> (already started) in this view. The terminal view is
    /// attached as soon as the panel has a size, which may be a layout pass later.
    /// </summary>
    public void Show(PuttySession newSession)
    {
        session = newSession;
        attached = false;
        host.Content = panel;
        panel.UpdateLayout();
        TryAttach();
        host.Focus(FocusState.Programmatic);
    }

    private (int Width, int Height) PhysicalSize() => (
        (int)Math.Round(panel.ActualWidth * panel.CompositionScaleX),
        (int)Math.Round(panel.ActualHeight * panel.CompositionScaleY));

    private void TryAttach()
    {
        if (disposed || attached || session is null)
        {
            return;
        }

        (int width, int height) = PhysicalSize();
        if (width < 8 || height < 8)
        {
            return;     // not laid out yet: OnPanelSizeChanged tries again
        }

        float sizePixels = FontSizeDips * (float)panel.CompositionScaleX;
        nint abi = ((IWinRTObject)panel).NativeObject.ThisPtr;
        foreach (string family in FontFamilies)
        {
            try
            {
                session.AttachPanelView(abi, width, height, family, sizePixels);
                fontFamily = family;
                attached = true;
                UpdateKeyTarget();
                return;
            }
            catch (InvalidOperationException) when (family != FontFamilies[^1])
            {
                // that font is not installed; try the next
            }
        }
    }

    private void OnPanelSizeChanged()
    {
        if (disposed || session is null)
        {
            return;
        }

        if (!attached)
        {
            TryAttach();
            return;
        }

        (int width, int height) = PhysicalSize();
        if (width >= 8 && height >= 8)
        {
            session.ResizeView(width, height);
            if (fontFamily is not null)
            {
                // The DPI scale may have changed, which changes the size of a font in pixels.
                session.SetFont(fontFamily, FontSizeDips * (float)panel.CompositionScaleX);
            }
        }
    }

    private void UpdateKeyTarget()
    {
        if (attached && session is not null && host.FocusState != FocusState.Unfocused)
        {
            PuttySession.SetKeyboardTarget(session);
        }
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        PuttySession.SetKeyboardTarget(null);
        session = null;
    }
}
