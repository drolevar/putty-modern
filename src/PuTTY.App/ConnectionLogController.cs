using System.Collections.ObjectModel;
using System.Globalization;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;
using PuTTY.Interop;
using PuTTY.Sessions;

namespace PuTTY.App;

/// <summary>
/// The Connection log panel (the sketch's frame <c>9:2</c>): what PuTTY says about each connection as it happens
/// ("Connecting to ...", "Host key verified", "Authenticated using public key"), one row per line with the time and the
/// session's name, a filter ("All sessions" or one of them) and Clear. The lines are PuTTY's own event log and error
/// messages, passed in by the main window as the sessions raise them.
/// </summary>
/// <remarks>
/// <para>
/// The lines live in <see cref="ConnectionLogModel"/> (the last 5000, a ring buffer), not in controls. The list is data-driven
/// and virtualized: its items are the model's <see cref="LogEntry"/> objects and the ListView makes a row only for what is on
/// screen and fills it in <c>ContainerContentChanging</c>, from a row template that is a plain Grid of three text blocks (no
/// bindings, so nothing is looked up by reflection under NativeAOT). Lines are applied in batches on a timer, so a flood of
/// lines costs one update per tick; a batch bigger than a few dozen lines replaces the list's source instead of changing it item
/// by item.
/// </para>
/// <para>
/// The Session column is as wide as the longest session name met so far (between 90 and 260 px) until the user drags the
/// divider in the header; every row, made or recycled, takes the width from one value. Built from code on a plain
/// <see cref="UserControl"/>, like the other panels (NativeAOT rules in CLAUDE.md).
/// </para>
/// </remarks>
internal sealed class ConnectionLogController
{
    /// <summary>The AutomationIds, for UI Automation checks.</summary>
    public const string ListId = "ConnectionLogList";
    public const string FilterId = "ConnectionLogFilter";
    public const string ClearId = "ConnectionLogClear";
    public const string GripId = "ConnectionLogColumnGrip";

    private const string AllSessions = "All sessions";
    private const int Capacity = 5000;      // the oldest lines go first
    private const double MinWidth = 70;
    private const double MaxWidth = 400;
    private const double FitMinWidth = 90;
    private const double FitMaxWidth = 260;
    private const string RowMark = "connection-log-row";
    private static readonly TimeSpan FlushInterval = TimeSpan.FromMilliseconds(100);

    // The row: three columns whose widths are set when a row is filled in (the middle one is the Session column).
    private const string RowTemplate = """
        <DataTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation">
          <Grid ColumnSpacing="14">
            <Grid.ColumnDefinitions>
              <ColumnDefinition Width="64" />
              <ColumnDefinition Width="110" />
              <ColumnDefinition Width="*" />
            </Grid.ColumnDefinitions>
            <TextBlock Grid.Column="0" FontFamily="Cascadia Mono" FontSize="12" VerticalAlignment="Center"
                       Foreground="{ThemeResource TextFillColorSecondaryBrush}" />
            <TextBlock Grid.Column="1" TextTrimming="CharacterEllipsis" VerticalAlignment="Center"
                       Foreground="{ThemeResource AccentTextFillColorPrimaryBrush}" />
            <TextBlock Grid.Column="2" TextTrimming="CharacterEllipsis" VerticalAlignment="Center" />
          </Grid>
        </DataTemplate>
        """;

    private readonly ConnectionLogModel model = new(Capacity);
    private readonly ListView list = new();
    private readonly ComboBox filter = new();
    private readonly Grid header = new();
    private readonly DispatcherQueueTimer timer;
    private readonly List<WeakReference<Grid>> rows = [];        // the rows the list has made, to give them a new width
    private ObservableCollection<LogEntry> items = [];
    private double sessionWidth = FitMinWidth;
    private bool userSized;
    private bool rebuilding;
    private bool templateWarned;

    public ConnectionLogController(UserControl host)
    {
        filter.Items.Add(AllSessions);
        filter.SelectedIndex = 0;
        filter.MinWidth = 150;
        filter.SelectionChanged += (_, _) =>
        {
            if (!rebuilding)
            {
                model.SetFilter(filter.SelectedItem is string chosen && chosen != AllSessions ? chosen : null);
                Flush();
            }
        };
        AutomationProperties.SetAutomationId(filter, FilterId);
        AutomationProperties.SetName(filter, "Show the log of");

        var clear = new HyperlinkButton { Content = "Clear", Padding = new Thickness(0), VerticalAlignment = VerticalAlignment.Center };
        clear.Click += (_, _) => Clear();
        AutomationProperties.SetAutomationId(clear, ClearId);

        list.SelectionMode = ListViewSelectionMode.None;
        list.ItemTemplate = (DataTemplate)XamlReader.Load(RowTemplate);
        list.ItemsSource = items;
        list.ContainerContentChanging += (_, args) => FillRow(args);
        AutomationProperties.SetAutomationId(list, ListId);
        AutomationProperties.SetName(list, "Connection log");

        var top = new Grid { Margin = new Thickness(12, 8, 12, 4) };
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        top.Children.Add(filter);
        Grid.SetColumn(clear, 1);
        top.Children.Add(clear);

        BuildHeader();

        var layout = new Grid();
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        layout.Children.Add(top);
        Grid.SetRow(header, 1);
        layout.Children.Add(header);
        Grid.SetRow(list, 2);
        layout.Children.Add(list);
        host.Content = layout;

        timer = host.DispatcherQueue.CreateTimer();
        timer.Interval = FlushInterval;
        timer.IsRepeating = true;
        timer.Tick += (_, _) =>
        {
            Flush();
            if (!model.HasPending)
            {
                timer.Stop();
            }
        };
    }

    /// <summary>One line of a session's log (<paramref name="error"/>: an error, shown in the error colour).</summary>
    public void Add(string session, string text, bool error = false)
    {
        if (model.Add(new LogEntry(DateTime.Now, session, text, error)))
        {
            Learn(session);
        }

        if (model.HasPending && !timer.IsRunning)
        {
            timer.Start();
        }
    }

    /// <summary>Forgets every line (of every session, whatever the filter shows).</summary>
    public void Clear()
    {
        model.Clear();
        Flush();
    }

    // The captions, and the divider that resizes the Session column. The columns are those of a row, so they line up.
    private void BuildHeader()
    {
        header.Margin = new Thickness(12, 0, 12, 2);
        header.ColumnSpacing = 14;
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(64) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(sessionWidth) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        string[] captions = ["Time", "Session", "Message"];
        for (int column = 0; column < captions.Length; column++)
        {
            var caption = new TextBlock { Text = captions[column], FontSize = 12 };
            Brush("TextFillColorSecondaryBrush", caption);
            Grid.SetColumn(caption, column);
            header.Children.Add(caption);
        }

        var grip = new ColumnGrip(header, () => sessionWidth, width => SetSessionWidth(width, byUser: true))
        {
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 0, -13, 0),       // centred on the gap between the Session and Message columns
        };
        AutomationProperties.SetAutomationId(grip, GripId);
        AutomationProperties.SetName(grip, "Resize the Session column");
        Grid.SetColumn(grip, 1);
        header.Children.Add(grip);
    }

    // A session the log has not met: it can now be chosen in the filter (the selection stays where it is), and the Session
    // column is made wide enough for its name unless the user has set the width.
    private void Learn(string session)
    {
        rebuilding = true;
        int selected = filter.SelectedIndex;
        filter.Items.Add(session);
        filter.SelectedIndex = selected;
        rebuilding = false;

        if (!userSized)
        {
            var probe = new TextBlock { Text = session };
            probe.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
            double wanted = Math.Clamp(probe.DesiredSize.Width + 6, FitMinWidth, FitMaxWidth);
            if (wanted > sessionWidth)
            {
                SetSessionWidth(wanted, byUser: false);
            }
        }
    }

    private void SetSessionWidth(double width, bool byUser)
    {
        sessionWidth = Math.Clamp(width, MinWidth, MaxWidth);
        userSized |= byUser;
        header.ColumnDefinitions[1].Width = new GridLength(sessionWidth);

        for (int i = rows.Count - 1; i >= 0; i--)
        {
            if (rows[i].TryGetTarget(out Grid? row))
            {
                row.ColumnDefinitions[1].Width = new GridLength(sessionWidth);
            }
            else
            {
                rows.RemoveAt(i);
            }
        }
    }

    // Applies what the model has gathered since the last time to the list.
    private void Flush()
    {
        if (!model.HasPending)
        {
            return;
        }

        // Following the tail: the newest line is on screen (its row exists), so the list keeps showing it. A list the
        // user has scrolled back stays where it is.
        bool following = items.Count == 0 || list.ContainerFromIndex(items.Count - 1) is not null;

        LogChange change = model.TakeChange();
        if (change.IsReset)
        {
            items = new ObservableCollection<LogEntry>(change.Added);
            list.ItemsSource = items;
            following = true;
        }
        else
        {
            for (int i = 0; i < change.Removed; i++)
            {
                items.RemoveAt(0);
            }

            foreach (LogEntry entry in change.Added)
            {
                items.Add(entry);
            }
        }

        if (following && items.Count > 0)
        {
            list.ScrollIntoView(items[^1]);
        }
    }

    // Fills in the row the list is showing an entry in; a row that is made once is filled in again each time it is reused.
    private void FillRow(ContainerContentChangingEventArgs args)
    {
        if (args.InRecycleQueue || args.Item is not LogEntry entry)
        {
            return;
        }

        if (args.ItemContainer.ContentTemplateRoot is not Grid row
            || row.Children.Count != 3
            || row.Children[0] is not TextBlock time
            || row.Children[1] is not TextBlock session
            || row.Children[2] is not TextBlock message)
        {
            if (!templateWarned)
            {
                templateWarned = true;        // once: the log would be empty rows, and this is the only trace of why
                PuttyLog.Warning("app", "The connection log's row template did not give the elements it was built from");
            }

            return;
        }

        if (row.Tag is not string mark || mark != RowMark)
        {
            row.Tag = RowMark;
            rows.Add(new WeakReference<Grid>(row));
        }

        row.ColumnDefinitions[1].Width = new GridLength(sessionWidth);
        string clock = entry.Time.ToString("HH:mm:ss", CultureInfo.InvariantCulture);
        time.Text = clock;
        session.Text = entry.Session;
        message.Text = entry.Text;
        if (entry.Error)
        {
            Brush("SystemFillColorCriticalBrush", message);
        }
        else
        {
            message.ClearValue(TextBlock.ForegroundProperty);        // a reused row may have had an error in it
        }

        SelectorItem container = args.ItemContainer;
        container.MinHeight = 26;
        container.Padding = new Thickness(12, 0, 12, 0);
        AutomationProperties.SetName(container, $"{clock} {entry.Session} {entry.Text}");
        ToolTipService.SetToolTip(container, $"{entry.Session}: {entry.Text}");       // the whole line, which the row may cut off
    }

    private static void Brush(string key, TextBlock text)
    {
        if (Application.Current.Resources.TryGetValue(key, out object? value) && value is Brush brush)
        {
            text.Foreground = brush;
        }
    }
}
