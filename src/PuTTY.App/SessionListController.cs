using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using PuTTY.Interop;
using PuTTY.Sessions;

namespace PuTTY.App;

/// <summary>
/// The Sessions panel: PuTTY's saved sessions as a tree of folders (the sketch's "SAVED SESSIONS"), the ones
/// opened lately ("RECENT"), a search box that flattens the tree to its matches, and the buttons to create, edit
/// and open. The sessions come from PuTTY's own storage (the registry, read only: what PuTTY classic saved shows
/// up without any import); the folders, and which session is in which, are this application's own file
/// (<see cref="MetadataStore"/>, never PuTTY's storage). Opening is a double click, Enter, the button, or a click
/// in RECENT; the menus (right click, or the Menu key) make and rename folders and move sessions between them.
/// </summary>
/// <remarks>
/// <para>
/// Built from code on a plain <see cref="UserControl"/>, like <see cref="TerminalViewController"/>: no
/// subclass of a control, every control constructed here (the NativeAOT rules in CLAUDE.md).
/// </para>
/// <para>
/// The tree is a <see cref="ListView"/> of the visible rows, not a <c>TreeView</c>: a <c>TreeView</c> shows only
/// text for a node's content (a row built from code came out as the node's type name) and names its items after
/// the node, so neither the look of the sketch nor UI Automation names and ids can be had from it. Here every
/// row is a <see cref="ListViewItem"/> made in this file, with its own indent, chevron (a real button, so that
/// UI Automation can press it), name and id.
/// </para>
/// </remarks>
internal sealed class SessionListController
{
    /// <summary>The AutomationIds, for UI Automation checks (tools/app-sessions-smoke.ps1).</summary>
    public const string SearchId = "SessionSearch";
    public const string ListId = "SessionList";
    public const string OpenId = "OpenSessionButton";
    public const string NewId = "NewSessionButton";
    public const string EditId = "SessionSettingsButton";
    public const string AddId = "AddToSessionsButton";
    public const string RecentId = "RecentSessions";
    public const string FolderNameId = "FolderNameBox";

    /// <summary>A row's AutomationId is <c>Session:</c> and the session's name, <c>Folder:</c> and the folder's name, <c>Recent:</c> and the name.</summary>
    public const string SessionIdPrefix = "Session:";
    public const string FolderIdPrefix = "Folder:";
    public const string RecentIdPrefix = "Recent:";
    public const string ToggleIdPrefix = "Toggle:";

    private const int RecentShown = 5;
    private const double Indent = 18;

    private enum Kind { Folder, Session }

    /// <summary>What a row stands for: a folder (<see cref="Key"/> is its id) or a session (its name).</summary>
    private sealed record Entry(Kind Kind, string Key, string Name);

    private readonly UserControl host;
    private readonly MetadataStore store = new(TestRegistry.WorkspaceFileOverride ?? MetadataStore.DefaultPath);
    private SessionMetadata metadata = new();

    private readonly TextBox search = new();
    private readonly ListView list = new();
    private readonly ListView recent = new();
    private readonly StackPanel recentSection = new();
    private readonly Button open = new();
    private readonly Button create = new();
    private readonly Button edit = new();
    private readonly Button add = new();
    private readonly MenuFlyoutItem newFolder = new();
    private readonly TextBlock status = new();

    private readonly List<ListViewItem> rows = [];                      // the rows in the order they are shown
    private readonly Dictionary<ListViewItem, Entry> entries = [];
    private readonly Dictionary<ListViewItem, string> recentNames = [];
    private readonly HashSet<string> collapsed = new(StringComparer.Ordinal);      // folder ids the user closed
    private readonly Dictionary<string, SavedSessionSummary> summaries = new(StringComparer.Ordinal);
    private string[] all = [];     // an array, not IReadOnlyList: CsWinRT rejects a collection expression for that (CsWinRT1032)
    private bool rebuilding;

    public SessionListController(UserControl host)
    {
        this.host = host;

        search.PlaceholderText = "Search sessions";
        search.TextChanged += (_, _) => RebuildRows(selectFirst: search.Text.Trim().Length > 0);
        AutomationProperties.SetAutomationId(search, SearchId);
        AutomationProperties.SetName(search, "Search sessions");

        list.SelectionMode = ListViewSelectionMode.Single;
        list.IsDoubleTapEnabled = true;
        list.DoubleTapped += (_, _) => ActivateSelected();
        list.KeyDown += OnListKeyDown;
        list.SelectionChanged += (_, _) => { if (!rebuilding) { UpdateButtons(); } };
        AutomationProperties.SetAutomationId(list, ListId);
        AutomationProperties.SetName(list, "Saved sessions");

        recent.SelectionMode = ListViewSelectionMode.None;
        recent.IsItemClickEnabled = true;
        recent.MaxHeight = RecentShown * 48;
        recent.ItemClick += (_, e) =>
        {
            if (e.ClickedItem is ListViewItem item && recentNames.TryGetValue(item, out string? name))
            {
                OpenRequested?.Invoke(name);
            }
        };
        AutomationProperties.SetAutomationId(recent, RecentId);
        AutomationProperties.SetName(recent, "Recent sessions");

        open.Content = "Open";
        open.IsEnabled = false;
        open.Click += (_, _) => OpenSelected();
        AutomationProperties.SetAutomationId(open, OpenId);
        AutomationProperties.SetName(open, "Open saved session");      // the settings window has an "Open session" of its own

        create.Content = "New...";
        create.Click += (_, _) => NewRequested?.Invoke();
        AutomationProperties.SetAutomationId(create, NewId);

        edit.Content = "Edit...";
        edit.IsEnabled = false;
        edit.Click += (_, _) => { if (Selected() is { Kind: Kind.Session } entry) { EditRequested?.Invoke(entry.Name); } };
        AutomationProperties.SetAutomationId(edit, EditId);
        AutomationProperties.SetName(edit, "Session settings");

        status.Opacity = 0.7;
        status.VerticalAlignment = VerticalAlignment.Center;
        status.TextTrimming = TextTrimming.CharacterEllipsis;

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(create);
        buttons.Children.Add(edit);
        buttons.Children.Add(open);

        // "SAVED SESSIONS" and the plus that adds to it
        var caption = new TextBlock { Text = "SAVED SESSIONS", FontSize = 11, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(2, 0, 0, 0) };
        Dim(caption);
        var newSession = new MenuFlyoutItem { Text = "New session..." };
        newSession.Click += (_, _) => NewRequested?.Invoke();
        newFolder.Text = "New folder...";
        newFolder.Click += (_, _) => _ = AskNewFolderAsync(null);
        var addMenu = new MenuFlyout();
        addMenu.Items.Add(newSession);
        addMenu.Items.Add(newFolder);
        add.Content = Icon(Glyph.Add, 14);
        add.Flyout = addMenu;
        add.Padding = new Thickness(6);
        add.Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        add.BorderThickness = new Thickness(0);
        AutomationProperties.SetAutomationId(add, AddId);
        AutomationProperties.SetName(add, "Add session or folder");
        var header = new Grid { Margin = new Thickness(0, 4, 0, 4) };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.Children.Add(caption);
        Grid.SetColumn(add, 1);
        header.Children.Add(add);

        // "RECENT": a divider, the caption, the list
        var recentCaption = new TextBlock { Text = "RECENT", FontSize = 11, Margin = new Thickness(2, 8, 0, 4) };
        Dim(recentCaption);
        var divider = new Border { Height = 1, Margin = new Thickness(0, 8, 0, 0) };
        if (ThemeBrush("DividerStrokeColorDefaultBrush") is { } line)
        {
            divider.Background = line;
        }

        recentSection.Visibility = Visibility.Collapsed;
        recentSection.Children.Add(divider);
        recentSection.Children.Add(recentCaption);
        recentSection.Children.Add(recent);

        var layout = new Grid { Margin = new Thickness(12) };
        for (int row = 0; row < 6; row++)
        {
            layout.RowDefinitions.Add(new RowDefinition { Height = row == 2 ? new GridLength(1, GridUnitType.Star) : GridLength.Auto });
        }

        layout.Children.Add(search);
        Grid.SetRow(header, 1);
        layout.Children.Add(header);
        Grid.SetRow(list, 2);
        layout.Children.Add(list);
        Grid.SetRow(recentSection, 3);
        layout.Children.Add(recentSection);
        Grid.SetRow(status, 4);
        status.Margin = new Thickness(0, 8, 0, 4);
        layout.Children.Add(status);
        Grid.SetRow(buttons, 5);
        layout.Children.Add(buttons);
        host.Content = layout;
    }

    /// <summary>The user asked to open the session with this name.</summary>
    public event Action<string>? OpenRequested;

    /// <summary>The user asked for the settings window for a new session.</summary>
    public event Action? NewRequested;

    /// <summary>The user asked to see and change the settings of the session with this name.</summary>
    public event Action<string>? EditRequested;

    /// <summary>Asks for a name and makes a folder at the top (the command bar's New folder).</summary>
    public void RequestNewFolder() => _ = AskNewFolderAsync(null);

    /// <summary>Reads the saved sessions and the folders again. Call it on the engine thread, with the engine running.</summary>
    public void Reload()
    {
        try
        {
            all = [.. PuttySessions.List()
                .Where(name => !name.Equals(PuttySessions.DefaultSettingsName, StringComparison.Ordinal))
                .Order(StringComparer.CurrentCultureIgnoreCase)];
            summaries.Clear();
            foreach (string name in all)
            {
                if (PuttySessions.Describe(name) is { } summary)
                {
                    summaries[name] = summary;
                }
            }

            status.Text = all.Length == 0 ? "No saved PuTTY sessions" : $"{all.Length} saved session{(all.Length == 1 ? string.Empty : "s")}";
        }
        catch (Exception ex) when (ex is InvalidOperationException or DllNotFoundException)
        {
            all = [];
            summaries.Clear();
            status.Text = "Sessions are unavailable: " + ex.Message;
        }

        metadata = store.Load();
        if (store.LoadProblem is { } problem)
        {
            status.Text += " - " + problem;
        }

        newFolder.IsEnabled = !metadata.IsReadOnly;
        RebuildRows(selectFirst: false);
        RebuildRecent();
    }

    /// <summary>A session was opened: it goes to the top of RECENT. Not being able to remember that is not worth a message.</summary>
    public void NoteOpened(string name)
    {
        if (metadata.IsReadOnly || Array.IndexOf(all, name) < 0)
        {
            return;
        }

        try
        {
            metadata.RecordOpened(name, DateTimeOffset.Now);
            store.Save(metadata);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            PuttyLog.Warning("app", $"Could not remember that {name} was opened: {ex.Message}");
        }

        RebuildRecent();
    }

    // ----- The rows --------------------------------------------------------------------------

    private Entry? Selected()
    {
        int index = list.SelectedIndex;
        return index >= 0 && index < rows.Count && entries.TryGetValue(rows[index], out Entry? entry) ? entry : null;
    }

    private void UpdateButtons() => open.IsEnabled = edit.IsEnabled = Selected()?.Kind == Kind.Session;

    private void RebuildRows(bool selectFirst)
    {
        Entry? keep = Selected();
        rebuilding = true;
        try
        {
            list.Items.Clear();
            rows.Clear();
            entries.Clear();

            string text = search.Text.Trim();
            if (text.Length == 0)
            {
                AddLevel(metadata.BuildTree(all), depth: 0);
            }
            else
            {
                // Searching flattens: the sessions whose name, host or folder matches, with no folders to open first.
                foreach (string name in all)
                {
                    string folder = FolderPathOf(name);
                    summaries.TryGetValue(name, out SavedSessionSummary? summary);
                    if (name.Contains(text, StringComparison.CurrentCultureIgnoreCase)
                        || (summary?.Host.Contains(text, StringComparison.CurrentCultureIgnoreCase) ?? false)
                        || folder.Contains(text, StringComparison.CurrentCultureIgnoreCase))
                    {
                        string? detail = summary?.Description;
                        AddSession(name, depth: 0, indentForChevron: false, folder.Length == 0 ? detail : $"{detail} · {folder}");
                    }
                }
            }

            int target = keep is null ? -1 : rows.FindIndex(row => entries[row] == keep);
            if (target < 0 && selectFirst)
            {
                target = rows.FindIndex(row => entries[row].Kind == Kind.Session);    // typing narrows the list to the one you mean: Enter opens it
            }

            if (target >= 0)
            {
                list.SelectedIndex = target;
            }
        }
        finally
        {
            rebuilding = false;
        }

        UpdateButtons();
    }

    /// <summary>The folders under <paramref name="node"/> (each with what is in it, if open), then its sessions.</summary>
    private void AddLevel(SessionTreeNode node, int depth)
    {
        foreach (SessionTreeNode folder in node.Folders)
        {
            bool expanded = !collapsed.Contains(folder.Id!);
            AddFolder(folder, depth, expanded);
            if (expanded)
            {
                AddLevel(folder, depth + 1);
            }
        }

        foreach (string name in node.Sessions)
        {
            summaries.TryGetValue(name, out SavedSessionSummary? summary);
            AddSession(name, depth, indentForChevron: true, summary?.Description);
        }
    }

    private void AddFolder(SessionTreeNode folder, int depth, bool expanded)
    {
        string id = folder.Id!;
        var grid = new Grid { ColumnSpacing = 6, MinHeight = 32, Margin = new Thickness(depth * Indent, 0, 0, 0) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        // The chevron is a button so that it can be pressed by keyboard and by UI Automation, not only by a click.
        var chevron = new Button
        {
            Content = Icon(expanded ? Glyph.ChevronDown : Glyph.ChevronRight, 10),
            Padding = new Thickness(6),
            MinWidth = 0,
            MinHeight = 0,
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            BorderThickness = new Thickness(0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        AutomationProperties.SetAutomationId(chevron, ToggleIdPrefix + folder.Name);
        AutomationProperties.SetName(chevron, $"{(expanded ? "Collapse" : "Expand")} {folder.Name}");
        chevron.Click += (_, _) => Toggle(id);
        grid.Children.Add(chevron);

        var icon = Icon(Glyph.Folder, 16);
        Grid.SetColumn(icon, 1);
        grid.Children.Add(icon);
        var text = new TextBlock { Text = folder.Name, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        Grid.SetColumn(text, 2);
        grid.Children.Add(text);
        var number = new TextBlock { Text = CountSessions(folder).ToString(System.Globalization.CultureInfo.CurrentCulture), FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 4, 0) };
        Dim(number);
        Grid.SetColumn(number, 3);
        grid.Children.Add(number);

        AddRow(new Entry(Kind.Folder, id, folder.Name), grid, FolderIdPrefix + folder.Name);
    }

    private void AddSession(string name, int depth, bool indentForChevron, string? detail)
    {
        // Under a folder the session lines up with the folder's name, which sits after the chevron.
        double left = depth * Indent + (indentForChevron ? 24 : 0);
        var grid = new Grid { ColumnSpacing = 10, MinHeight = 44, Margin = new Thickness(left, 0, 0, 0) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.Children.Add(Icon(Glyph.Terminal, 16));
        var lines = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        lines.Children.Add(new TextBlock { Text = name, TextTrimming = TextTrimming.CharacterEllipsis });
        if (!string.IsNullOrEmpty(detail))
        {
            var second = new TextBlock { Text = detail, FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis };
            Dim(second);
            lines.Children.Add(second);
        }

        Grid.SetColumn(lines, 1);
        grid.Children.Add(lines);
        AddRow(new Entry(Kind.Session, name, name), grid, SessionIdPrefix + name);
    }

    private void AddRow(Entry entry, FrameworkElement content, string automationId)
    {
        var item = new ListViewItem { Content = content, Padding = new Thickness(4, 0, 4, 0) };
        AutomationProperties.SetName(item, entry.Name);
        AutomationProperties.SetAutomationId(item, automationId);
        entries[item] = entry;
        rows.Add(item);
        item.ContextRequested += (_, e) => ShowMenu(item, e);
        list.Items.Add(item);
    }

    private void Toggle(string folderId)
    {
        if (!collapsed.Remove(folderId))
        {
            collapsed.Add(folderId);
        }

        RebuildRows(selectFirst: false);
    }

    private static int CountSessions(SessionTreeNode folder) => folder.Sessions.Count + folder.Folders.Sum(CountSessions);

    private string FolderPathOf(string session) =>
        metadata.FindSession(session)?.FolderId is { } id && metadata.FindFolder(id) is not null ? PathOf(id) : string.Empty;

    /// <summary>"Production / Web": the folder's name after those of the folders around it.</summary>
    private string PathOf(string folderId)
    {
        var parts = new List<string>();
        int steps = 0;
        for (string? id = folderId; id is not null && steps++ <= metadata.Folders.Count; id = metadata.FindFolder(id)?.ParentId)
        {
            if (metadata.FindFolder(id) is { } folder)
            {
                parts.Insert(0, folder.Name);
            }
        }

        return string.Join(" / ", parts);
    }

    // ----- Recent ----------------------------------------------------------------------------

    private void RebuildRecent()
    {
        recent.Items.Clear();
        recentNames.Clear();
        foreach (string name in metadata.Recents(all, RecentShown))
        {
            DateTimeOffset when = metadata.FindSession(name)?.LastOpened ?? default;
            var grid = new Grid { ColumnSpacing = 10, MinHeight = 44 };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.Children.Add(Icon(Glyph.Recent, 15));
            var lines = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            lines.Children.Add(new TextBlock { Text = name, TextTrimming = TextTrimming.CharacterEllipsis });
            var time = new TextBlock { Text = When(when), FontSize = 12 };
            Dim(time);
            lines.Children.Add(time);
            Grid.SetColumn(lines, 1);
            grid.Children.Add(lines);

            var item = new ListViewItem { Content = grid, Padding = new Thickness(4, 0, 4, 0) };
            AutomationProperties.SetName(item, name);
            AutomationProperties.SetAutomationId(item, RecentIdPrefix + name);
            recentNames[item] = name;
            recent.Items.Add(item);
        }

        recentSection.Visibility = recent.Items.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>"Today, 09:41", "Yesterday", the weekday within the last week, else the date.</summary>
    internal static string When(DateTimeOffset moment)
    {
        DateTime local = moment.ToLocalTime().DateTime;
        DateTime today = DateTime.Today;
        if (local.Date == today)
        {
            return $"Today, {local:t}";
        }

        if (local.Date == today.AddDays(-1))
        {
            return "Yesterday";
        }

        return (today - local.Date).TotalDays < 7 ? local.ToString("dddd") : local.ToString("d");
    }

    // ----- Opening, keys --------------------------------------------------------------------

    private void OnListKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (Selected() is not { } entry)
        {
            return;
        }

        switch (e.Key)
        {
            case Windows.System.VirtualKey.Enter:
                ActivateSelected();
                e.Handled = true;
                break;
            case Windows.System.VirtualKey.Right when entry.Kind == Kind.Folder && collapsed.Contains(entry.Key):
            case Windows.System.VirtualKey.Left when entry.Kind == Kind.Folder && !collapsed.Contains(entry.Key):
                Toggle(entry.Key);
                e.Handled = true;
                break;
        }
    }

    /// <summary>Enter and a double click: a session opens, a folder opens or closes.</summary>
    private void ActivateSelected()
    {
        if (Selected() is { Kind: Kind.Folder } folder)
        {
            Toggle(folder.Key);
        }
        else
        {
            OpenSelected();
        }
    }

    private void OpenSelected()
    {
        if (Selected() is { Kind: Kind.Session } entry)
        {
            OpenRequested?.Invoke(entry.Name);
        }
    }

    // ----- Menus and folders ----------------------------------------------------------------

    private void ShowMenu(ListViewItem row, ContextRequestedEventArgs e)
    {
        if (!entries.TryGetValue(row, out Entry? entry))
        {
            return;
        }

        list.SelectedIndex = rows.IndexOf(row);
        MenuFlyout menu = BuildMenu(entry);
        if (e.TryGetPosition(row, out Windows.Foundation.Point point))
        {
            menu.ShowAt(row, point);
        }
        else
        {
            menu.ShowAt(row);
        }

        e.Handled = true;
    }

    private MenuFlyout BuildMenu(Entry entry)
    {
        var menu = new MenuFlyout();
        bool editable = !metadata.IsReadOnly;
        if (entry.Kind == Kind.Session)
        {
            menu.Items.Add(Command("Open", () => OpenRequested?.Invoke(entry.Name)));
            menu.Items.Add(Command("Settings...", () => EditRequested?.Invoke(entry.Name)));

            var move = new MenuFlyoutSubItem { Text = "Move to folder", IsEnabled = editable };
            string? current = metadata.FindSession(entry.Name)?.FolderId;
            if (current is not null && metadata.FindFolder(current) is null)
            {
                current = null;     // in a folder this file no longer has: it is shown at the top
            }

            move.Items.Add(Command("(top level)", () => Change(m => m.MoveSession(entry.Name, null)), enabled: current is not null));
            foreach (FolderEntry folder in metadata.Folders.OrderBy(f => PathOf(f.Id), StringComparer.CurrentCultureIgnoreCase))
            {
                string id = folder.Id;
                move.Items.Add(Command(PathOf(id), () => Change(m => m.MoveSession(entry.Name, id)), enabled: id != current));
            }

            menu.Items.Add(move);
        }
        else
        {
            menu.Items.Add(Command("New folder inside...", () => _ = AskNewFolderAsync(entry.Key), enabled: editable));
            menu.Items.Add(Command("Rename...", () => _ = AskRenameFolderAsync(entry), enabled: editable));
            menu.Items.Add(Command("Delete folder", () => _ = AskDeleteFolderAsync(entry), enabled: editable));
        }

        return menu;
    }

    private static MenuFlyoutItem Command(string text, Action run, bool enabled = true)
    {
        var item = new MenuFlyoutItem { Text = text, IsEnabled = enabled };
        item.Click += (_, _) => run();
        return item;
    }

    /// <summary>Applies a change to the folders and saves it; a change that cannot be made or saved is told to the user and undone by reading the file again.</summary>
    private void Change(Action<SessionMetadata> change)
    {
        try
        {
            change(metadata);
            store.Save(metadata);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            _ = SessionInteraction.ShowMessageAsync(host.XamlRoot, "The change could not be made", ex.Message);
            metadata = store.Load();
        }

        RebuildRows(selectFirst: false);
        RebuildRecent();
    }

    private Task AskNewFolderAsync(string? parentId) =>
        AskFolderNameAsync(parentId is null ? "New folder" : $"New folder in \"{metadata.FindFolder(parentId)?.Name}\"", "Create", string.Empty,
            name => Apply(m => m.CreateFolder(name, parentId)));

    private Task AskRenameFolderAsync(Entry folder) =>
        AskFolderNameAsync("Rename folder", "Rename", folder.Name, name => Apply(m => m.RenameFolder(folder.Key, name)));

    /// <summary>Makes a change for a dialog: null if it worked, else the words to show in the dialog, which stays open.</summary>
    private string? Apply(Action<SessionMetadata> change)
    {
        try
        {
            change(metadata);
            return null;
        }
        catch (InvalidOperationException ex)
        {
            return ex.Message;
        }
        catch (ArgumentException)
        {
            return "Type a name for the folder.";
        }
    }

    private async Task AskFolderNameAsync(string title, string accept, string initial, Func<string, string?> apply)
    {
        var box = new TextBox { Text = initial, MinWidth = 320, PlaceholderText = "Folder name" };
        AutomationProperties.SetAutomationId(box, FolderNameId);
        AutomationProperties.SetName(box, "Folder name");
        var problem = new TextBlock { TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed };
        if (ThemeBrush("SystemFillColorCriticalBrush") is { } critical)
        {
            problem.Foreground = critical;
        }

        var content = new StackPanel { Spacing = 8 };
        content.Children.Add(box);
        content.Children.Add(problem);
        var dialog = new ContentDialog
        {
            XamlRoot = host.XamlRoot,
            Title = title,
            Content = content,
            PrimaryButtonText = accept,
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
        };
        bool done = false;
        dialog.PrimaryButtonClick += (_, args) =>
        {
            if (apply(box.Text) is { } words)
            {
                problem.Text = words;
                problem.Visibility = Visibility.Visible;
                args.Cancel = true;     // stays open so the name can be fixed
            }
            else
            {
                done = true;
            }
        };
        dialog.Opened += (_, _) =>
        {
            box.Focus(FocusState.Programmatic);
            box.SelectAll();
        };
        try
        {
            await dialog.ShowAsync();
        }
        catch (Exception ex)
        {
            PuttyLog.Warning("app", $"Dialog failed: {ex.Message}");      // another dialog is open
            metadata = store.Load();
            RebuildRows(selectFirst: false);
            return;
        }

        if (done)
        {
            Change(_ => { });       // the change is made already; this saves it and redraws
        }
    }

    private async Task AskDeleteFolderAsync(Entry folder)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = host.XamlRoot,
            Title = $"Delete the folder \"{folder.Name}\"?",
            Content = "The sessions and folders in it move up one level. No saved session is deleted.",
            PrimaryButtonText = "Delete folder",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
        };
        try
        {
            if (await dialog.ShowAsync() == ContentDialogResult.Primary)
            {
                Change(m => m.DeleteFolder(folder.Key));
            }
        }
        catch (Exception ex)
        {
            PuttyLog.Warning("app", $"Dialog failed: {ex.Message}");
        }
    }

    // ----- Theme ----------------------------------------------------------------------------

    /// <summary>Code points of Segoe Fluent Icons (Segoe MDL2 Assets before Windows 11), the icon font the theme uses. Built from numbers: a private-use character does not survive being typed into a source file.</summary>
    private static class Glyph
    {
        public static readonly string Add = char.ConvertFromUtf32(0xE710);
        public static readonly string Folder = char.ConvertFromUtf32(0xE8B7);
        public static readonly string Terminal = char.ConvertFromUtf32(0xE756);
        public static readonly string Recent = char.ConvertFromUtf32(0xE823);
        public static readonly string ChevronDown = char.ConvertFromUtf32(0xE70D);
        public static readonly string ChevronRight = char.ConvertFromUtf32(0xE76C);
    }

    private static FontIcon Icon(string glyph, double size) =>
        new() { Glyph = glyph, FontSize = size, VerticalAlignment = VerticalAlignment.Center };

    private static Brush? ThemeBrush(string key) =>
        Application.Current.Resources.TryGetValue(key, out object? value) ? value as Brush : null;

    private static void Dim(TextBlock text)
    {
        if (ThemeBrush("TextFillColorSecondaryBrush") is { } brush)
        {
            text.Foreground = brush;
        }
    }
}
