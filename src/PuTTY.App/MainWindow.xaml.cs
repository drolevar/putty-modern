using Digi21.WinUI.Docking;
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

    private readonly SessionListController sessions;
    private readonly SessionDetailsController details;

    // One tab per open session. The welcome document (TerminalDocument, with the engine status) is closed when the first opens.
    private readonly List<SessionTab> tabs = [];
    private readonly Dictionary<DocumentWindow, SessionTab> tabOf = [];
    private SessionTab? active;
    private int tabCounter;
    private bool welcomeClosed;

    public MainWindow()
    {
        InitializeComponent();

        sessions = new SessionListController(SessionsHost);
        sessions.OpenRequested += OpenSaved;
        sessions.NewRequested += () => OpenSettings(null);
        sessions.EditRequested += name => OpenSettings(name);
        details = new SessionDetailsController(DetailsHost);
        details.SettingsRequested += OpenChangeSettings;
        SessionsHost.Loaded += (_, _) =>
        {
            if (App.EngineRunning)
            {
                sessions.Reload();
            }
        };

        DockSite.WindowActivated += (_, e) =>
        {
            if (e.Window is DocumentWindow document && tabOf.TryGetValue(document, out SessionTab? tab))
            {
                Activate(tab);
            }
        };
        DockSite.WindowClosed += (_, e) =>
        {
            if (e.Window is DocumentWindow document && tabOf.Remove(document, out SessionTab? tab))
            {
                tabs.Remove(tab);
                if (ReferenceEquals(active, tab))
                {
                    active = null;
                    details.Clear();
                    Title = "PuTTY";
                }

                tab.Dispose();      // the tab was closed: its session ends
            }
        };

        Closed += (_, _) =>
        {
            foreach (SessionTab tab in tabs)
            {
                tab.Dispose();
            }

            tabs.Clear();
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

        connected = true;      // TerminalHost.Loaded can come more than once
        PuttySessionOptions? options = target.UserName is null && target.KeyFile is null
            ? null
            : new PuttySessionOptions { UserName = target.UserName, KeyFile = target.KeyFile };
        StartSession(() => new PuttySession(target.Host, target.Port, target.Protocol, options), target.Host, $"{target.Host}:{target.Port}");
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
            _ = SessionInteraction.ShowMessageAsync(DockSite.XamlRoot, "Could not open the settings", ex.Message);
            return;
        }

        try
        {
            if (savedName is not null && !dialog.LoadSavedSession(savedName))
            {
                dialog.Dispose();
                _ = SessionInteraction.ShowMessageAsync(DockSite.XamlRoot, "Could not open the settings", $"There is no saved session called \"{savedName}\" any more.");
                return;
            }

            var window = new SettingsWindow(dialog, savedName is null ? "New session" : $"Settings - {savedName}")
            {
                Opened = d =>
                {
                    StartSession(() => d.CreateSession(), TitleFor(d));       // a tab of its own, beside any that are open
                    return Task.FromResult(true);
                },
            };
            window.Closed += (_, _) => sessions.Reload();       // a session saved or deleted there shows up here
            window.Activate();
        }
        catch (Exception ex)
        {
            // A bug in drawing one of PuTTY's controls must not take the application down, and must be seen.
            dialog.Dispose();
            _ = SessionInteraction.ShowMessageAsync(DockSite.XamlRoot, "Could not open the settings", ex.ToString());
        }
    }

    /// <summary>
    /// PuTTY's Change Settings on the open session (the Session settings button of the details panel): the same box,
    /// without what cannot change while connected, and Apply instead of Open. Apply passes the edits to the session.
    /// </summary>
    private void OpenChangeSettings()
    {
        if (!App.EngineRunning || active is not { Session.IsConnected: true } tab)
        {
            return;
        }

        PuttySession session = tab.Session;

        SettingsDialog dialog;
        try
        {
            dialog = SettingsDialog.OpenForSession(session);
        }
        catch (InvalidOperationException ex)
        {
            _ = SessionInteraction.ShowMessageAsync(DockSite.XamlRoot, "Could not open the session's settings", ex.Message);
            return;
        }

        try
        {
            var window = new SettingsWindow(dialog, $"Session settings - {tab.Title}");
            window.Closed += (_, _) => tab.Terminal.Focus();
            window.Activate();
        }
        catch (Exception ex)
        {
            dialog.Dispose();
            _ = SessionInteraction.ShowMessageAsync(DockSite.XamlRoot, "Could not open the session's settings", ex.ToString());
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

    /// <summary>Opens a saved session in a tab of its own.</summary>
    private void OpenSaved(string name)
    {
        if (!App.EngineRunning)
        {
            return;
        }

        if (StartSession(() => PuttySession.OpenSaved(name), name, PuttySessions.Describe(name)?.Description))
        {
            sessions.NoteOpened(name);      // it goes to the top of RECENT
        }
    }

    /// <summary>
    /// Starts a session in a new tab: a document with a terminal view of its own, beside the tabs that are open. false
    /// (after telling the user) if it could not be started.
    /// </summary>
    private bool StartSession(Func<PuttySession> create, string title, string? description = null)
    {
        PuttySession? session = null;
        TerminalViewController? terminal = null;
        SessionInteraction? interaction = null;
        try
        {
            session = create();
            var host = new UserControl { HorizontalContentAlignment = HorizontalAlignment.Stretch, VerticalContentAlignment = VerticalAlignment.Stretch };
            terminal = new TerminalViewController(host);
            var document = new DocumentWindow { Title = title, Content = host, SerializationId = $"doc.session.{++tabCounter}" };
            interaction = new SessionInteraction(session, () => DockSite.XamlRoot, terminal.Focus);
            var tab = new SessionTab(document, host, terminal, session, interaction, title, description);
            session.TitleChanged += (_, e) =>
            {
                // PuTTY's own default title already ends in " - PuTTY" ("host - PuTTY"); a title the server sets does not.
                tab.WindowTitle = e.Title.EndsWith("PuTTY", StringComparison.Ordinal) ? e.Title : $"{e.Title} - PuTTY";
                if (ReferenceEquals(active, tab))
                {
                    Title = tab.WindowTitle;
                }
            };
            session.Closed += (_, _) =>
            {
                tab.Connected = false;
                if (ReferenceEquals(active, tab))
                {
                    details.SetConnected(false);
                }
            };
            session.Start();

            tabs.Add(tab);
            tabOf[document] = tab;
            DockSite.OpenDocument(document);
            CloseWelcomeDocument();
            terminal.Show(session);
            document.Activate();
            Activate(tab);
            return true;
        }
        catch (InvalidOperationException ex)
        {
            interaction?.Dispose();
            terminal?.Dispose();
            session?.Dispose();
            _ = SessionInteraction.ShowMessageAsync(DockSite.XamlRoot, "Could not connect", ex.Message);
            return false;
        }
    }

    /// <summary>A tab became the active one: the details panel, the title bar and the keyboard follow it.</summary>
    private void Activate(SessionTab tab)
    {
        active = tab;
        details.Show(tab.Title, tab.Description);
        details.SetConnected(tab.Connected);
        Title = tab.WindowTitle;
        tab.Terminal.Focus();
    }

    /// <summary>The first session replaces the welcome document (the engine status that was there before any session).</summary>
    private void CloseWelcomeDocument()
    {
        if (welcomeClosed)
        {
            return;
        }

        welcomeClosed = true;
        try
        {
            DockSite.CloseWindow(TerminalDocument);
        }
        catch (InvalidOperationException)
        {
            // already closed by the user
        }
    }
}