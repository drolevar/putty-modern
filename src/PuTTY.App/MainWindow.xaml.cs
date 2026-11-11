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
            interaction?.Dispose();
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
    private SessionInteraction? interaction;

    private void Connect(ConnectionTarget target)
    {
        if (connected || !App.EngineRunning)
        {
            return;
        }

        connected = true;
        PuttySessionOptions? options = target.UserName is null && target.KeyFile is null
            ? null
            : new PuttySessionOptions { UserName = target.UserName, KeyFile = target.KeyFile };
        var session = new PuttySession(target.Host, target.Port, target.Protocol, options);
        session.TitleChanged += (_, e) => Title = $"{e.Title} - PuTTY";
        interaction = new SessionInteraction(session, () => TerminalHost.XamlRoot, terminal.Focus);
        session.Start();
        terminal.Show(session);
    }
}
