using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using PuTTY.Interop;

namespace PuTTY.App;

/// <summary>
/// The list of sessions saved in PuTTY's own storage, in the Sessions panel: a search box, the sessions,
/// and an Open button. Opening is a double click, Enter, or the button. The list is read from the
/// registry, so what PuTTY classic saved shows up without any import, and nothing here writes to it.
/// </summary>
/// <remarks>
/// Built from code on a plain <see cref="UserControl"/>, like <see cref="TerminalViewController"/>: no
/// subclass of a control, every control constructed here (the NativeAOT rules in CLAUDE.md).
/// </remarks>
internal sealed class SessionListController
{
    /// <summary>The AutomationIds, for UI Automation checks (tools/app-sessions-smoke.ps1).</summary>
    public const string SearchId = "SessionSearch";
    public const string ListId = "SessionList";
    public const string OpenId = "OpenSessionButton";

    private readonly TextBox search = new();
    private readonly ListView list = new();
    private readonly Button open = new();
    private readonly TextBlock status = new();
    private string[] all = [];     // an array, not IReadOnlyList: CsWinRT rejects a collection expression for that (CsWinRT1032)

    public SessionListController(UserControl host)
    {
        search.PlaceholderText = "Search sessions";
        search.TextChanged += (_, _) => ApplyFilter();
        AutomationProperties.SetAutomationId(search, SearchId);
        AutomationProperties.SetName(search, "Search sessions");

        list.SelectionMode = ListViewSelectionMode.Single;
        list.IsDoubleTapEnabled = true;
        list.DoubleTapped += (_, _) => OpenSelected();
        list.KeyDown += OnListKeyDown;
        list.SelectionChanged += (_, _) => open.IsEnabled = list.SelectedItem is string;
        AutomationProperties.SetAutomationId(list, ListId);
        AutomationProperties.SetName(list, "Saved sessions");

        open.Content = "Open";
        open.IsEnabled = false;
        open.Click += (_, _) => OpenSelected();
        AutomationProperties.SetAutomationId(open, OpenId);

        status.Opacity = 0.7;
        status.VerticalAlignment = VerticalAlignment.Center;
        status.TextTrimming = TextTrimming.CharacterEllipsis;

        var footer = new Grid { ColumnSpacing = 8, Margin = new Thickness(0, 8, 0, 0) };
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        footer.Children.Add(status);
        Grid.SetColumn(open, 1);
        footer.Children.Add(open);

        var layout = new Grid { Margin = new Thickness(12) };
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        search.Margin = new Thickness(0, 0, 0, 8);
        layout.Children.Add(search);
        Grid.SetRow(list, 1);
        layout.Children.Add(list);
        Grid.SetRow(footer, 2);
        layout.Children.Add(footer);
        host.Content = layout;
    }

    /// <summary>The user asked to open the session with this name.</summary>
    public event Action<string>? OpenRequested;

    /// <summary>Reads the saved sessions again. Call it on the engine thread, with the engine running.</summary>
    public void Reload()
    {
        try
        {
            all = [.. PuttySessions.List()
                .Where(name => !name.Equals(PuttySessions.DefaultSettingsName, StringComparison.Ordinal))
                .Order(StringComparer.CurrentCultureIgnoreCase)];
            status.Text = all.Length == 0 ? "No saved PuTTY sessions" : $"{all.Length} saved session{(all.Length == 1 ? string.Empty : "s")}";
        }
        catch (Exception ex) when (ex is InvalidOperationException or DllNotFoundException)
        {
            all = [];
            status.Text = "Sessions are unavailable: " + ex.Message;
        }

        ApplyFilter();
    }

    private void ApplyFilter()
    {
        string text = search.Text.Trim();
        string? keep = list.SelectedItem as string;
        string[] shown = text.Length == 0
            ? [.. all]
            : [.. all.Where(name => name.Contains(text, StringComparison.CurrentCultureIgnoreCase))];
        list.ItemsSource = shown;
        if (keep is not null && Array.IndexOf(shown, keep) >= 0)
        {
            list.SelectedItem = keep;
        }
        else if (text.Length > 0 && shown.Length > 0)
        {
            list.SelectedIndex = 0;     // typing narrows the list to the one you mean: Enter opens it
        }
    }

    private void OnListKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Enter)
        {
            OpenSelected();
            e.Handled = true;
        }
    }

    private void OpenSelected()
    {
        if (list.SelectedItem is string name)
        {
            OpenRequested?.Invoke(name);
        }
    }
}
