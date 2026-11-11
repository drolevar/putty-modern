using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using PuTTY.Interop;
using Windows.UI;

namespace PuTTY.App.Settings;

/// <summary>
/// Draws PuTTY's configuration box from the description the engine gives (<see cref="SettingsDialog"/>), laid
/// out as in the Figma sketch "Session configuration": a navigation pane (title, "Find a setting", a tree of
/// the pages) on the left; on the right a breadcrumb, the page title and subtitle, one card per group of
/// controls, and a bar with Restore defaults, Cancel, Save and Open session.
/// Nothing here knows what a setting means: PuTTY's own handlers keep the controls consistent (choosing a
/// protocol changes the port), and this class only shows the state it reads and reports what the user does.
/// </summary>
/// <remarks>
/// Built from code, with no XAML and no subclass of a control, and every control constructed here, which is
/// what NativeAOT needs (CLAUDE.md). The look is the theme's: standard Fluent controls and theme brushes, which
/// is what the sketch is drawn with. Pages are built the first time they are shown and then kept; a change to
/// a control on a page that was never shown costs nothing, because building the page reads the state.
/// </remarks>
internal sealed partial class SettingsView
{
    private const string RootTitle = "Configuration";
    private const string DefaultSettings = "Default Settings";

    private readonly SettingsDialog dialog;
    private readonly Func<XamlRoot?> xamlRoot;
    private readonly Func<nint> windowHandle;
    private readonly Dictionary<int, Widget> widgets = [];
    private readonly Dictionary<string, FrameworkElement> builtPages = [];
    private readonly List<string> pagePaths = [];
    private readonly Dictionary<string, TreeViewNode> nodeOfPath = [];
    private readonly Dictionary<TreeViewNode, string> pathOfNode = [];
    private readonly List<SearchEntry> searchIndex = [];
    private readonly Dictionary<string, SearchEntry> searchByDisplay = [];
    private readonly TreeView tree = new();
    private readonly AutoSuggestBox search = new();
    private readonly BreadcrumbBar breadcrumb = new();
    private string[]? hiddenLabels;        // labels left out of the group being built (Presentation.Card.HiddenLabels)
    private readonly TextBlock pageTitle = new();
    private readonly TextBlock pageSubtitle = new();
    private readonly StackPanel pageContent = new();
    private readonly ScrollViewer pageHost = new();
    private readonly Grid root = new();
    private bool updating;
    private bool navigating;
    private string? currentPath;

    /// <summary>What the host needs to keep per control to bring it up to date.</summary>
    private sealed class Widget(SettingsControl control)
    {
        public SettingsControl Control { get; } = control;

        public FrameworkElement? Root { get; set; }

        public Action Refresh { get; set; } = () => { };

        public Action<string>? SetLabel { get; set; }

        public Func<bool>? Focus { get; set; }
    }

    private sealed record SearchEntry(string Display, string Path, int ControlId);

    public SettingsView(SettingsDialog dialog, Func<XamlRoot?> xamlRoot, Func<nint> windowHandle)
    {
        this.dialog = dialog;
        this.xamlRoot = xamlRoot;
        this.windowHandle = windowHandle;

        dialog.ControlChanged += OnControlChanged;
        dialog.LabelChanged += (id, text) => { if (widgets.TryGetValue(id, out Widget? w)) { w.SetLabel?.Invoke(text); } };
        dialog.MessageRaised += (text, isError) => _ = SessionInteraction.ShowMessageAsync(xamlRoot(), isError ? "Error" : "PuTTY", text);
        dialog.Beeped += () => MessageBeep(0);
        dialog.FocusRequested += id => { if (widgets.TryGetValue(id, out Widget? w)) { w.Focus?.Invoke(); } };
        dialog.ColourRequested += (id, r, g, b) => _ = ChooseColourAsync(id, r, g, b);
        dialog.Ended += go => Ended?.Invoke(go);

        FindPages();
        BuildSearchIndex();
        Layout();
        Navigate(pagePaths.Count > 0 ? pagePaths[0] : string.Empty);
    }

    /// <summary>The whole dialog.</summary>
    public FrameworkElement Root => root;

    /// <summary>The dialog is over (Open or Cancel, or a double-clicked saved session): true to go ahead.</summary>
    public event Action<bool>? Ended;

    // ----- Theme -----------------------------------------------------------------------------------

    /// <summary>A brush of the theme (WinUI's resources), or null if this theme has none of that name.</summary>
    private static Brush? ThemeBrush(string key) =>
        Application.Current.Resources.TryGetValue(key, out object? value) ? value as Brush : null;

    private static TextBlock Secondary(string text, double size = 12)
    {
        var block = new TextBlock { Text = text, FontSize = size, TextWrapping = TextWrapping.Wrap };
        if (ThemeBrush("TextFillColorSecondaryBrush") is { } brush)
        {
            block.Foreground = brush;
        }

        return block;
    }

    // ----- Frame -------------------------------------------------------------------------------------

    private void Layout()
    {
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(256) });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        Grid navigation = BuildNavigation();
        root.Children.Add(navigation);

        var content = new Grid();
        content.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetColumn(content, 1);
        root.Children.Add(content);

        // The header (breadcrumb, title, subtitle) scrolls with the cards, as the page is one column.
        breadcrumb.ItemClicked += (_, e) => OnBreadcrumbClicked(e.Index);
        pageTitle.FontSize = 28;
        pageTitle.FontWeight = FontWeights.SemiBold;
        pageTitle.Margin = new Thickness(0, 6, 0, 0);
        pageSubtitle.FontSize = 14;
        pageSubtitle.TextWrapping = TextWrapping.Wrap;
        if (ThemeBrush("TextFillColorSecondaryBrush") is { } secondary)
        {
            pageSubtitle.Foreground = secondary;
        }

        pageContent.Spacing = 12;
        pageContent.Margin = new Thickness(0, 24, 0, 0);
        var page = new StackPanel { Margin = new Thickness(32, 12, 32, 24), MaxWidth = 864 };
        page.Children.Add(breadcrumb);
        page.Children.Add(pageTitle);
        page.Children.Add(pageSubtitle);
        page.Children.Add(pageContent);
        pageHost.Content = page;
        pageHost.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
        content.Children.Add(pageHost);

        FrameworkElement actions = BuildActions();
        Grid.SetRow(actions, 1);
        content.Children.Add(actions);
    }

    /// <summary>The navigation pane: "Settings", "Find a setting", the PAGES caption, the tree, About and Help.</summary>
    private Grid BuildNavigation()
    {
        // Rows: the title, the search box, the PAGES caption, the tree (takes what is left), About and Help.
        var nav = new Grid();
        nav.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        nav.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        nav.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        nav.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        nav.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var title = new TextBlock { Text = "Settings", FontSize = 25, FontWeight = FontWeights.SemiBold, Margin = new Thickness(24, 14, 0, 0) };
        nav.Children.Add(title);

        search.PlaceholderText = "Find a setting";
        search.QueryIcon = new SymbolIcon(Symbol.Find);
        search.Margin = new Thickness(16, 16, 16, 0);
        AutomationProperties.SetAutomationId(search, "SettingsSearch");
        AutomationProperties.SetName(search, "Find a setting");
        search.TextChanged += OnSearchTextChanged;
        search.QuerySubmitted += OnSearchSubmitted;
        Grid.SetRow(search, 1);
        nav.Children.Add(search);

        var caption = new TextBlock { Text = "PAGES", FontSize = 10, FontWeight = FontWeights.SemiBold, Margin = new Thickness(18, 16, 0, 4) };
        if (ThemeBrush("TextFillColorSecondaryBrush") is { } secondary)
        {
            caption.Foreground = secondary;
        }

        Grid.SetRow(caption, 2);
        nav.Children.Add(caption);

        tree.SelectionMode = TreeViewSelectionMode.Single;
        tree.Margin = new Thickness(8, 0, 0, 0);
        AutomationProperties.SetAutomationId(tree, "SettingsPages");
        AutomationProperties.SetName(tree, "Pages");
        BuildTree();
        tree.SelectionChanged += (_, _) => OnTreeSelectionChanged();
        Grid.SetRow(tree, 3);
        nav.Children.Add(tree);

        var footer = new Grid { Margin = new Thickness(24, 10, 24, 12) };
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        footer.Children.Add(Secondary("About PuTTY"));
        var help = new HyperlinkButton
        {
            Content = "Help",
            Padding = new Thickness(0),
            NavigateUri = new Uri("https://the.earth.li/~sgtatham/putty/latest/htmldoc/"),
        };
        Grid.SetColumn(help, 1);
        footer.Children.Add(help);
        Grid.SetRow(footer, 4);
        nav.Children.Add(footer);
        return nav;
    }

    /// <summary>The bar under the page: Restore defaults on the left; Cancel, Save and Open session on the right.</summary>
    private FrameworkElement BuildActions()
    {
        var bar = new Grid { Padding = new Thickness(32, 0, 32, 0), Height = 76 };
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var restore = new HyperlinkButton { Content = "Restore defaults", Padding = new Thickness(0), VerticalAlignment = VerticalAlignment.Center };
        AutomationProperties.SetAutomationId(restore, "RestoreDefaults");
        restore.Click += (_, _) =>
        {
            // PuTTY has no such button: it is the Load of "Default Settings", which is always in the list.
            dialog.LoadSavedSession(DefaultSettings);
        };
        bar.Children.Add(restore);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, VerticalAlignment = VerticalAlignment.Center };
        foreach (SettingsGroup group in dialog.Groups.Where(g => g.Path.Length == 0))
        {
            foreach (SettingsControl control in group.Controls.Where(c => c.Type == SettingsControlType.Button && c.Flags.HasFlag(SettingsControlFlags.Cancel)))
            {
                FrameworkElement cancel = Build(control);
                cancel.MinWidth = 92;
                buttons.Children.Add(cancel);
            }
        }

        // Save is PuTTY's own Save button of the Session page ("Saved Sessions" names the session); this is a second way to it.
        SettingsControl? saveControl = dialog.Controls.FirstOrDefault(c => c.Type == SettingsControlType.Button && c.Label == "Save" && dialog.Groups[c.Group].Path == "Session");
        if (saveControl is not null)
        {
            var save = new Button { Content = "Save", MinWidth = 92 };
            AutomationProperties.SetAutomationId(save, "SaveSession");
            AutomationProperties.SetName(save, "Save session");
            save.Click += (_, _) => dialog.Act(saveControl.Id);
            buttons.Children.Add(save);
        }

        foreach (SettingsGroup group in dialog.Groups.Where(g => g.Path.Length == 0))
        {
            foreach (SettingsControl control in group.Controls.Where(c => c.Type == SettingsControlType.Button && c.Flags.HasFlag(SettingsControlFlags.Default)))
            {
                FrameworkElement open = Build(control);
                if (open is Button button)
                {
                    button.Content = "Open session";                      // PuTTY says Open
                    AutomationProperties.SetName(button, "Open session");
                    button.MinWidth = 124;
                    // The framework's own primary-button style, which follows the theme and the accent colour. (Not the
                    // accent colour read from the resources: unboxing that boxed WinRT struct throws InvalidCastException
                    // under NativeAOT. A Style is a class, and is kept known in AotTypeAnchors.)
                    if (Application.Current.Resources.TryGetValue("AccentButtonStyle", out object? accent) && accent is Style accentStyle)
                    {
                        button.Style = accentStyle;
                    }
                }

                buttons.Children.Add(open);
            }
        }

        Grid.SetColumn(buttons, 1);
        bar.Children.Add(buttons);

        var divided = new Border
        {
            Child = bar,
            BorderThickness = new Thickness(0, 1, 0, 0),
        };
        if (ThemeBrush("DividerStrokeColorDefaultBrush") is { } divider)
        {
            divided.BorderBrush = divider;
        }

        if (ThemeBrush("LayerFillColorDefaultBrush") is { } layer)
        {
            divided.Background = layer;
        }

        return divided;
    }

    // ----- The page tree and navigation -----------------------------------------------------------

    private void FindPages()
    {
        foreach (SettingsGroup group in dialog.Groups)
        {
            if (group.Path.Length > 0 && !pagePaths.Contains(group.Path))
            {
                pagePaths.Add(group.Path);
            }
        }
    }

    /// <summary>One node per path element: "Connection/SSH/Auth" is Connection, then SSH, then Auth.</summary>
    private void BuildTree()
    {
        foreach (string path in pagePaths)
        {
            string[] parts = path.Split('/');
            string prefix = string.Empty;
            TreeViewNode? parent = null;
            foreach (string part in parts)
            {
                prefix = prefix.Length == 0 ? part : prefix + "/" + part;
                if (!nodeOfPath.TryGetValue(prefix, out TreeViewNode? node))
                {
                    node = new TreeViewNode { Content = part };
                    nodeOfPath[prefix] = node;
                    pathOfNode[node] = prefix;
                    if (parent is null)
                    {
                        tree.RootNodes.Add(node);
                    }
                    else
                    {
                        parent.Children.Add(node);
                    }
                }

                parent = node;
            }
        }
    }

    private void OnTreeSelectionChanged()
    {
        if (navigating || tree.SelectedNode is not { } node || !pathOfNode.TryGetValue(node, out string? path))
        {
            return;
        }

        Navigate(path);
    }

    /// <summary>Shows a page: selects it in the tree (opening its parents), and fills in the header and the cards.</summary>
    private void Navigate(string path)
    {
        // A node with no page of its own (none in PuTTY today) shows the first page under it.
        if (!pagePaths.Contains(path))
        {
            path = pagePaths.FirstOrDefault(p => p.StartsWith(path + "/", StringComparison.Ordinal)) ?? (pagePaths.Count > 0 ? pagePaths[0] : string.Empty);
        }

        if (path.Length == 0 || path == currentPath)
        {
            return;
        }

        navigating = true;
        try
        {
            currentPath = path;
            if (nodeOfPath.TryGetValue(path, out TreeViewNode? node))
            {
                for (string ancestor = path; ancestor.Contains('/');)
                {
                    ancestor = ancestor[..ancestor.LastIndexOf('/')];
                    if (nodeOfPath.TryGetValue(ancestor, out TreeViewNode? parent))
                    {
                        parent.IsExpanded = true;
                    }
                }

                node.IsExpanded = node.Children.Count > 0;       // the selected branch is open, as in the sketch
                tree.SelectedNode = node;
            }

            string[] parts = path.Split('/');
            var crumbs = new List<string> { RootTitle };       // (a List, not a collection expression: CsWinRT1032)
            crumbs.AddRange(parts);
            breadcrumb.ItemsSource = crumbs;
            pageTitle.Text = parts[^1];
            pageSubtitle.Text = Presentation.PageSubtitle(path)
                ?? dialog.Groups.FirstOrDefault(g => g.Path == path && g.IsPageHeading)?.Title ?? string.Empty;
            pageSubtitle.Visibility = pageSubtitle.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

            if (!builtPages.TryGetValue(path, out FrameworkElement? cards))
            {
                cards = BuildPage(path);
                builtPages[path] = cards;
            }

            pageContent.Children.Clear();
            pageContent.Children.Add(cards);
            pageHost.ChangeView(0, 0, null, disableAnimation: true);
            try
            {
                dialog.SetPage(path);
            }
            catch (ObjectDisposedException)
            {
                // the dialog was closed while the tree was still changing
            }
        }
        finally
        {
            navigating = false;
        }
    }

    private void OnBreadcrumbClicked(int index)
    {
        // Item 0 is "Configuration" (the first page); item n is the path of the first n elements.
        string[] parts = (currentPath ?? string.Empty).Split('/');
        if (index <= 0)
        {
            Navigate(pagePaths.Count > 0 ? pagePaths[0] : string.Empty);
            return;
        }

        Navigate(string.Join('/', parts.Take(index)));
    }

    // ----- Find a setting ---------------------------------------------------------------------------

    /// <summary>Every labelled control on a page, so "Find a setting" can take the user to it.</summary>
    private void BuildSearchIndex()
    {
        foreach (SettingsControl control in dialog.Controls)
        {
            string path = dialog.Groups[control.Group].Path;
            if (path.Length == 0 || control.Label is not { Length: > 0 } label ||
                control.Type is SettingsControlType.Text or SettingsControlType.Columns)
            {
                continue;
            }

            string display = $"{Plain(label).TrimEnd(':')}  -  {path.Replace("/", " / ")}";
            if (searchByDisplay.ContainsKey(display))
            {
                continue;
            }

            var entry = new SearchEntry(display, path, control.Id);
            searchIndex.Add(entry);
            searchByDisplay[display] = entry;
        }
    }

    private void OnSearchTextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        if (args.Reason != AutoSuggestionBoxTextChangeReason.UserInput)
        {
            return;
        }

        string text = sender.Text.Trim();
        var matches = new List<string>();
        if (text.Length > 0)
        {
            matches.AddRange(searchIndex.Where(e => e.Display.Contains(text, StringComparison.CurrentCultureIgnoreCase)).Take(12).Select(e => e.Display));
        }

        sender.ItemsSource = matches;
    }

    private void OnSearchSubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args)
    {
        string text = args.QueryText.Trim();
        if (text.Length == 0)
        {
            return;
        }

        SearchEntry? entry = searchByDisplay.GetValueOrDefault(text)
            ?? searchIndex.FirstOrDefault(e => e.Display.Contains(text, StringComparison.CurrentCultureIgnoreCase));
        if (entry is null)
        {
            return;
        }

        currentPath = null;                   // force the page to be shown even if it is the one that is open
        Navigate(entry.Path);
        if (widgets.TryGetValue(entry.ControlId, out Widget? widget))
        {
            pageHost.UpdateLayout();
            widget.Root?.StartBringIntoView();
            widget.Focus?.Invoke();
        }
    }

    // ----- A page: its groups as cards -----------------------------------------------------------------

    private FrameworkElement BuildPage(string path)
    {
        var cards = new StackPanel { Spacing = 12 };
        foreach (SettingsGroup group in dialog.Groups.Where(g => g.Path == path && !g.IsPageHeading))
        {
            // The sketch's cards where it has them (Presentation.cs), else one card with PuTTY's own title.
            Presentation.Card[] rules = Presentation.CardsFor(path, group.Title) ?? [new Presentation.Card(null, group.Title)];
            foreach ((Presentation.Card rule, List<SettingsControl> controls) in Partition(group, rules))
            {
                cards.Children.Add(BuildCard(rule, group, controls));
            }
        }

        return cards;
    }

    /// <summary>Splits a group's controls among its cards: each card runs from the control named by its <c>FromLabel</c> to the next card's.</summary>
    private static List<(Presentation.Card Rule, List<SettingsControl> Controls)> Partition(SettingsGroup group, Presentation.Card[] rules)
    {
        var parts = new List<(Presentation.Card Rule, List<SettingsControl> Controls)>();
        foreach (Presentation.Card rule in rules)
        {
            // A card whose first control is not in this group (PuTTY changed) is left out: its controls stay in the card before.
            if (rule.FromLabel is null || group.Controls.Any(c => Plain(c.Label) == rule.FromLabel))
            {
                parts.Add((rule, new List<SettingsControl>()));
            }
        }

        int current = 0;
        foreach (SettingsControl control in group.Controls)
        {
            for (int next = current + 1; next < parts.Count; next++)
            {
                if (Plain(control.Label) == parts[next].Rule.FromLabel)
                {
                    current = next;
                }
            }

            parts[current].Controls.Add(control);
        }

        return parts;
    }

    private FrameworkElement BuildCard(Presentation.Card rule, SettingsGroup group, List<SettingsControl> controls)
    {
        FrameworkElement body = BuildGroup(controls, rule.HiddenLabels);
        FrameworkElement inside;
        if (rule.Side)
        {
            // One setting: what it is on the left, the control on the right.
            var row = new Grid { ColumnSpacing = 16 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.2, GridUnitType.Star), MinWidth = 220 });
            row.Children.Add(CardHeading(rule));
            Grid.SetColumn(body, 1);
            body.VerticalAlignment = VerticalAlignment.Center;
            row.Children.Add(body);
            inside = row;
        }
        else
        {
            var stack = new StackPanel { Spacing = 12 };
            if (rule.Title.Length > 0)
            {
                stack.Children.Add(CardHeading(rule));
            }

            stack.Children.Add(body);
            inside = stack;
        }

        var card = new Border
        {
            Child = inside,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(16),
        };
        if (ThemeBrush("CardBackgroundFillColorDefaultBrush") is { } fill)
        {
            card.Background = fill;
        }

        if (ThemeBrush("CardStrokeColorDefaultBrush") is { } stroke)
        {
            card.BorderBrush = stroke;
        }

        return card;
    }

    /// <summary>A card's title, with the line of explanation under it if the sketch has one.</summary>
    private static FrameworkElement CardHeading(Presentation.Card rule)
    {
        var heading = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        heading.Children.Add(new TextBlock { Text = rule.Title, FontSize = 14, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
        if (rule.Subtitle is { Length: > 0 } subtitle)
        {
            heading.Children.Add(Secondary(subtitle));
        }

        return heading;
    }

    /// <summary>
    /// PuTTY's layout: a group is a vertical flow, split into columns by the Columns controls; each column is
    /// its own vertical stack, so a tall list in one column does not push down the buttons beside it. The
    /// only control that spans columns in all of PuTTY is laid out as a row of its own.
    /// </summary>
    private StackPanel BuildGroup(IReadOnlyList<SettingsControl> controls, string[]? hiddenLabels)
    {
        this.hiddenLabels = hiddenLabels;      // read by Labelled while this group is being built
        try
        {
            return BuildFlow(controls);
        }
        finally
        {
            this.hiddenLabels = null;
        }
    }

    private StackPanel BuildFlow(IReadOnlyList<SettingsControl> controls)
    {
        var flow = new StackPanel { Spacing = 10 };
        StackPanel[]? columns = null;          // the stacks of the multi-column block being filled, or null for one column

        foreach (SettingsControl control in controls)
        {
            if (control.Type == SettingsControlType.Columns)
            {
                if (control.Columns > 1)
                {
                    var grid = new Grid { ColumnSpacing = 12 };
                    columns = new StackPanel[control.Columns];
                    for (int i = 0; i < columns.Length; i++)
                    {
                        int percent = i < control.Percentages.Count ? control.Percentages[i] : 100 / control.Columns;
                        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(1, percent), GridUnitType.Star) });
                        columns[i] = new StackPanel { Spacing = 10 };
                        Grid.SetColumn(columns[i], i);
                        grid.Children.Add(columns[i]);
                    }

                    flow.Children.Add(grid);
                }
                else
                {
                    columns = null;
                }

                continue;
            }

            FrameworkElement element = Build(control);
            if (columns is not null && control.ColumnSpan == 1 && control.ColumnStart < columns.Length)
            {
                columns[control.ColumnStart].Children.Add(element);
            }
            else
            {
                columns = null;                // a spanning control ends the block; what follows starts a new one
                flow.Children.Add(element);
            }
        }

        return flow;
    }

    // ----- Controls ---------------------------------------------------------------------------------

    private FrameworkElement Build(SettingsControl control)
    {
        var widget = new Widget(control);
        FrameworkElement element = control.Type switch
        {
            SettingsControlType.Text => BuildText(widget),
            SettingsControlType.EditBox => BuildEdit(widget),
            SettingsControlType.Radio => BuildRadio(widget),
            SettingsControlType.CheckBox => BuildCheck(widget),
            SettingsControlType.Button => BuildButton(widget),
            SettingsControlType.ListBox => BuildList(widget),
            SettingsControlType.FileSelect => BuildFile(widget),
            SettingsControlType.FontSelect => BuildFont(widget),
            _ => new Grid(),
        };
        widget.Root = element;
        widgets[control.Id] = widget;
        widget.Refresh();
        return element;
    }

    private void OnControlChanged(int id)
    {
        if (widgets.TryGetValue(id, out Widget? widget))
        {
            widget.Refresh();
        }
    }

    /// <summary>Runs a change from PuTTY (not from the user) without the controls reporting it back as a user's edit.</summary>
    private void Quietly(Action change)
    {
        bool before = updating;
        updating = true;
        try
        {
            change();
        }
        finally
        {
            updating = before;
        }
    }

    private static string Plain(string? text) => (text ?? string.Empty).Replace("&&", "\u0001").Replace("&", string.Empty).Replace('\u0001', '&');

    private static void Access(Control control, char shortcut)
    {
        if (shortcut != '\0')
        {
            control.AccessKey = shortcut.ToString();
        }
    }

    private void Tag(FrameworkElement element, SettingsControl control, Widget widget)
    {
        AutomationProperties.SetAutomationId(element, $"Setting{control.Id}");
        if (control.Label is { Length: > 0 } label)
        {
            AutomationProperties.SetName(element, Plain(label));
        }

        element.GotFocus += (_, _) => { try { dialog.Focused(control.Id); } catch (ObjectDisposedException) { } };
        widget.Focus = () => element is Control c && c.Focus(FocusState.Programmatic);
    }

    private FrameworkElement Labelled(SettingsControl control, Widget widget, FrameworkElement input, bool inline)
    {
        if (control.Label is not { Length: > 0 } || control.Type is SettingsControlType.CheckBox or SettingsControlType.Button)
        {
            return input;
        }

        if (hiddenLabels is not null && Array.IndexOf(hiddenLabels, Plain(control.Label)) >= 0)
        {
            return input;       // the card's title already says it (the control keeps its label as its UI Automation name)
        }

        var label = new TextBlock { Text = Presentation.Caption(Plain(control.Label)), TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
        widget.SetLabel = text => label.Text = Presentation.Caption(Plain(text));
        if (inline)
        {
            var row = new Grid { ColumnSpacing = 10 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.Children.Add(label);
            Grid.SetColumn(input, 1);
            input.MinWidth = Math.Max(input.MinWidth, 220);
            row.Children.Add(input);
            return row;
        }

        var stack = new StackPanel { Spacing = 4 };
        stack.Children.Add(label);
        stack.Children.Add(input);
        return stack;
    }

    private FrameworkElement BuildText(Widget widget)
    {
        SettingsControl control = widget.Control;
        var text = new TextBlock
        {
            TextWrapping = control.Flags.HasFlag(SettingsControlFlags.Wrap) ? TextWrapping.Wrap : TextWrapping.NoWrap,
            IsTextSelectionEnabled = true,
        };
        widget.Refresh = () =>
        {
            string state = dialog.GetText(control.Id);
            text.Text = state.Length > 0 ? state : Presentation.Caption(Plain(control.Label));
        };
        widget.SetLabel = value => text.Text = Presentation.Caption(value);
        return text;
    }

    private FrameworkElement BuildEdit(Widget widget)
    {
        SettingsControl control = widget.Control;
        bool inline = control.PercentWidth < 100;

        if (control.Flags.HasFlag(SettingsControlFlags.HasList))
        {
            var combo = new ComboBox { IsEditable = true, HorizontalAlignment = HorizontalAlignment.Stretch };
            Access(combo, control.Shortcut);
            Tag(combo, control, widget);
            combo.TextSubmitted += (_, e) => { if (!updating) { dialog.SetText(control.Id, e.Text); } };
            combo.SelectionChanged += (_, _) => { if (!updating && combo.SelectedItem is string chosen) { dialog.SetText(control.Id, chosen); } };
            widget.Refresh = () => Quietly(() =>
            {
                combo.Items.Clear();
                foreach (SettingsListEntry entry in dialog.GetList(control.Id))
                {
                    combo.Items.Add(entry.Text);
                }

                // An editable ComboBox shows an item reliably when that item is selected; Text alone is not always
                // applied before its template exists. A text that is not in the list goes in as typed text.
                string shown = Presentation.ShownText(Plain(control.Label), dialog.GetText(control.Id));
                int at = -1;
                for (int i = 0; i < combo.Items.Count && at < 0; i++)
                {
                    if (combo.Items[i] is string item && item == shown)
                    {
                        at = i;
                    }
                }

                if (at >= 0)
                {
                    combo.SelectedIndex = at;
                }
                else
                {
                    combo.Text = shown;
                }
            });
            return Labelled(control, widget, combo, inline);
        }

        if (control.Flags.HasFlag(SettingsControlFlags.Password))
        {
            var password = new PasswordBox { HorizontalAlignment = HorizontalAlignment.Stretch };
            Access(password, control.Shortcut);
            Tag(password, control, widget);
            password.PasswordChanged += (_, _) => { if (!updating) { dialog.SetText(control.Id, password.Password); } };
            widget.Refresh = () => Quietly(() => password.Password = dialog.GetText(control.Id));
            return Labelled(control, widget, password, inline);
        }

        var box = new TextBox { HorizontalAlignment = HorizontalAlignment.Stretch };
        Access(box, control.Shortcut);
        Tag(box, control, widget);
        box.TextChanged += (_, _) => { if (!updating) { dialog.SetText(control.Id, box.Text); } };
        widget.Refresh = () => Quietly(() =>
        {
            string text = dialog.GetText(control.Id);
            if (box.Text != text)
            {
                box.Text = text;
            }
        });
        return Labelled(control, widget, box, inline);
    }

    /// <summary>PuTTY's three-way setting (Auto, Force on, Force off): the sketch draws it as a segmented control.</summary>
    private static bool IsTriState(SettingsControl control) =>
        control.Buttons.Count == 3 && Plain(control.Buttons[0]) == "Auto" && Plain(control.Buttons[1]).StartsWith("Force on", StringComparison.Ordinal);

    private FrameworkElement BuildRadio(Widget widget)
    {
        SettingsControl control = widget.Control;
        if (IsTriState(control))
        {
            var bar = new SelectorBar();
            foreach (string label in control.Buttons)
            {
                bar.Items.Add(new SelectorBarItem { Text = Plain(label) });
            }

            Tag(bar, control, widget);
            bar.SelectionChanged += (sender, _) =>
            {
                int index = sender.Items.IndexOf(sender.SelectedItem);
                if (!updating && index >= 0)
                {
                    dialog.SetRadio(control.Id, index);
                }
            };
            widget.Refresh = () => Quietly(() =>
            {
                int selected = dialog.GetRadio(control.Id);
                if (selected >= 0 && selected < bar.Items.Count)
                {
                    bar.SelectedItem = bar.Items[selected];
                }
            });
            return Labelled(control, widget, bar, inline: true);
        }

        var radios = new RadioButtons { MaxColumns = Math.Max(1, control.Columns) };
        foreach (string label in control.Buttons)
        {
            radios.Items.Add(Presentation.Caption(Plain(label)));
        }

        Tag(radios, control, widget);
        radios.SelectionChanged += (_, _) =>
        {
            if (!updating && radios.SelectedIndex >= 0)
            {
                dialog.SetRadio(control.Id, radios.SelectedIndex);
            }
        };
        widget.Refresh = () => Quietly(() => radios.SelectedIndex = dialog.GetRadio(control.Id));
        return Labelled(control, widget, radios, inline: false);
    }

    /// <summary>A check box is a switch with its label on the left and On or Off beside it, as in the sketch.</summary>
    private FrameworkElement BuildCheck(Widget widget)
    {
        SettingsControl control = widget.Control;
        var label = new TextBlock { Text = Plain(control.Label), TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
        var toggle = new ToggleSwitch { OnContent = "On", OffContent = "Off", MinWidth = 0, HorizontalAlignment = HorizontalAlignment.Right };
        Access(toggle, control.Shortcut);
        Tag(toggle, control, widget);
        AutomationProperties.SetName(toggle, Plain(control.Label));
        toggle.Toggled += (_, _) => { if (!updating) { dialog.SetChecked(control.Id, toggle.IsOn); } };
        widget.Refresh = () => Quietly(() => toggle.IsOn = dialog.GetChecked(control.Id));
        widget.SetLabel = text => { label.Text = Plain(text); AutomationProperties.SetName(toggle, Plain(text)); };

        var row = new Grid { ColumnSpacing = 12 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.Children.Add(label);
        Grid.SetColumn(toggle, 1);
        row.Children.Add(toggle);
        return row;
    }

    private FrameworkElement BuildButton(Widget widget)
    {
        SettingsControl control = widget.Control;
        var button = new Button { Content = Plain(control.Label), HorizontalAlignment = HorizontalAlignment.Stretch };
        Access(button, control.Shortcut);
        Tag(button, control, widget);
        button.Click += (_, _) => dialog.Act(control.Id);
        widget.SetLabel = text => button.Content = Plain(text);
        return button;
    }

    private FrameworkElement BuildList(Widget widget)
    {
        SettingsControl control = widget.Control;
        if (control.IsDropDown)
        {
            var drop = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch };
            Access(drop, control.Shortcut);
            Tag(drop, control, widget);
            drop.SelectionChanged += (_, _) =>
            {
                if (!updating && drop.SelectedIndex >= 0)
                {
                    dialog.Select(control.Id, drop.SelectedIndex);
                }
            };
            widget.Refresh = () => Quietly(() =>
            {
                IReadOnlyList<SettingsListEntry> entries = dialog.GetList(control.Id);
                SyncItems(drop.Items, entries);
                drop.SelectedIndex = entries.ToList().FindIndex(e => e.Selected);
            });
            return Labelled(control, widget, drop, control.PercentWidth < 100);
        }

        var list = new ListView
        {
            SelectionMode = control.MultiSelect == 0 ? ListViewSelectionMode.Single : control.MultiSelect == 1 ? ListViewSelectionMode.Multiple : ListViewSelectionMode.Extended,
            Height = Math.Max(3, control.ListHeight) * 34 + 4,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            IsDoubleTapEnabled = true,
        };
        if (ThemeBrush("CardStrokeColorDefaultBrush") is { } stroke)
        {
            list.BorderBrush = stroke;
        }

        Tag(list, control, widget);

        // The saved-sessions list shows each session's host and protocol beside its name, as in the sketch.
        SettingsGroup home = dialog.Groups[control.Group];
        bool sessionList = Presentation.IsSavedSessionList(home.Path, home.Title);
        var shownRows = new List<string>();

        int dragFrom = -1;
        if (control.Flags.HasFlag(SettingsControlFlags.DragList))
        {
            list.CanReorderItems = true;
            list.AllowDrop = true;
            list.CanDragItems = true;
            list.DragItemsStarting += (_, e) => dragFrom = e.Items.Count > 0 ? list.Items.IndexOf(e.Items[0]) : -1;
            list.DragItemsCompleted += (_, e) =>
            {
                int to = e.Items.Count > 0 ? list.Items.IndexOf(e.Items[0]) : -1;
                if (dragFrom >= 0 && to >= 0 && to != dragFrom)
                {
                    dialog.Move(control.Id, dragFrom, to);
                }

                dragFrom = -1;
            };
        }

        list.SelectionChanged += (_, e) =>
        {
            if (updating)
            {
                return;
            }

            foreach (object removed in e.RemovedItems)
            {
                int at = list.Items.IndexOf(removed);
                if (control.MultiSelect != 0 && at >= 0)
                {
                    dialog.Select(control.Id, at, selected: false);
                }
            }

            foreach (object added in e.AddedItems)
            {
                int at = list.Items.IndexOf(added);
                if (at >= 0)
                {
                    dialog.Select(control.Id, at, selected: true);
                }
            }
        };
        list.DoubleTapped += (_, _) => dialog.Act(control.Id);
        widget.Refresh = () => Quietly(() =>
        {
            IReadOnlyList<SettingsListEntry> entries = dialog.GetList(control.Id);
            if (sessionList)
            {
                SyncSessionRows(list.Items, entries, shownRows);
            }
            else
            {
                SyncItems(list.Items, entries);
            }

            if (control.MultiSelect == 0)
            {
                // SelectedItems.Add fails (E_UNEXPECTED) on a single-selection list that is not on screen yet.
                int selected = entries.ToList().FindIndex(e => e.Selected);
                if (list.SelectedIndex != selected)
                {
                    list.SelectedIndex = selected;
                }

                return;
            }

            for (int i = 0; i < entries.Count; i++)
            {
                bool isSelected = list.SelectedItems.Contains(list.Items[i]);
                if (entries[i].Selected && !isSelected)
                {
                    list.SelectedItems.Add(list.Items[i]);
                }
                else if (!entries[i].Selected && isSelected)
                {
                    list.SelectedItems.Remove(list.Items[i]);
                }
            }
        });
        return Labelled(control, widget, list, inline: false);
    }

    /// <summary>Makes a list's items the entries' text, touching the list only if it differs (PuTTY refreshes lists often).</summary>
    private static void SyncItems(IList<object> items, IReadOnlyList<SettingsListEntry> entries)
    {
        bool same = items.Count == entries.Count;
        for (int i = 0; same && i < entries.Count; i++)
        {
            same = items[i] is string text && text == Display(entries[i].Text);
        }

        if (same)
        {
            return;
        }

        items.Clear();
        foreach (SettingsListEntry entry in entries)
        {
            items.Add(Display(entry.Text));
        }
    }

    /// <summary>
    /// Makes the saved-sessions list a row per session: the name, and on the right in the secondary colour where it
    /// goes ("10.20.0.12 · SSH", read from PuTTY's storage like the list). Touches the list only if a row differs.
    /// </summary>
    private static void SyncSessionRows(IList<object> items, IReadOnlyList<SettingsListEntry> entries, List<string> shown)
    {
        var descriptions = new List<string>();
        var wanted = new List<string>();
        foreach (SettingsListEntry entry in entries)
        {
            string name = Display(entry.Text);
            string description = string.Empty;
            if (!name.Equals(PuttySessions.DefaultSettingsName, StringComparison.Ordinal))
            {
                try
                {
                    description = PuttySessions.Describe(entry.Text)?.Description ?? string.Empty;
                }
                catch (InvalidOperationException)
                {
                    // the engine is shutting down: the name alone will do
                }
            }

            descriptions.Add(description);
            wanted.Add(name + "\u0001" + description);
        }

        if (wanted.SequenceEqual(shown))
        {
            return;
        }

        items.Clear();
        for (int i = 0; i < entries.Count; i++)
        {
            string name = Display(entries[i].Text);
            var row = new Grid { ColumnSpacing = 12, MinHeight = 32 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.Children.Add(new TextBlock { Text = name, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis });
            if (descriptions[i].Length > 0)
            {
                TextBlock where = Secondary(descriptions[i]);
                where.VerticalAlignment = VerticalAlignment.Center;
                Grid.SetColumn(where, 1);
                row.Children.Add(where);
            }

            var item = new ListViewItem { Content = row };
            AutomationProperties.SetName(item, name);
            items.Add(item);
        }

        shown.Clear();
        shown.AddRange(wanted);
    }

    /// <summary>PuTTY puts tab-separated columns in some list entries (a port forwarding is "L8080&lt;tab&gt;host:80").</summary>
    private static string Display(string text) => text.Replace("\t", "    ");

    private FrameworkElement BuildFile(Widget widget)
    {
        SettingsControl control = widget.Control;
        bool justButton = control.Flags.HasFlag(SettingsControlFlags.JustButton);
        var path = new TextBox { HorizontalAlignment = HorizontalAlignment.Stretch };
        var browse = new Button { Content = "Browse..." };
        Tag(browse, control, widget);
        AutomationProperties.SetAutomationId(path, $"Setting{control.Id}Path");
        path.TextChanged += (_, _) => { if (!updating && !justButton) { dialog.SetFile(control.Id, path.Text); } };
        browse.Click += async (_, _) => await BrowseAsync(control, justButton ? null : path);
        widget.Refresh = () => Quietly(() =>
        {
            if (!justButton)
            {
                string file = dialog.GetFile(control.Id);
                if (path.Text != file)
                {
                    path.Text = file;
                }
            }
        });

        var row = new Grid { ColumnSpacing = 8 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        if (!justButton)
        {
            row.Children.Add(path);
        }

        Grid.SetColumn(browse, 1);
        row.Children.Add(browse);
        return justButton ? browse : Labelled(control, widget, row, inline: false);
    }

    private async Task BrowseAsync(SettingsControl control, TextBox? path)
    {
        try
        {
            string? chosen;
            nint hwnd = windowHandle();
            string[] extensions = control.Filter switch
            {
                SettingsFileFilter.KeyFiles => [".ppk"],
                SettingsFileFilter.DynamicLibraries => [".dll"],
                SettingsFileFilter.Sounds => [".wav"],
                _ => ["*"],
            };
            if (control.Flags.HasFlag(SettingsControlFlags.ForWriting))
            {
                var picker = new Windows.Storage.Pickers.FileSavePicker { SuggestedFileName = "putty" };
                picker.FileTypeChoices.Add("Files", [.. extensions.Where(e => e != "*")]);
                WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
                chosen = (await picker.PickSaveFileAsync())?.Path;
            }
            else
            {
                var picker = new Windows.Storage.Pickers.FileOpenPicker();
                foreach (string extension in extensions)
                {
                    picker.FileTypeFilter.Add(extension);
                }

                WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
                chosen = (await picker.PickSingleFileAsync())?.Path;
            }

            if (chosen is not null)
            {
                if (path is not null)
                {
                    path.Text = chosen;        // the text change reports it
                }
                else
                {
                    dialog.SetFile(control.Id, chosen);
                }
            }
        }
        catch (Exception ex) when (ex is not ObjectDisposedException)
        {
            await SessionInteraction.ShowMessageAsync(xamlRoot(), "Could not choose a file", ex.Message);
        }
    }

    private FrameworkElement BuildFont(Widget widget)
    {
        SettingsControl control = widget.Control;
        var description = new TextBlock { VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        var change = new Button { Content = "Change..." };
        Tag(change, control, widget);
        change.Click += async (_, _) => await ChooseFontAsync(control);
        widget.Refresh = () =>
        {
            try
            {
                SettingsFont font = dialog.GetFont(control.Id);
                description.Text = $"{font.Name}, {(font.Bold ? "bold, " : string.Empty)}{font.Height}-point";
            }
            catch (InvalidOperationException)
            {
                description.Text = string.Empty;    // no font set yet
            }
        };

        var row = new Grid { ColumnSpacing = 8 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.Children.Add(description);
        Grid.SetColumn(change, 1);
        row.Children.Add(change);
        return Labelled(control, widget, row, inline: false);
    }

    // ----- The choosers PuTTY asks the host for -------------------------------------------------------

    private async Task ChooseFontAsync(SettingsControl control)
    {
        XamlRoot? ownerRoot = xamlRoot();
        if (ownerRoot is null)
        {
            return;
        }

        SettingsFont current;
        try
        {
            current = dialog.GetFont(control.Id);
        }
        catch (InvalidOperationException)
        {
            current = new SettingsFont("Consolas", 10, false, 0);
        }

        var family = new ComboBox { IsEditable = true, HorizontalAlignment = HorizontalAlignment.Stretch, Header = "Font" };
        foreach (string name in InstalledFonts.Families())
        {
            family.Items.Add(name);
        }

        family.Text = current.Name;
        var size = new NumberBox { Header = "Size (points)", Value = current.Height, Minimum = 4, Maximum = 96, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline };
        var bold = new CheckBox { Content = "Bold", IsChecked = current.Bold };
        var panel = new StackPanel { Spacing = 12, MinWidth = 340 };
        panel.Children.Add(family);
        panel.Children.Add(size);
        panel.Children.Add(bold);

        var chooser = new ContentDialog
        {
            XamlRoot = ownerRoot,
            Title = control.Label is { Length: > 0 } label ? Plain(label) : "Font",
            Content = panel,
            PrimaryButtonText = "OK",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
        };
        if (await chooser.ShowAsync() == ContentDialogResult.Primary && family.Text.Length > 0)
        {
            dialog.SetFont(control.Id, new SettingsFont(family.Text, (int)Math.Round(double.IsNaN(size.Value) ? current.Height : size.Value), bold.IsChecked == true, current.Charset));
        }
    }

    private async Task ChooseColourAsync(int id, int r, int g, int b)
    {
        XamlRoot? ownerRoot = xamlRoot();
        if (ownerRoot is null)
        {
            dialog.ColourChosen(id, null);
            return;
        }

        var picker = new ColorPicker { Color = Color.FromArgb(255, (byte)r, (byte)g, (byte)b), IsAlphaEnabled = false, IsHexInputVisible = true };
        var chooser = new ContentDialog
        {
            XamlRoot = ownerRoot,
            Title = "Choose a colour",
            Content = picker,
            PrimaryButtonText = "OK",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
        };
        ContentDialogResult result = await chooser.ShowAsync();
        dialog.ColourChosen(id, result == ContentDialogResult.Primary ? (picker.Color.R, picker.Color.G, picker.Color.B) : null);
    }

    [System.Runtime.InteropServices.LibraryImport("user32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static partial bool MessageBeep(uint type);
}
