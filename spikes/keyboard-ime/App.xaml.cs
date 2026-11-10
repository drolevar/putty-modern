using Microsoft.UI.Xaml;

namespace KeyboardIme;

public partial class App : Application
{
    public static MainWindow Main { get; private set; } = null!;

    /// <summary>Appends a line to trace.txt next to the results, so a crash under NativeAOT leaves evidence.</summary>
    public static void Trace(string line)
    {
        try
        {
            string dir = Environment.GetCommandLineArgs() is { } a && Array.IndexOf(a, "--out") is int i and >= 0 && i + 1 < a.Length
                ? a[i + 1]
                : Path.Combine(AppContext.BaseDirectory, "results");
            Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, "trace.txt"), $"{DateTime.Now:HH:mm:ss.fff} {line}{Environment.NewLine}");
        }
        catch
        {
            // Tracing must never be the thing that fails.
        }
    }

    public App()
    {
        InitializeComponent();
        UnhandledException += (_, e) => Trace($"Application.UnhandledException: {e.Exception}");
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Trace($"AppDomain.UnhandledException: {e.ExceptionObject}");
        TaskScheduler.UnobservedTaskException += (_, e) => Trace($"UnobservedTaskException: {e.Exception}");
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        Trace("OnLaunched");
        Main = new MainWindow();
        Trace("MainWindow created");
        Main.Activate();
        Trace("Activated");
    }
}
