using Microsoft.UI;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace KeyboardIme;

/// <summary>
/// A focusable control that is not a TextBox, standing in for the terminal view. It logs every
/// key and character event XAML delivers and, like a terminal would, handles keys so the
/// framework does not act on them (focus navigation, accelerators).
/// </summary>
/// <remarks>
/// It is a plain <see cref="UserControl"/> with handlers attached, not a subclass: a C# subclass of a
/// WinUI control crashed this spike's NativeAOT build at start-up (see docs/spike-keyboard-ime.md).
/// </remarks>
public static class TerminalKeyControl
{
    public static UserControl Create()
    {
        var c = new UserControl
        {
            IsTabStop = true,
            AllowFocusOnInteraction = true,
            UseSystemFocusVisuals = true,
            Content = new Border
            {
                Background = new SolidColorBrush(Colors.Black),
                BorderBrush = new SolidColorBrush(Colors.Gray),
                BorderThickness = new Thickness(1),
                Child = new TextBlock
                {
                    Text = "terminal control (a UserControl, not a TextBox)",
                    Foreground = new SolidColorBrush(Colors.LimeGreen),
                    Margin = new Thickness(8),
                },
            },
        };

        c.PreviewKeyDown += (_, e) => LogKey("PreviewKeyDown", e, handle: false);
        c.KeyDown += (_, e) => LogKey("KeyDown", e, handle: true);
        c.KeyUp += (_, e) => LogKey("KeyUp", e, handle: true);
        c.ContextRequested += (_, e) => e.Handled = true;
        c.CharacterReceived += (_, e) =>
        {
            Rec.Add("xaml", "Char", ch: e.Character, scan: (int)e.KeyStatus.ScanCode, ext: e.KeyStatus.IsExtendedKey,
                alt: e.KeyStatus.IsMenuKeyDown, was: e.KeyStatus.WasKeyDown, rep: (int)e.KeyStatus.RepeatCount);
            e.Handled = true;
        };
        c.GotFocus += (_, _) => Hook.FocusIsTerminal = true;
        c.LostFocus += (_, _) => Hook.FocusIsTerminal = false;
        return c;
    }

    /// <summary>
    /// Pass A handles every key (the way a terminal that owns the keyboard would); pass A2 handles
    /// only keys that are not text, leaving text keys for XAML to turn into CharacterReceived.
    /// </summary>
    public static bool HandleAll = true;

    /// <summary>Keys the terminal turns into escape sequences or control codes itself, not text.</summary>
    public static bool IsNotText(int vk)
    {
        bool ctrl = Native.IsDown(Native.VK_CONTROL);
        bool alt = Native.IsDown(Native.VK_MENU);
        bool altgr = ctrl && alt;      // Windows treats Ctrl+Alt as AltGr when translating text
        return Ported.IsSpecial(vk) || vk is Native.VK_TAB or 0x1B or 0x5D /* Apps */ || (ctrl && !alt) || (alt && !ctrl);
    }

    private static void LogKey(string kind, KeyRoutedEventArgs e, bool handle)
    {
        var s = e.KeyStatus;
        Rec.Add("xaml", kind, vk: (int)e.Key, scan: (int)s.ScanCode, ext: s.IsExtendedKey, alt: s.IsMenuKeyDown,
            was: s.WasKeyDown, up: s.IsKeyReleased, rep: (int)s.RepeatCount,
            extra: e.OriginalKey != e.Key ? $"original={(int)e.OriginalKey}" : "");
        if (handle && (HandleAll || IsNotText((int)e.Key)))
        {
            e.Handled = true;
        }
    }

    /// <summary>
    /// The proxy TextBox design: text (dead keys, AltGr, IME, emoji panel) arrives through a real
    /// TextBox, and only keys that are not text are handled before the TextBox sees them.
    /// </summary>
    public static void AttachProxyBox(TextBox box)
    {
        box.IsSpellCheckEnabled = false;
        box.IsTextPredictionEnabled = false;
        box.AcceptsReturn = false;

        box.PreviewKeyDown += (_, e) =>
        {
            var s = e.KeyStatus;
            int vk = (int)e.Key;
            bool handle = IsNotText(vk);
            Rec.Add("tbox", "PreviewKeyDown", vk: vk, scan: (int)s.ScanCode, ext: s.IsExtendedKey, alt: s.IsMenuKeyDown,
                was: s.WasKeyDown, up: s.IsKeyReleased, rep: (int)s.RepeatCount,
                extra: handle ? "handled-by-terminal" : "left-to-textbox");
            if (handle)
            {
                e.Handled = true;
            }
        };
        box.ContextRequested += (_, e) => e.Handled = true;
        box.PreviewKeyUp += (_, e) =>
        {
            Rec.Add("tbox", "PreviewKeyUp", vk: (int)e.Key, scan: (int)e.KeyStatus.ScanCode, ext: e.KeyStatus.IsExtendedKey, up: true);
            if (IsNotText((int)e.Key) || e.Key == Windows.System.VirtualKey.F10)
            {
                e.Handled = true;       // the context menu opens on key-up for Shift+F10 and the Apps key
            }
        };
        box.KeyDown += (_, e) => Rec.Add("tbox", "KeyDown", vk: (int)e.Key, scan: (int)e.KeyStatus.ScanCode,
            ext: e.KeyStatus.IsExtendedKey, alt: e.KeyStatus.IsMenuKeyDown, was: e.KeyStatus.WasKeyDown);
        box.CharacterReceived += (_, e) => Rec.Add("tbox", "Char", ch: e.Character);
        box.BeforeTextChanging += (_, e) => Rec.Add("tbox", "BeforeTextChanging", extra: Quote(e.NewText));
        box.TextChanged += (_, _) =>
        {
            if (!Suppress)
            {
                Rec.Add("tbox", "TextChanged", extra: Quote(box.Text));
            }
        };
        box.TextCompositionStarted += (_, _) => Rec.Add("tbox", "CompositionStarted");
        box.TextCompositionChanged += (_, e) => Rec.Add("tbox", "CompositionChanged", extra: $"{e.StartIndex}+{e.Length}");
        box.TextCompositionEnded += (_, e) => Rec.Add("tbox", "CompositionEnded", extra: $"{e.StartIndex}+{e.Length}");
        box.GotFocus += (_, _) => Hook.FocusIsTerminal = false;
    }

    public static bool Suppress;

    private static string Quote(string text) => string.Join(' ', text.Select(c => $"U+{(int)c:X4}"));
}
