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
        StartLog();
        TestRegistry.ApplyFromEnvironment();
        InitializeComponent();
        AotTypeAnchors.Touch();
        CrashLog.Install(this);
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        StartEngine();
        Window = new MainWindow();
        Window.Activate();
        TestRegistry.ScheduleFatalFromEnvironment(Window.DispatcherQueue);
    }

    /// <summary>
    /// The diagnostic log is off unless asked for: <c>PUTTYMODERN_LOG=debug</c> (or <c>error</c>, <c>warning</c>,
    /// <c>info</c>, <c>trace</c>) or <c>--log[=level]</c> on the command line, and <c>PUTTYMODERN_LOG_FILE</c> or
    /// <c>--log-file=path</c> for the file (default: a new one in <c>%LOCALAPPDATA%\PuTTY Modern\logs</c>).
    /// </summary>
    private static void StartLog()
    {
        string[] arguments = Environment.GetCommandLineArgs();
        PuttyLogLevel level = PuttyLog.TryParseSwitch(arguments, out string? file);
        if (level != PuttyLogLevel.Off && PuttyLog.Configure(level, file))
        {
            PuttyLog.Info("app", $"started: {Environment.ProcessPath}, arguments: {string.Join(' ', arguments.Skip(1))}");
        }
    }

    // The engine lives on the thread that starts it and needs that thread's message loop: the UI
    // thread. A missing or incompatible engine is not fatal, the window just has no sessions.
    private static void StartEngine()
    {
        try
        {
            PuttyEngine.EnsureCompatible();
            PuttyEngine.Initialize();
            PuttyEngine.Fatal += OnEngineFatal;
            EngineRunning = true;
        }
        catch (Exception ex) when (ex is DllNotFoundException or InvalidOperationException)
        {
            PuttyLog.Warning("app", $"PuTTY engine not started: {ex.Message}");
        }
    }

    /// <summary>
    /// The engine is about to end the process (PuTTY's own fatal error, a failed assertion in its code, an abort): every
    /// session goes with it, so keep what can be kept. The reason goes to the crash log, the workspace (the tabs and the
    /// layout) is saved so that the next start brings it back, and the user is told. Runs on the thread that hit the error,
    /// possibly inside native code in a state nobody can vouch for: everything here is best effort and none of it may throw.
    /// </summary>
    private static void OnEngineFatal(object? sender, EngineFatalEventArgs e)
    {
        CrashLog.Write("Engine fatal error", null, e.Message);
        try
        {
            (Window as MainWindow)?.SaveWorkspace();
        }
        catch (Exception ex)
        {
            CrashLog.Write("Saving the workspace after a fatal error failed", ex, null);
        }

        if (!TestRegistry.IsActive)
        {
            FatalBox.Show(e.Message);       // a test must not stop at a message box
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
