using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using PuTTY.App.ViewModels;
using PuTTY.Interop;
using Windows.Graphics;

namespace PuTTY.App;

public sealed partial class MainWindow : Window
{
    public MainViewModel ViewModel { get; } = new();

    private readonly TerminalViewController terminal;
    private readonly SessionListController sessions;
    private SessionInteraction? interaction;

    public MainWindow()
    {
        InitializeComponent();

        terminal = new TerminalViewController(TerminalHost);
        sessions = new SessionListController(SessionsHost);
        sessions.OpenRequested += name => _ = OpenSavedAsync(name);
        SessionsHost.Loaded += (_, _) =>
        {
            if (App.EngineRunning)
            {
                sessions.Reload();
            }
        };

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
        StartSession(() => new PuttySession(target.Host, target.Port, target.Protocol, options), target.Host);
    }

    /// <summary>Opens a saved session in the terminal; asks first if a connection is open, which it replaces.</summary>
    private async Task OpenSavedAsync(string name)
    {
        if (!App.EngineRunning)
        {
            return;
        }

        if (terminal.Session is { IsConnected: true } && !await ConfirmReplaceAsync(name))
        {
            return;
        }

        StartSession(() => PuttySession.OpenSaved(name), name);
    }

    /// <summary>
    /// There is one terminal for now (tabs are the workspace slice), so opening another session replaces
    /// the open one, which must not happen without the user''s say.
    /// </summary>
    private async Task<bool> ConfirmReplaceAsync(string next)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = TerminalHost.XamlRoot,
            Title = "Close the open session?",
            Content = $"Opening \"{next}\" closes the connection that is open now.",
            PrimaryButtonText = "Close and open",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
        };
        try
        {
            return await dialog.ShowAsync() == ContentDialogResult.Primary;
        }
        catch (Exception)
        {
            return false;       // another dialog is open: do nothing rather than guess
        }
    }

    private void StartSession(Func<PuttySession> create, string documentTitle)
    {
        // The old session goes first: its renderer holds the terminal panel's swap chain.
        interaction?.Dispose();
        interaction = null;
        terminal.Session?.Dispose();

        PuttySession? session = null;
        try
        {
            session = create();
            session.TitleChanged += (_, e) => Title = $"{e.Title} - PuTTY";
            interaction = new SessionInteraction(session, () => TerminalHost.XamlRoot, terminal.Focus);
            session.Start();
            terminal.Show(session);
            TerminalDocument.Title = documentTitle;
        }
        catch (InvalidOperationException ex)
        {
            session?.Dispose();
            interaction?.Dispose();
            interaction = null;
            _ = SessionInteraction.ShowMessageAsync(TerminalHost.XamlRoot, "Could not connect", ex.Message);
        }
    }
}