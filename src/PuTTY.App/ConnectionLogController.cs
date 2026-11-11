using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace PuTTY.App;

/// <summary>
/// The Connection log panel (the sketch's frame <c>9:2</c>): what PuTTY says about each connection as it happens
/// ("Connecting to ...", "Host key verified", "Authenticated using public key"), one row per line with the time and the
/// session's name, a filter ("All sessions" or one of them) and Clear. The lines are PuTTY's own event log and error
/// messages, passed in by the main window as the sessions raise them.
/// </summary>
/// <remarks>Built from code on a plain <see cref="UserControl"/>, like the other panels (NativeAOT rules in CLAUDE.md).</remarks>
internal sealed class ConnectionLogController
{
    /// <summary>The AutomationIds, for UI Automation checks.</summary>
    public const string ListId = "ConnectionLogList";
    public const string FilterId = "ConnectionLogFilter";
    public const string ClearId = "ConnectionLogClear";

    private const string AllSessions = "All sessions";
    private const int Capacity = 5000;      // the oldest lines go first

    private sealed record Entry(DateTime Time, string Session, string Text, bool Error);

    private readonly List<Entry> entries = [];
    private readonly ListView list = new();
    private readonly ComboBox filter = new();
    private bool rebuilding;

    public ConnectionLogController(UserControl host)
    {
        filter.Items.Add(AllSessions);
        filter.SelectedIndex = 0;
        filter.MinWidth = 150;
        filter.SelectionChanged += (_, _) => { if (!rebuilding) { Rebuild(); } };
        AutomationProperties.SetAutomationId(filter, FilterId);
        AutomationProperties.SetName(filter, "Show the log of");

        var clear = new HyperlinkButton { Content = "Clear", Padding = new Thickness(0), VerticalAlignment = VerticalAlignment.Center };
        clear.Click += (_, _) => Clear();
        AutomationProperties.SetAutomationId(clear, ClearId);

        list.SelectionMode = ListViewSelectionMode.None;
        AutomationProperties.SetAutomationId(list, ListId);
        AutomationProperties.SetName(list, "Connection log");

        var header = new Grid { Margin = new Thickness(12, 8, 12, 4) };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.Children.Add(filter);
        Grid.SetColumn(clear, 1);
        header.Children.Add(clear);

        var layout = new Grid();
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        layout.Children.Add(header);
        Grid.SetRow(list, 1);
        layout.Children.Add(list);
        host.Content = layout;
    }

    /// <summary>One line of a session's log (<paramref name="error"/>: an error, shown in the error colour).</summary>
    public void Add(string session, string text, bool error = false)
    {
        var entry = new Entry(DateTime.Now, session, text, error);
        entries.Add(entry);
        if (entries.Count > Capacity)
        {
            entries.RemoveRange(0, entries.Count - Capacity);
            Rebuild();
            return;
        }

        if (!filter.Items.Any(item => item is string name && name == session))
        {
            // A session the filter has not met: it can now be chosen (the selection stays where it is).
            rebuilding = true;
            int selected = filter.SelectedIndex;
            filter.Items.Add(session);
            filter.SelectedIndex = selected;
            rebuilding = false;
        }

        if (Shown(entry))
        {
            ListViewItem row = Row(entry);
            list.Items.Add(row);
            list.ScrollIntoView(row);
        }
    }

    /// <summary>Forgets every line (of every session, whatever the filter shows).</summary>
    public void Clear()
    {
        entries.Clear();
        list.Items.Clear();
    }

    private bool Shown(Entry entry) => filter.SelectedItem is not string chosen || chosen == AllSessions || chosen == entry.Session;

    private void Rebuild()
    {
        list.Items.Clear();
        ListViewItem? last = null;
        foreach (Entry entry in entries.Where(Shown))
        {
            last = Row(entry);
            list.Items.Add(last);
        }

        if (last is not null)
        {
            list.ScrollIntoView(last);
        }
    }

    private static ListViewItem Row(Entry entry)
    {
        var grid = new Grid { ColumnSpacing = 14 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(64) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(130) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var time = new TextBlock { Text = entry.Time.ToString("HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture), FontFamily = new FontFamily("Cascadia Mono"), FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
        Dim(time);
        var session = new TextBlock { Text = entry.Session, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
        Accent(session);
        var message = new TextBlock { Text = entry.Text, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
        if (entry.Error)
        {
            Brush("SystemFillColorCriticalBrush", message);
        }

        Grid.SetColumn(session, 1);
        Grid.SetColumn(message, 2);
        grid.Children.Add(time);
        grid.Children.Add(session);
        grid.Children.Add(message);

        var item = new ListViewItem { Content = grid, MinHeight = 26, Padding = new Thickness(12, 0, 12, 0) };
        AutomationProperties.SetName(item, $"{entry.Time:HH:mm:ss} {entry.Session} {entry.Text}");
        return item;
    }

    private static void Dim(TextBlock text) => Brush("TextFillColorSecondaryBrush", text);

    private static void Accent(TextBlock text) => Brush("AccentTextFillColorPrimaryBrush", text);

    private static void Brush(string key, TextBlock text)
    {
        if (Application.Current.Resources.TryGetValue(key, out object? value) && value is Brush brush)
        {
            text.Foreground = brush;
        }
    }
}
