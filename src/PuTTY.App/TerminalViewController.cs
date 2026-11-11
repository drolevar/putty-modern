using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using PuTTY.Interop;
using Windows.ApplicationModel.DataTransfer;
using Windows.System;
using WinRT;
using ScrollMode = PuTTY.Interop.ScrollMode;   // XAML has one too

namespace PuTTY.App;

/// <summary>
/// Hosts one terminal in a <see cref="UserControl"/>: a <see cref="SwapChainPanel"/> the engine's
/// renderer draws into, sized to the whole cells that fit, refitted when the view or the DPI scale
/// changes, wired to the keyboard hook while it has focus, and to the mouse, the scrollbar and the
/// clipboard.
/// </summary>
/// <remarks>
/// A controller object attached to a plain control, not a subclass of one, on purpose: it keeps the
/// view free of the NativeAOT rules for derived controls (a subclass must be <c>partial</c>, see
/// CLAUDE.md), and a terminal needs no XAML of its own. Everything here runs on the UI thread, which
/// is also the engine thread. The engine never touches the clipboard: this class reads and writes it.
/// </remarks>
internal sealed class TerminalViewController : IDisposable
{
    // Tried in order; Cascadia Mono ships with Windows 11 and Terminal, Consolas with every Windows.
    private static readonly string[] FontFamilies = ["Cascadia Mono", "Consolas"];

    private const float FontSizeDips = 15.0f;

    /// <summary>The AutomationId of the scrollbar, for UI Automation checks (tools/app-terminal-smoke.ps1).</summary>
    public const string ScrollBarAutomationId = "TerminalScrollBar";

    private readonly UserControl host;
    private readonly Grid layout = new();
    private readonly SwapChainPanel panel = new();
    private readonly ScrollBar scrollBar = new();

    /// <summary>
    /// What makes input methods work. A custom control has no text store, so Windows' text services (TSF) leave an IME
    /// out of it: measured with Rime Weasel in the Sandbox, pinyin typed into the terminal arrived as plain Latin
    /// letters. A real <see cref="TextBox"/> has one. So this one, invisible while empty, holds the keyboard focus
    /// and sits at the terminal's cursor (where the IME's candidate window then opens); the IME composes into it,
    /// the composition shows at the cursor, and what is committed is passed to the terminal with
    /// <see cref="PuttySession.TypeText"/> and cleared. Plain keys never reach it: the engine's message hook takes
    /// them first, except the keys an IME has taken (VK_PROCESSKEY), which are left for TSF.
    /// </summary>
    private readonly TextBox proxy = new();
    private const double RestingOpacity = 0.01;
    private bool composing;
    private bool flushing;
    private PuttySession? session;
    private string? fontFamily;
    private bool attached;
    private bool disposed;
    private bool updatingScrollBar;
    private int heldButtons;

    public TerminalViewController(UserControl host)
    {
        this.host = host;
        host.IsTabStop = true;
        host.AllowFocusOnInteraction = true;
        host.UseSystemFocusVisuals = false;
        host.GotFocus += (_, _) =>
        {
            if (proxy.FocusState == FocusState.Unfocused)
            {
                proxy.Focus(FocusState.Programmatic);       // the host got the focus (a tab stop, a dialog closing): the box takes it on
            }

            UpdateKeyTarget();
        };
        host.LostFocus += (_, _) => PuttySession.SetKeyboardTarget(null);

        ConfigureProxy();

        panel.SizeChanged += (_, _) => OnPanelSizeChanged();
        panel.CompositionScaleChanged += (_, _) => OnPanelSizeChanged();
        panel.PointerPressed += OnPointerPressed;
        panel.PointerMoved += OnPointerMoved;
        panel.PointerReleased += OnPointerReleased;
        panel.PointerCanceled += OnPointerLost;
        panel.PointerCaptureLost += OnPointerLost;
        panel.PointerWheelChanged += OnPointerWheelChanged;

        // The scrollbar is always in the layout, disabled while there is no scrollback: one that
        // appeared and vanished would change the terminal's width and with it the line wrapping.
        scrollBar.Orientation = Orientation.Vertical;
        scrollBar.SmallChange = 1;
        scrollBar.IsTabStop = false;
        scrollBar.IndicatorMode = ScrollingIndicatorMode.MouseIndicator;   // a standalone bar otherwise draws no thumb until the pointer is over it
        scrollBar.IsEnabled = false;
        AutomationProperties.SetAutomationId(scrollBar, ScrollBarAutomationId);
        AutomationProperties.SetName(scrollBar, "Terminal scrollback");
        scrollBar.ValueChanged += OnScrollBarValueChanged;

        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(scrollBar, 1);
        layout.Children.Add(panel);
        layout.Children.Add(proxy);
        layout.Children.Add(scrollBar);
    }

    /// <summary>The proxy box looks like nothing until something is being composed in it, and takes no mouse.</summary>
    private void ConfigureProxy()
    {
        proxy.BorderThickness = new Thickness(0);
        proxy.Padding = new Thickness(0);
        proxy.MinWidth = 2;
        proxy.IsHitTestVisible = false;
        proxy.IsSpellCheckEnabled = false;
        proxy.IsTextPredictionEnabled = false;
        proxy.AcceptsReturn = false;
        proxy.HorizontalAlignment = HorizontalAlignment.Left;
        proxy.VerticalAlignment = VerticalAlignment.Top;
        proxy.Opacity = RestingOpacity;                       // visible only while it holds composition text; not 0: WinUI raises TextChanged in a render pass, which an element at opacity 0 never gets
        proxy.UseSystemFocusVisuals = false;

        // The terminal's own colours, in every state of the box (its default theme paints a white box with an accent underline).
        var black = new SolidColorBrush(Microsoft.UI.Colors.Black);
        var light = new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(255, 187, 187, 187));
        var none = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        foreach (string key in new[] { "TextControlBackground", "TextControlBackgroundPointerOver", "TextControlBackgroundFocused", "TextControlBackgroundDisabled" })
        {
            proxy.Resources[key] = black;
        }

        foreach (string key in new[] { "TextControlBorderBrush", "TextControlBorderBrushPointerOver", "TextControlBorderBrushFocused", "TextControlBorderBrushDisabled" })
        {
            proxy.Resources[key] = none;
        }

        foreach (string key in new[] { "TextControlForeground", "TextControlForegroundPointerOver", "TextControlForegroundFocused" })
        {
            proxy.Resources[key] = light;
        }

        AutomationProperties.SetAutomationId(proxy, "TerminalInput");
        AutomationProperties.SetName(proxy, "Terminal input");

        proxy.GotFocus += (_, _) => UpdateKeyTarget();
        proxy.TextCompositionStarted += (_, _) => { composing = true; proxy.Opacity = 1; };
        proxy.TextCompositionEnded += (_, _) =>
        {
            composing = false;
            FlushProxy();
        };
        proxy.TextChanged += (_, _) => OnProxyTextChanged("TextChanged");
        // TextChanged is not raised for every change (it did not fire for text set through UI Automation, which is what
        // voice typing and the emoji panel resemble), but TextChanging is, synchronously and before Text is updated:
        // look again once the change has been applied.
        proxy.TextChanging += (_, _) => host.DispatcherQueue.TryEnqueue(() => OnProxyTextChanged("TextChanging"));
    }

    private void OnProxyTextChanged(string source)
    {
        proxy.Opacity = proxy.Text.Length > 0 ? 1 : RestingOpacity;
        if (!composing)
        {
            FlushProxy();           // text with no composition: the emoji panel, voice typing
        }
    }

    /// <summary>Passes what the proxy box holds (committed text) to the terminal and empties the box.</summary>
    private void FlushProxy()
    {
        if (flushing || proxy.Text.Length == 0)
        {
            return;
        }

        string text = proxy.Text;
        flushing = true;
        try
        {
            proxy.Text = string.Empty;
            proxy.Opacity = RestingOpacity;
        }
        finally
        {
            flushing = false;
        }

        try
        {
            session?.TypeText(text);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException)
        {
            System.Diagnostics.Debug.WriteLine($"Typed text could not be sent: {ex.Message}");      // the session ended under the IME
        }
    }

    /// <summary>The box goes where the cursor is, so that the composition appears there and the IME opens its candidates next to it.</summary>
    private void OnCursorMoved(object? sender, SessionCursorEventArgs e)
    {
        double scaleX = Math.Max(0.1, panel.CompositionScaleX);
        double scaleY = Math.Max(0.1, panel.CompositionScaleY);
        proxy.Margin = new Thickness(e.X / scaleX, e.Y / scaleY, 0, 0);
        proxy.Height = e.Height / scaleY;
        proxy.MinHeight = e.Height / scaleY;
        proxy.FontSize = FontSizeDips;
        if (fontFamily is not null)
        {
            proxy.FontFamily = new FontFamily(fontFamily);
        }
    }

    /// <summary>The terminal's size in cells may have changed (the view was resized, or its first size came).</summary>
    public event Action? Resized;

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
        Detach();
        session = newSession;
        attached = false;

        // A tab that is not on screen yet (a restored workspace opens the others behind the first one) has no panel size
        // and so no view, but its server is already talking: give the session its terminal now, at a default size, so that
        // what arrives is kept and drawn when the view attaches (which fits the terminal to it).
        try
        {
            session.EnableTerminal(80, 24);
        }
        catch (InvalidOperationException)
        {
            // it has one already
        }

        session.ScrollbarChanged += OnScrollbarChanged;
        session.CursorMoved += OnCursorMoved;
        session.ClipboardWrite += OnClipboardWrite;
        session.ClipboardPasteRequested += OnClipboardPasteRequested;
        host.Content = layout;
        layout.UpdateLayout();
        TryAttach();
        proxy.Focus(FocusState.Programmatic);
    }

    private void Detach()
    {
        if (session is { } old)
        {
            old.ScrollbarChanged -= OnScrollbarChanged;
            old.CursorMoved -= OnCursorMoved;
            old.ClipboardWrite -= OnClipboardWrite;
            old.ClipboardPasteRequested -= OnClipboardPasteRequested;
        }
    }

    /// <summary>Puts the keyboard focus on the terminal, for instance after a dialog closed.</summary>
    public void Focus() => proxy.Focus(FocusState.Programmatic);

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
                Resized?.Invoke();
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

            Resized?.Invoke();
        }
    }

    private void UpdateKeyTarget()
    {
        if (attached && session is not null && (proxy.FocusState != FocusState.Unfocused || host.FocusState != FocusState.Unfocused))
        {
            PuttySession.SetKeyboardTarget(session);
        }
    }

    // ----- Mouse ---------------------------------------------------------------------------

    private static TerminalModifiers ModifiersOf(VirtualKeyModifiers keys)
    {
        var mods = TerminalModifiers.None;
        if (keys.HasFlag(VirtualKeyModifiers.Shift))
        {
            mods |= TerminalModifiers.Shift;
        }

        if (keys.HasFlag(VirtualKeyModifiers.Control))
        {
            mods |= TerminalModifiers.Control;
        }

        if (keys.HasFlag(VirtualKeyModifiers.Menu))
        {
            mods |= TerminalModifiers.Alt;
        }

        return mods;
    }

    /// <summary>The pointer's place in view pixels (physical, as the renderer counts) and its time in milliseconds.</summary>
    private (int X, int Y, uint Time) Locate(PointerPoint point) => (
        (int)Math.Round(point.Position.X * panel.CompositionScaleX),
        (int)Math.Round(point.Position.Y * panel.CompositionScaleY),
        unchecked((uint)(point.Timestamp / 1000)));    // microseconds to milliseconds; only differences matter

    private static MouseButton? ButtonOf(PointerUpdateKind kind, out bool pressed)
    {
        pressed = kind is PointerUpdateKind.LeftButtonPressed or PointerUpdateKind.MiddleButtonPressed or PointerUpdateKind.RightButtonPressed;
        return kind switch
        {
            PointerUpdateKind.LeftButtonPressed or PointerUpdateKind.LeftButtonReleased => MouseButton.Left,
            PointerUpdateKind.MiddleButtonPressed or PointerUpdateKind.MiddleButtonReleased => MouseButton.Middle,
            PointerUpdateKind.RightButtonPressed or PointerUpdateKind.RightButtonReleased => MouseButton.Right,
            _ => null,
        };
    }

    private bool MouseReady => attached && !disposed && session is not null;

    private void OnPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        proxy.Focus(FocusState.Pointer);
        if (!MouseReady)
        {
            return;
        }

        PointerPoint point = e.GetCurrentPoint(panel);
        if (ButtonOf(point.Properties.PointerUpdateKind, out bool pressed) is not { } button || !pressed)
        {
            return;
        }

        // Keep receiving moves and the release when a drag leaves the view: that is how a selection is extended.
        panel.CapturePointer(e.Pointer);
        heldButtons |= 1 << (int)button;
        (int x, int y, uint time) = Locate(point);
        Send(MouseAction.Down, button, x, y, e.KeyModifiers, time);
        e.Handled = true;
    }

    private void OnPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!MouseReady)
        {
            return;
        }

        // The engine knows which buttons are down; the button here is only for a move with none.
        (int x, int y, uint time) = Locate(e.GetCurrentPoint(panel));
        Send(MouseAction.Move, MouseButton.Left, x, y, e.KeyModifiers, time);
    }

    private void OnPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!MouseReady)
        {
            return;
        }

        PointerPoint point = e.GetCurrentPoint(panel);
        if (ButtonOf(point.Properties.PointerUpdateKind, out bool pressed) is { } button && !pressed)
        {
            heldButtons &= ~(1 << (int)button);
            (int x, int y, uint time) = Locate(point);
            Send(MouseAction.Up, button, x, y, e.KeyModifiers, time);
            e.Handled = true;
        }

        if (heldButtons == 0)
        {
            panel.ReleasePointerCapture(e.Pointer);
        }
    }

    /// <summary>The pointer was cancelled or capture was taken away: end any drag so the engine does not wait for a release.</summary>
    private void OnPointerLost(object sender, PointerRoutedEventArgs e)
    {
        int held = heldButtons;
        heldButtons = 0;
        if (!MouseReady || held == 0)
        {
            return;
        }

        (int x, int y, uint time) = Locate(e.GetCurrentPoint(panel));
        foreach (MouseButton button in Enum.GetValues<MouseButton>())
        {
            if ((held & (1 << (int)button)) != 0)
            {
                Send(MouseAction.Up, button, x, y, TerminalModifiers.None, time);
            }
        }
    }

    private void OnPointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        if (!MouseReady)
        {
            return;
        }

        PointerPoint point = e.GetCurrentPoint(panel);
        (int x, int y, _) = Locate(point);
        try
        {
            session!.Wheel(point.Properties.MouseWheelDelta, point.Properties.IsHorizontalMouseWheel, x, y, ModifiersOf(e.KeyModifiers));
            e.Handled = true;
        }
        catch (Exception ex) when (ex is ObjectDisposedException or InvalidOperationException)
        {
            // the session ended under us
        }
    }

    private void Send(MouseAction action, MouseButton button, int x, int y, VirtualKeyModifiers keys, uint time) =>
        Send(action, button, x, y, ModifiersOf(keys), time);

    private void Send(MouseAction action, MouseButton button, int x, int y, TerminalModifiers mods, uint time)
    {
        try
        {
            session!.Mouse(action, button, x, y, mods, time);
        }
        catch (Exception ex) when (ex is ObjectDisposedException or InvalidOperationException)
        {
            // the session ended under us
        }
    }

    // ----- Scrollbar -----------------------------------------------------------------------

    /// <summary>The engine reports the scrollback: total lines, the first visible one, the lines on screen.</summary>
    private void OnScrollbarChanged(object? sender, SessionScrollbarEventArgs e)
    {
        int maximum = Math.Max(0, e.Total - e.Page);
        updatingScrollBar = true;
        try
        {
            scrollBar.IsEnabled = maximum > 0;
            scrollBar.Minimum = 0;
            scrollBar.Maximum = maximum;
            scrollBar.ViewportSize = e.Page;
            scrollBar.LargeChange = Math.Max(1, e.Page - 1);
            scrollBar.Value = Math.Clamp(e.Start, 0, maximum);
        }
        finally
        {
            updatingScrollBar = false;
        }
    }

    /// <summary>The user moved the scrollbar (a change we made ourselves in <see cref="OnScrollbarChanged"/> is ignored).</summary>
    private void OnScrollBarValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (updatingScrollBar || disposed || session is null)
        {
            return;
        }

        try
        {
            session.Scroll(ScrollMode.Absolute, (int)Math.Round(e.NewValue));
        }
        catch (Exception ex) when (ex is ObjectDisposedException or InvalidOperationException)
        {
            // the session ended under us
        }
    }

    // ----- Clipboard -----------------------------------------------------------------------

    /// <summary>Selected text goes onto the system clipboard.</summary>
    private static void OnClipboardWrite(object? sender, SessionClipboardEventArgs e)
    {
        try
        {
            var package = new DataPackage { RequestedOperation = DataPackageOperation.Copy };
            package.SetText(e.Text);
            Clipboard.SetContent(package);
        }
        catch (Exception ex)
        {
            // another process may hold the clipboard open; a lost copy must not take the terminal down
            System.Diagnostics.Debug.WriteLine($"Clipboard write failed: {ex.Message}");
        }
    }

    /// <summary>The terminal wants to paste: read the clipboard (asynchronously) and hand the text back.</summary>
    private async void OnClipboardPasteRequested(object? sender, EventArgs e)
    {
        PuttySession? asked = session;
        try
        {
            DataPackageView content = Clipboard.GetContent();
            if (!content.Contains(StandardDataFormats.Text))
            {
                return;
            }

            string text = await content.GetTextAsync();
            if (!disposed && asked is not null && ReferenceEquals(asked, session) && text.Length > 0)
            {
                asked.Paste(text);
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Clipboard paste failed: {ex.Message}");
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
        Detach();
        session = null;
    }
}
