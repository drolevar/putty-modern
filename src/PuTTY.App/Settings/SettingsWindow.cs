using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using PuTTY.Interop;
using Windows.Graphics;

namespace PuTTY.App.Settings;

/// <summary>
/// The configuration window: PuTTY's settings as a window of its own, drawn by <see cref="SettingsView"/>.
/// Closing it, or Cancel, discards what was edited; Open raises <see cref="Opened"/> with the dialog so the
/// caller can start a session from it. The window owns the dialog and disposes it when it closes.
/// </summary>
/// <remarks>
/// A <c>Window</c> subclass, so it is <c>partial</c> (CsWinRT1028 under NativeAOT); it has no XAML, its
/// content is built in code.
/// </remarks>
internal sealed partial class SettingsWindow : Window
{
    private readonly SettingsDialog dialog;
    private readonly SettingsView view;
    private bool finished;
    private bool handling;

    public SettingsWindow(SettingsDialog dialog, string title)
    {
        this.dialog = dialog;
        Title = title;
        view = new SettingsView(dialog, () => Content?.XamlRoot, () => WinRT.Interop.WindowNative.GetWindowHandle(this));

        // Like the main window, the title bar is the framework's own control in the window's content, so that it follows the
        // theme of the app: the system's title bar is drawn light in a dark app, which is how this window looked before.
        var titleBar = new TitleBar { Title = title };
        string icon = Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico");
        if (File.Exists(icon))
        {
            titleBar.IconSource = new ImageIconSource { ImageSource = new BitmapImage(new Uri(icon)) };
        }

        var frame = new Grid();
        frame.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        frame.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        frame.Children.Add(titleBar);
        Grid.SetRow(view.Root, 1);
        frame.Children.Add(view.Root);
        Content = frame;
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(titleBar);
        SystemBackdrop = new Microsoft.UI.Xaml.Media.MicaBackdrop();      // the sketch's #F3F3F3 base is Mica
        WindowSizing.Resize(AppWindow, 1120, 860);                           // the size of the Figma frame, or the screen if that is smaller
        view.Ended += OnEnded;
        Closed += (_, _) =>
        {
            finished = true;
            dialog.Dispose();
        };
    }

    /// <summary>
    /// The user pressed Open (or double-clicked a saved session) and the settings name a host: start the
    /// session from the dialog and return true, and the window closes; return false (the user changed their
    /// mind about replacing an open session) and it stays open.
    /// </summary>
    public Func<SettingsDialog, Task<bool>>? Opened { get; set; }

    private async void OnEnded(bool go)
    {
        if (finished || handling)
        {
            return;
        }

        if (!go)
        {
            finished = true;
            Close();
            return;
        }

        handling = true;
        try
        {
            if (dialog.IsMidSession)
            {
                // Change Settings on a running session: Apply passes the edits to it, and the window is done either way.
                try
                {
                    dialog.Apply();
                }
                catch (InvalidOperationException ex)
                {
                    await SessionInteraction.ShowMessageAsync(Content?.XamlRoot, "The settings could not be applied", ex.Message);
                }

                finished = true;
                Close();
                return;
            }

            bool started = Opened is null || await Opened(dialog);
            if (started)
            {
                finished = true;
                Close();
            }
        }
        finally
        {
            handling = false;
        }
    }
}
