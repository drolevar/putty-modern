using Microsoft.UI.Xaml;
using PuTTY.Interop;

namespace PuTTY.App;

/// <summary>Application entry point.</summary>
public partial class App : Application
{
    /// <summary>The main window, for dialogs, pickers and interop that need it.</summary>
    public static Window Window { get; private set; } = null!;

    /// <summary>True while the PuTTY engine is running on the UI thread.</summary>
    public static bool EngineRunning { get; private set; }

    public App()
    {
        InitializeComponent();
        AotTypeAnchors.Touch();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        StartEngine();
        Window = new MainWindow();
        Window.Activate();
    }

    // The engine lives on the thread that starts it and needs that thread's message loop: the UI
    // thread. A missing or incompatible engine is not fatal, the window just has no sessions.
    private static void StartEngine()
    {
        try
        {
            PuttyEngine.EnsureCompatible();
            PuttyEngine.Initialize();
            EngineRunning = true;
        }
        catch (Exception ex) when (ex is DllNotFoundException or InvalidOperationException)
        {
            System.Diagnostics.Debug.WriteLine($"PuTTY engine not started: {ex.Message}");
        }
    }

    internal static void StopEngine()
    {
        if (EngineRunning)
        {
            EngineRunning = false;
            PuttyEngine.Shutdown();
        }
    }
}
