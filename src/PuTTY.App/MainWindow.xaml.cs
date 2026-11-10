using Microsoft.UI.Xaml;
using PuTTY.App.ViewModels;
using PuTTY.Interop;
using Windows.Graphics;

namespace PuTTY.App;

public sealed partial class MainWindow : Window
{
    public MainViewModel ViewModel { get; } = new();

    private readonly TerminalViewController terminal;

    public MainWindow()
    {
        InitializeComponent();

        terminal = new TerminalViewController(TerminalHost);
        Closed += (_, _) =>
        {
            terminal.Dispose();
            terminal.Session?.Dispose();
            App.StopEngine();
        };

        // A way to start a session for tests and development: --connect raw://host:port
        if (ConnectionTarget.FromArguments(Environment.GetCommandLineArgs()) is { } target)
        {
            TerminalHost.Loaded += (_, _) => Connect(target);
        }

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);

        AppWindow.SetIcon("Assets/AppIcon.ico");
        AppWindow.Resize(new SizeInt32(1440, 940));   // size of the Figma sketch frame
    }

    private bool connected;

    private void Connect(ConnectionTarget target)
    {
        if (connected || !App.EngineRunning)
        {
            return;
        }

        connected = true;
        var session = new PuttySession(target.Host, target.Port, target.Protocol);
        session.TitleChanged += (_, e) => Title = $"{e.Title} - PuTTY";
        session.Start();
        terminal.Show(session);
    }
}
