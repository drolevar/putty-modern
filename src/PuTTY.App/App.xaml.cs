using Microsoft.UI.Xaml;

namespace PuTTY.App;

/// <summary>Application entry point.</summary>
public partial class App : Application
{
    /// <summary>The main window, for dialogs, pickers and interop that need it.</summary>
    public static Window Window { get; private set; } = null!;

    public App()
    {
        InitializeComponent();
        AotTypeAnchors.Touch();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        Window = new MainWindow();
        Window.Activate();
    }
}
