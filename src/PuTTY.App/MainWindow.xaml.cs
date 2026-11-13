using Digi21.WinUI.Docking;
using Digi21.WinUI.Docking.Serialization;
using PuTTY.Sessions;
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
    private readonly ConnectionLogController log;
    private readonly StatusBarController status;
    private readonly CommandBarController commands;
    // One tab per open session. The welcome document (TerminalDocument, with the engine status) is closed when the first opens.
    private readonly List<SessionTab> tabs = [];
    private readonly Dictionary<DocumentWindow, SessionTab> tabOf = [];
    private SessionTab? active;
    private int tabCounter;
    private bool restored;
    private readonly bool startedWithTarget = ConnectionTarget.FromArguments(Environment.GetCommandLineArgs()) is not null;
    private readonly WorkspaceStateStore workspaceStore = new(
        TestRegistry.WorkspaceFileOverride is { } folders
            ? Path.Combine(Path.GetDirectoryName(folders)!, "layout.json")      // a test run keeps it with its other scratch files
            : WorkspaceStateStore.DefaultPath);
    private const char MiddleDot = (char)0xB7;      // the dot of "host . protocol" in a tabs description
    private bool welcomeClosed;

    public MainWindow()
    {
        InitializeComponent();

        // The welcome tab is an empty state, or says why the engine could not start.
        WelcomePanel.Visibility = ViewModel.EngineAvailable ? Visibility.Visible : Visibility.Collapsed;
        EngineProblemText.Text = ViewModel.EngineProblem;
        EngineProblemText.Visibility = ViewModel.EngineAvailable ? Visibility.Collapsed : Visibility.Visible;

        sessions = new SessionListController(SessionsHost);
        sessions.OpenRequested += OpenSaved;
        sessions.NewRequested += () => OpenSettings(null);
        sessions.EditRequested += name => OpenSettings(name);
        details = new SessionDetailsController(DetailsHost);
        details.SettingsRequested += OpenChangeSettings;
        details.ReconnectRequested += Reconnect;
        details.DuplicateRequested += Duplicate;
        details.DisconnectRequested += Disconnect;
        log = new ConnectionLogController(LogHost);
        status = new StatusBarController(StatusBarHost);
        commands = new CommandBarController(CommandBarHost);
        commands.NewSessionRequested += () => OpenSettings(null);
        commands.ReconnectRequested += Reconnect;
        commands.DisconnectRequested += Disconnect;
        commands.SettingsRequested += OpenChangeSettings;
        commands.DuplicateRequested += Duplicate;
        commands.NewFolderRequested += sessions.RequestNewFolder;
        commands.AboutRequested += () => _ = ShowAboutAsync();
        commands.ArrangementRequested += ApplyArrangement;
        commands.SaveLayoutRequested += () => _ = SaveLayoutAsync();
        commands.LayoutRequested += ApplyLayout;
        commands.DeleteLayoutRequested += DeleteLayout;
        StartLayouts();
        SessionsHost.Loaded += (_, _) =>
        {
            if (App.EngineRunning)
            {
                sessions.Reload();
            }
        };

        // The layout and the open sessions come back as they were; they are saved when the window closes.
        DockSite.Loaded += (_, _) =>
        {
            CollapseDefaultPanels();        // a fresh start has Session details and the Connection log as tabs at the edge
            CaptureDefaultLayout();         // the built-in Default, with them so; a saved layout changes them afterwards
            RestoreWorkspace();
            FloodLogForTest();
        };
        AppWindow.Closing += (_, _) =>
        {
            log.Shutdown();         // before the window goes: a flush on its timer after that ends the process
            SaveWorkspace();
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
                bool wasActive = ReferenceEquals(active, tab);
                if (wasActive)
                {
                    active = null;
                    details.Clear();
                    Title = "PuTTY";
                }

                tab.Dispose();      // the tab was closed: its session ends
                UpdateStatus();
                if (wasActive)
                {
                    // The docking library shows the neighbouring tab, and may say so before or after this event: look once it has settled.
                    DispatcherQueue.TryEnqueue(ActivateRemaining);
                }
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
        StartSession(() => new PuttySession(target.Host, target.Port, target.Protocol, options), target.Host, $"{target.Host}:{target.Port}", savedName: null);
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
            window.Closed += (_, _) =>
            {
                tab.Terminal.RefreshFont();         // the font may have been changed (Window > Appearance)
                tab.Terminal.Focus();
            };
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

        if (StartSession(() => PuttySession.OpenSaved(name), name, PuttySessions.Describe(name)?.Description, savedName: name))
        {
            sessions.NoteOpened(name);      // it goes to the top of RECENT
        }
    }

    /// <summary>
    /// Starts a session in a new tab: a document with a terminal view of its own, beside the tabs that are open. false
    /// (after telling the user) if it could not be started. <paramref name="savedName"/> is the name in PuTTY's storage if
    /// it was opened from one (it is opened by that name again when the workspace is restored).
    /// </summary>
    private bool StartSession(Func<PuttySession> create, string title, string? description = null, string? savedName = null)
    {
        SessionTab? tab = CreateTab(create, title, description, savedName, $"doc.session.{++tabCounter}");
        if (tab is null)
        {
            return false;
        }

        DockSite.OpenDocument(tab.Document);
        CloseWelcomeDocument();
        tab.Terminal.Show(tab.Session);
        tab.Document.Activate();
        Activate(tab);
        return true;
    }

    /// <summary>
    /// Makes a tab and starts its session, without putting the document anywhere: <see cref="StartSession"/> opens it in
    /// the document area, and the layout loader places it where it was. Null (after telling the user) if the session could
    /// not be started.
    /// </summary>
    private SessionTab? CreateTab(Func<PuttySession> create, string title, string? description, string? savedName, string documentId)
    {
        PuttySession? session = null;
        TerminalViewController? terminal = null;
        SessionInteraction? interaction = null;
        try
        {
            session = create();
            var host = new UserControl { HorizontalContentAlignment = HorizontalAlignment.Stretch, VerticalContentAlignment = VerticalAlignment.Stretch };
            terminal = new TerminalViewController(host);
            var document = new DocumentWindow { Title = title, Content = host, SerializationId = documentId };
            interaction = new SessionInteraction(session, () => DockSite.XamlRoot, terminal.Focus);
            var tab = new SessionTab(document, host, terminal, session, interaction, title, description) { SavedName = savedName };
            terminal.Resized += () => { if (ReferenceEquals(active, tab)) { UpdateStatus(); } };
            Wire(tab, session);
            session.Start();

            tabs.Add(tab);
            tabOf[document] = tab;
            return tab;
        }
        catch (InvalidOperationException ex)
        {
            interaction?.Dispose();
            terminal?.Dispose();
            session?.Dispose();
            _ = SessionInteraction.ShowMessageAsync(DockSite.XamlRoot, "Could not connect", ex.Message);
            return null;
        }
    }

    // ----- The workspace that comes back ------------------------------------------------------------

    /// <summary>The New session button of the empty state: the same as the one in the command bar.</summary>
    private void OnWelcomeNewSession(object sender, RoutedEventArgs e) => OpenSettings(null);

    /// <summary>
    /// A test hook (see <see cref="TestRegistry.LogFloodVariable"/>): feeds the Connection log a very large number of lines, in
    /// bursts, and says in the diagnostic log how long it took.
    /// </summary>
    private void FloodLogForTest()
    {
        if (!TestRegistry.IsActive
            || !int.TryParse(Environment.GetEnvironmentVariable(TestRegistry.LogFloodVariable), out int total)
            || total <= 0)
        {
            return;
        }

        int sent = 0;
        var watch = System.Diagnostics.Stopwatch.StartNew();
        Microsoft.UI.Dispatching.DispatcherQueueTimer timer = DispatcherQueue.CreateTimer();
        timer.Interval = TimeSpan.FromMilliseconds(10);
        timer.IsRepeating = true;
        timer.Tick += (_, _) =>
        {
            for (int i = 0; i < 2000 && sent < total; i++, sent++)
            {
                log.Add(sent % 2 == 0 ? "flood alpha: a session with a rather long name" : "flood beta", $"Line {sent}: a message long enough to be cut off at the right edge of the panel when it is narrow");
            }

            if (sent >= total)
            {
                timer.Stop();
                PuttyLog.Info("app", $"The connection log was fed {total} lines in {watch.ElapsedMilliseconds} ms");
            }
        };
        timer.Start();
    }

    /// <summary>
    /// Saves how the panels and document groups are laid out, and which session was open in which document, for the
    /// next start. Runs when the window is closing, while everything is still there to read.
    /// </summary>
    internal void SaveWorkspace()
    {
        if (!App.EngineRunning || startedWithTarget)
        {
            return;         // a run started with --connect (tests, development) neither restores nor saves a workspace
        }

        try
        {
            var state = new WorkspaceState
            {
                Layout = new DockSiteLayoutSerializer().SaveToString(DockSite),
                LayoutName = string.Equals(currentLayout, LayoutNames.Default, StringComparison.OrdinalIgnoreCase) ? null : currentLayout,
            };
            state.Sessions.AddRange(OpenSessionRecords());
            workspaceStore.Save(state);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or ObjectDisposedException)
        {
            PuttyLog.Warning("app", $"The workspace could not be saved: {ex.Message}");      // not worth stopping the window from closing
        }
    }

    /// <summary>One record per open tab: its document, its title and how to open it again (by saved name, or by where it connected).</summary>
    private List<OpenSessionRecord> OpenSessionRecords()
    {
        var records = new List<OpenSessionRecord>();
        foreach (SessionTab tab in tabs)
        {
            var record = new OpenSessionRecord { Id = tab.Document.SerializationId ?? string.Empty, Title = tab.Title, Description = tab.Description, SavedName = tab.SavedName };
            if (record.SavedName is null)
            {
                SessionEndpoint endpoint = tab.Session.GetEndpoint();      // opened from the settings window or typed: where it connects
                record.Host = endpoint.Host;
                record.Port = endpoint.Port;
                record.Protocol = ProtocolText(endpoint.Protocol);
                record.UserName = endpoint.UserName;
                record.KeyFile = endpoint.KeyFile;
            }

            records.Add(record);
        }

        return records;
    }

    /// <summary>
    /// Puts the saved layout back and opens the sessions that were open, each in its document. The layout loader asks for
    /// each document by id; a session is made (and connects) for the ones that were sessions. A layout that cannot be
    /// read is ignored: the application starts with its default arrangement and nothing is opened.
    /// </summary>
    private void RestoreWorkspace()
    {
        if (restored || !App.EngineRunning || startedWithTarget)
        {
            return;
        }

        restored = true;
        WorkspaceState? state = workspaceStore.Load();
        if (state?.Layout is not { Length: > 0 } layout)
        {
            return;
        }

        var created = new List<SessionTab>();
        var serializer = new DockSiteLayoutSerializer();
        serializer.DocumentResolving += (_, e) =>
        {
            if (state.Sessions.FirstOrDefault(r => r.Id == e.Id) is { } record && TabFor(record) is { } tab)
            {
                created.Add(tab);
                e.Document = tab.Document;
            }
        };
        try
        {
            serializer.LoadFromString(DockSite, layout);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            PuttyLog.Warning("app", $"The saved layout could not be used: {ex.Message}");
            foreach (SessionTab tab in created)
            {
                tabs.Remove(tab);
                tabOf.Remove(tab.Document);
                tab.Dispose();
            }

            return;
        }

        foreach (SessionTab tab in created)
        {
            tab.Terminal.Show(tab.Session);
            tabCounter = Math.Max(tabCounter, NumberOf(tab.Document.SerializationId ?? string.Empty));
        }

        if (state.LayoutName is { } layoutName && layouts.Find(layoutName) is { } known)
        {
            currentLayout = known.Name;     // the layout in use at the last close
            commands.SetLayouts(layouts.Names, currentLayout);
        }

        if (created.Count > 0)
        {
            welcomeClosed = true;           // the loader closed it: the layout had no place for it
            Activate(DockSite.ActiveDocument is { } shown && tabOf.TryGetValue(shown, out SessionTab? current) ? current : created[^1]);
        }
    }

    /// <summary>A tab for a saved record: opened by its saved name, or by where it connected.</summary>
    private SessionTab? TabFor(OpenSessionRecord record)
    {
        Func<PuttySession>? create = null;
        if (record.SavedName is { } name)
        {
            create = () => PuttySession.OpenSaved(name);
        }
        else if (record.Host is { Length: > 0 } host && ProtocolOf(record.Protocol) is { } protocol)
        {
            var options = record.UserName is null && record.KeyFile is null ? null : new PuttySessionOptions { UserName = record.UserName, KeyFile = record.KeyFile };
            create = () => new PuttySession(host, record.Port, protocol, options);
        }

        return create is null ? null : CreateTab(create, record.Title, record.Description, record.SavedName, record.Id);
    }

    private static string ProtocolText(SavedSessionProtocol protocol) => protocol switch
    {
        SavedSessionProtocol.Raw => "raw",
        SavedSessionProtocol.Telnet => "telnet",
        SavedSessionProtocol.Rlogin => "rlogin",
        SavedSessionProtocol.Ssh => "ssh",
        _ => "other",
    };

    private static SessionProtocol? ProtocolOf(string? text) => text switch
    {
        "raw" => SessionProtocol.Raw,
        "telnet" => SessionProtocol.Telnet,
        "rlogin" => SessionProtocol.Rlogin,
        "ssh" => SessionProtocol.Ssh,
        _ => null,          // serial and the rest are opened only from a saved name
    };

    /// <summary>The number in "doc.session.7", so that new tabs get ids beyond the restored ones; 0 for any other id.</summary>
    private static int NumberOf(string id) => id.StartsWith("doc.session.", StringComparison.Ordinal) && int.TryParse(id["doc.session.".Length..], out int n) ? n : 0;
    /// <summary>What a tab does with its session's events: the server's title for the title bar, and the end of the connection.</summary>
    private void Wire(SessionTab tab, PuttySession session)
    {
        // The connection log: PuTTY's own event log lines and errors, with the tab's name. Subscribed before Start, so the
        // first lines ("Connecting to ...") are there too.
        session.Log += (_, e) =>
        {
            log.Add(tab.Title, e.Text);
            if (ReferenceEquals(tab.Session, session) && tab.Facts.Update(e.Text) && ReferenceEquals(active, tab))
            {
                details.ShowFacts(tab.Facts);       // the key exchange, cipher or host key was just learned
            }
        };
        session.Error += (_, e) => log.Add(tab.Title, e.Message, error: true);
        // The sound (or the visual bell) is the engine's; drawing attention to the window is ours: the session's "bell indicator" setting.
        session.Bell += (_, e) => WindowFlash.ForBell(WinRT.Interop.WindowNative.GetWindowHandle(this), e.Indicator);
        session.CursorMoved += (_, e) =>
        {
            if (ReferenceEquals(active, tab) && ReferenceEquals(tab.Session, session))
            {
                status.SetCursor((e.Y / Math.Max(1, e.Height)) + 1, (e.X / Math.Max(1, e.Width)) + 1);
            }
        };
        session.TitleChanged += (_, e) =>
        {
            if (!ReferenceEquals(tab.Session, session))
            {
                return;         // a session that Reconnect has replaced
            }

            // PuTTY's own default title already ends in " - PuTTY" ("host - PuTTY"); a title the server sets does not.
            tab.WindowTitle = e.Title.EndsWith("PuTTY", StringComparison.Ordinal) ? e.Title : $"{e.Title} - PuTTY";
            if (ReferenceEquals(active, tab))
            {
                Title = tab.WindowTitle;
            }
        };
        session.Closed += (_, _) =>
        {
            if (!ReferenceEquals(tab.Session, session))
            {
                return;
            }

            tab.Connected = false;
            log.Add(tab.Title, "Connection closed");
            if (ReferenceEquals(active, tab))
            {
                details.SetConnected(false);
            }

            UpdateStatus();
        };
    }

    /// <summary>The status bar, and the command bar's enabled buttons: how many sessions are connected, and what the active one is.</summary>
    private void UpdateStatus()
    {
        commands.SetSession(hasSession: active is not null, connected: active is { Connected: true }, tabCount: tabs.Count);
        status.SetSessions(tabs.Count(t => t.Connected));
        if (active is { } tab)
        {
            string? protocol = tab.Description is { } d && d.Contains(MiddleDot) ? d[(d.LastIndexOf(MiddleDot) + 1)..].Trim() : null;
            TerminalGeometry? geometry = tab.Terminal.Geometry;
            status.SetActive(protocol, geometry?.Columns, geometry?.Rows);
        }
        else
        {
            status.ClearActive();
        }
    }

    /// <summary>Another tab with a copy of the active session's settings (everything, including what Change Settings altered).</summary>
    private void Duplicate()
    {
        if (!App.EngineRunning || active is not { } tab)
        {
            return;
        }

        StartSession(() => tab.Session.Clone(), tab.Title, tab.Description, tab.SavedName);
    }

    /// <summary>
    /// The connection of the active tab has ended: connect again in the same tab with the same settings. The new session
    /// is started while the ended one is still there to copy from; then the old one goes, which frees the terminal panel
    /// for the new one's view.
    /// </summary>
    /// <summary>Ends the active tab's connection and leaves the tab, with its screen, as it is; Reconnect can bring it back.</summary>
    private void Disconnect()
    {
        if (active is not { Connected: true } tab)
        {
            return;
        }

        try
        {
            tab.Session.Disconnect();       // raises Closed, which updates the panel, the log and the status bar
        }
        catch (InvalidOperationException ex)
        {
            _ = SessionInteraction.ShowMessageAsync(DockSite.XamlRoot, "Could not disconnect", ex.Message);
        }

        tab.Terminal.Focus();
    }

    private void Reconnect()
    {
        if (!App.EngineRunning || active is not { Connected: false } tab)
        {
            return;
        }

        PuttySession? fresh = null;
        SessionInteraction? interaction = null;
        try
        {
            fresh = tab.Session.Clone();
            interaction = new SessionInteraction(fresh, () => DockSite.XamlRoot, tab.Terminal.Focus);
            Wire(tab, fresh);
            fresh.Start();
            tab.Replace(fresh, interaction);
            tab.Terminal.Show(fresh);
            details.SetConnected(true);
            UpdateStatus();
        }
        catch (InvalidOperationException ex)
        {
            interaction?.Dispose();
            fresh?.Dispose();
            _ = SessionInteraction.ShowMessageAsync(DockSite.XamlRoot, "Could not reconnect", ex.Message);
        }
    }

    /// <summary>The active tab was closed: whichever tab the docking library now shows becomes the active one, if there is one.</summary>
    private void ActivateRemaining()
    {
        if (active is not null)
        {
            return;         // the library's own WindowActivated already did it
        }

        SessionTab? next = DockSite.ActiveDocument is { } document && tabOf.TryGetValue(document, out SessionTab? shown) ? shown : tabs.LastOrDefault();
        if (next is not null)
        {
            Activate(next);
        }
    }

    /// <summary>A tab became the active one: the details panel, the title bar and the keyboard follow it.</summary>
    private void Activate(SessionTab tab)
    {
        active = tab;
        details.Show(tab.Title, tab.Description);
        details.SetConnected(tab.Connected);
        details.ShowFacts(tab.Facts);
        try
        {
            details.ShowEndpoint(tab.Session.GetEndpoint());
        }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException)
        {
            details.ShowEndpoint(null);         // the session is gone: nothing to say about where it was
        }

        Title = tab.WindowTitle;
        UpdateStatus();
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