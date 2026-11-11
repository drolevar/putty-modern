using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using PuTTY.Interop;
using Windows.UI;

namespace PuTTY.App.Settings;

/// <summary>
/// Draws PuTTY's configuration box from the description the engine gives (<see cref="SettingsDialog"/>): a
/// list of pages on the left, the groups of the selected page on the right, Open and Cancel at the bottom.
/// Nothing here knows what a setting means: PuTTY's own handlers keep the controls consistent (choosing a
/// protocol changes the port), and this class only shows the state it reads and reports what the user does.
/// </summary>
/// <remarks>
/// Built from code, with no XAML and no subclass of a control, and every control constructed here, which is
/// what NativeAOT needs (CLAUDE.md). Pages are built the first time they are shown and then kept; a change
/// to a control on a page that was never shown costs nothing, because building the page reads the state.
/// </remarks>
internal sealed partial class SettingsView
{
    private const double PageIndent = 16;

    private readonly SettingsDialog dialog;
    private readonly Func<XamlRoot?> xamlRoot;
    private readonly Func<nint> windowHandle;
    private readonly Dictionary<int, Widget> widgets = [];
    private readonly Dictionary<string, FrameworkElement> builtPages = [];
    private readonly List<string> pagePaths = [];
    private readonly ListView pageList = new();
    private readonly ScrollViewer pageHost = new();
    private readonly Grid root = new();
    private bool updating;

    /// <summary>What the host needs to keep per control to bring it up to date.</summary>
    private sealed class Widget(SettingsControl control)
    {
        public SettingsControl Control { get; } = control;

        public Action Refresh { get; set; } = () => { };

        public Action<string>? SetLabel { get; set; }

        public Func<bool>? Focus { get; set; }
    }

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

        BuildPageList();
        Layout();
    }

    /// <summary>The whole dialog.</summary>
    public FrameworkElement Root => root;

    /// <summary>The dialog is over (Open or Cancel, or a double-clicked saved session): true to go ahead.</summary>
    public event Action<bool>? Ended;

    // ----- Frame ----------------------------------------------------------------------------------

    private void Layout()
    {
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(230) });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        Grid.SetRow(pageList, 0);
        pageList.Margin = new Thickness(8, 8, 0, 8);
        root.Children.Add(pageList);

        pageHost.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
        pageHost.Padding = new Thickness(20, 12, 24, 12);
        Grid.SetColumn(pageHost, 1);
        root.Children.Add(pageHost);

        // Open and Cancel are PuTTY's controls in a group on no page: shown at the bottom, right-aligned.
        var footer = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(12),
        };
        foreach (SettingsGroup group in dialog.Groups.Where(g => g.Path.Length == 0))
        {
            foreach (SettingsControl control in group.Controls.Where(c => c.Type == SettingsControlType.Button).OrderBy(c => c.Flags.HasFlag(SettingsControlFlags.Cancel) ? 1 : 0))
            {
                FrameworkElement button = Build(control);
                if (control.Flags.HasFlag(SettingsControlFlags.Default) && button is Button primary)
                {
                    // The framework's own style for a primary button: it follows the theme and the accent colour.
                    // (Not the accent colour read from the resources: unboxing that boxed WinRT struct throws
                    // InvalidCastException under NativeAOT. A Style is a class, and is kept known in AotTypeAnchors.)
                    if (Application.Current.Resources.TryGetValue("AccentButtonStyle", out object? accent) && accent is Style accentStyle)
                    {
                        primary.Style = accentStyle;
                    }
                }

                button.MinWidth = 88;
                footer.Children.Add(button);
            }
        }

        Grid.SetRow(footer, 1);
        Grid.SetColumnSpan(footer, 2);
        root.Children.Add(footer);
    }

    private void BuildPageList()
    {
        foreach (SettingsGroup group in dialog.Groups)
        {
            if (group.Path.Length > 0 && !pagePaths.Contains(group.Path))
            {
                pagePaths.Add(group.Path);
            }
        }

        foreach (string path in pagePaths)
        {
            int depth = path.Count(c => c == '/');
            pageList.Items.Add(new TextBlock
            {
                Text = path[(path.LastIndexOf('/') + 1)..],
                Margin = new Thickness(depth * PageIndent, 0, 0, 0),
                FontWeight = depth == 0 ? FontWeights.SemiBold : FontWeights.Normal,
            });
        }

        AutomationProperties.SetAutomationId(pageList, "SettingsPages");
        pageList.SelectionChanged += (_, _) => ShowPage(pageList.SelectedIndex);
        pageList.SelectedIndex = 0;
    }

    private void ShowPage(int index)
    {
        if (index < 0 || index >= pagePaths.Count)
        {
            return;
        }

        string path = pagePaths[index];
        if (!builtPages.TryGetValue(path, out FrameworkElement? page))
        {
            page = BuildPage(path);
            builtPages[path] = page;
        }

        pageHost.Content = page;
        try
        {
            dialog.SetPage(path);
        }
        catch (ObjectDisposedException)
        {
            // the dialog was closed while the list was still changing
        }
    }

    // ----- A page: its groups, laid out the way PuTTY lays out columns ------------------------------

    private FrameworkElement BuildPage(string path)
    {
        var page = new StackPanel { Spacing = 14 };
        foreach (SettingsGroup group in dialog.Groups.Where(g => g.Path == path))
        {
            if (group.IsPageHeading)
            {
                page.Children.Add(new TextBlock { Text = group.Title, FontSize = 20, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
                continue;
            }

            var body = BuildGroup(group);
            var box = new StackPanel { Spacing = 6 };
            if (group.Title.Length > 0)
            {
                box.Children.Add(new TextBlock { Text = group.Title, FontWeight = FontWeights.SemiBold });
            }

            box.Children.Add(new Border
            {
                Child = body,
                BorderBrush = new SolidColorBrush(Color.FromArgb(0x30, 0x80, 0x80, 0x80)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(12),
            });
            page.Children.Add(box);
        }

        return page;
    }

    /// <summary>
    /// PuTTY's layout: a group is a vertical flow, split into columns by the Columns controls; each column is
    /// its own vertical stack, so a tall list in one column does not push down the buttons beside it. The
    /// only control that spans columns in all of PuTTY is laid out as a row of its own.
    /// </summary>
    private StackPanel BuildGroup(SettingsGroup group)
    {
        var flow = new StackPanel { Spacing = 8 };
        StackPanel[]? columns = null;          // the stacks of the multi-column block being filled, or null for one column

        foreach (SettingsControl control in group.Controls)
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
                        columns[i] = new StackPanel { Spacing = 8 };
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

        var label = new TextBlock { Text = Plain(control.Label), TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
        widget.SetLabel = text => label.Text = Plain(text);
        if (inline)
        {
            var row = new Grid { ColumnSpacing = 10 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.Children.Add(label);
            Grid.SetColumn(input, 1);
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
            text.Text = state.Length > 0 ? state : Plain(control.Label);
        };
        widget.SetLabel = value => text.Text = value;
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

                combo.Text = dialog.GetText(control.Id);
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

    private FrameworkElement BuildRadio(Widget widget)
    {
        SettingsControl control = widget.Control;
        var radios = new RadioButtons { MaxColumns = Math.Max(1, control.Columns) };
        foreach (string label in control.Buttons)
        {
            radios.Items.Add(Plain(label));
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

    private FrameworkElement BuildCheck(Widget widget)
    {
        SettingsControl control = widget.Control;
        var check = new CheckBox { Content = Plain(control.Label) };
        Access(check, control.Shortcut);
        Tag(check, control, widget);
        check.Checked += (_, _) => { if (!updating) { dialog.SetChecked(control.Id, true); } };
        check.Unchecked += (_, _) => { if (!updating) { dialog.SetChecked(control.Id, false); } };
        widget.Refresh = () => Quietly(() => check.IsChecked = dialog.GetChecked(control.Id));
        widget.SetLabel = text => check.Content = Plain(text);
        return check;
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
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x40, 0x80, 0x80, 0x80)),
            BorderThickness = new Thickness(1),
            IsDoubleTapEnabled = true,
        };
        Tag(list, control, widget);
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
            SyncItems(list.Items, entries);
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
