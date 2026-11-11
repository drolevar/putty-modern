using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using PuTTY.App.Settings;
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
        sessions.NewRequested += () => OpenSettings(null);
        sessions.EditRequested += name => OpenSettings(name);
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
        WindowSizing.Resize(AppWindow, 1440, 940);    // size of the Figma sketch frame, or the screen if that is smaller
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

    /// <summary>
    /// PuTTY's configuration window, for a new session or, with a name, for a saved one (loaded the way the Load
    /// button does). Open starts the session in the terminal; PuTTY's own Save writes it to PuTTY's storage.
    /// </summary>
    private void OpenSettings(string? savedName)
    {
        if (!App.EngineRunning)
        {
            return;
        }

        SettingsDialog dialog;
        try
        {
            dialog = SettingsDialog.Open(useSavedDefaults: true);      // a new session starts from the user's own Default Settings
        }
        catch (InvalidOperationException ex)
        {
            _ = SessionInteraction.ShowMessageAsync(TerminalHost.XamlRoot, "Could not open the settings", ex.Message);
            return;
        }

        try
        {
            if (savedName is not null && !dialog.LoadSavedSession(savedName))
            {
                dialog.Dispose();
                _ = SessionInteraction.ShowMessageAsync(TerminalHost.XamlRoot, "Could not open the settings", $"There is no saved session called \"{savedName}\" any more.");
                return;
            }

            var window = new SettingsWindow(dialog, savedName is null ? "New session" : $"Settings - {savedName}")
            {
                Opened = async d =>
                {
                    string title = TitleFor(d);
                    if (terminal.Session is { IsConnected: true } && !await ConfirmReplaceAsync(title))
                    {
                        return false;
                    }

                    StartSession(() => d.CreateSession(), title);
                    return true;
                },
            };
            window.Closed += (_, _) => sessions.Reload();       // a session saved or deleted there shows up here
            window.Activate();
        }
        catch (Exception ex)
        {
            // A bug in drawing one of PuTTY's controls must not take the application down, and must be seen.
            dialog.Dispose();
            _ = SessionInteraction.ShowMessageAsync(TerminalHost.XamlRoot, "Could not open the settings", ex.ToString());
        }
    }

    /// <summary>What to call the document: the saved session's name if it has one, else the host.</summary>
    private static string TitleFor(SettingsDialog dialog)
    {
        SettingsControl[] session = [.. dialog.Groups.Where(g => g.Path == "Session").SelectMany(g => g.Controls).Where(c => c.Type == SettingsControlType.EditBox)];
        foreach (string label in new[] { "Saved Sessions", "Host Name (or IP address)" })
        {
            if (session.FirstOrDefault(c => c.Label == label) is { } box && dialog.GetText(box.Id).Trim() is { Length: > 0 } text)
            {
                return text;
            }
        }

        return "Session";
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

        if (StartSession(() => PuttySession.OpenSaved(name), name))
        {
            sessions.NoteOpened(name);      // it goes to the top of RECENT
        }
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

    /// <summary>Starts a session in the terminal; false (after telling the user) if it could not be started.</summary>
    private bool StartSession(Func<PuttySession> create, string documentTitle)
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
            return true;
        }
        catch (InvalidOperationException ex)
        {
            session?.Dispose();
            interaction?.Dispose();
            interaction = null;
            _ = SessionInteraction.ShowMessageAsync(TerminalHost.XamlRoot, "Could not connect", ex.Message);
            return false;
        }
    }
}