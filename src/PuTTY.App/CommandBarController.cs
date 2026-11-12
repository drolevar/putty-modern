using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using PuTTY.Sessions;

namespace PuTTY.App;

/// <summary>
/// The session command bar under the title bar (the sketch's "Session CommandBar", frame <c>9:2</c>): New session, Reconnect and
/// Disconnect, then Split pane (a split button: the arrow lists the arrangements of the terminals, the button repeats the last
/// one) and Save layout on the left; on the right the layout drop-down (the built-in Default and the layouts the user saved),
/// Settings and the three-dot menu (Duplicate session, New folder, About). Two <see cref="CommandBar"/>s in one row, because a
/// command bar puts its commands at one end only.
/// </summary>
/// <remarks>
/// Built from code like the other panels (NativeAOT rules in CLAUDE.md: every control is constructed here). It knows nothing of
/// sessions: it raises events and is told what is enabled (<see cref="SetSession"/>) and which layouts exist (<see cref="SetLayouts"/>).
/// </remarks>
internal sealed class CommandBarController
{
    /// <summary>The AutomationIds, for UI Automation checks.</summary>
    public const string NewSessionId = "CommandNewSession";
    public const string ReconnectId = "CommandReconnect";
    public const string DisconnectId = "CommandDisconnect";
    public const string SplitPaneId = "CommandSplitPane";
    public const string SaveLayoutId = "CommandSaveLayout";
    public const string LayoutId = "CommandLayout";
    public const string SettingsId = "CommandSettings";
    public const string DuplicateId = "CommandDuplicate";
    public const string NewFolderId = "CommandNewFolder";
    public const string AboutId = "CommandAbout";
    public const string ArrangementIdPrefix = "Arrangement:";
    public const string LayoutItemIdPrefix = "Layout:";
    public const string DeleteLayoutId = "CommandDeleteLayout";

    private readonly AppBarButton newSession = new();
    private readonly AppBarButton reconnect = new();
    private readonly AppBarButton disconnect = new();
    private readonly AppBarButton saveLayout = new();
    private readonly AppBarButton settings = new();
    private readonly AppBarButton duplicate = new();
    private readonly AppBarButton newFolder = new();
    private readonly AppBarButton about = new();
    private readonly SplitButton splitPane = new();
    private readonly MenuFlyout splitMenu = new();
    private readonly SplitButton layout = new();
    private readonly MenuFlyout layoutMenu = new();
    private Arrangement lastArrangement = Arrangement.SideBySide;
    private string currentLayout = LayoutNames.Default;

    public CommandBarController(UserControl host)
    {
        Configure(newSession, "New session", 0xE710, NewSessionId);          // Add
        Configure(reconnect, "Reconnect", 0xE72C, ReconnectId);              // Refresh
        Configure(disconnect, "Disconnect", 0xE711, DisconnectId);           // Cancel
        Configure(saveLayout, "Save layout", 0xE74E, SaveLayoutId);          // Save
        Configure(settings, "Settings", 0xE713, SettingsId);                 // Setting
        Configure(duplicate, "Duplicate session", 0xE8C8, DuplicateId);      // Copy
        Configure(newFolder, "New folder...", 0xE8F4, NewFolderId);          // NewFolder
        Configure(about, "About PuTTY", 0xE946, AboutId);                    // Info

        newSession.Click += (_, _) => NewSessionRequested?.Invoke();
        reconnect.Click += (_, _) => ReconnectRequested?.Invoke();
        disconnect.Click += (_, _) => DisconnectRequested?.Invoke();
        saveLayout.Click += (_, _) => SaveLayoutRequested?.Invoke();
        settings.Click += (_, _) => SettingsRequested?.Invoke();
        duplicate.Click += (_, _) => DuplicateRequested?.Invoke();
        newFolder.Click += (_, _) => NewFolderRequested?.Invoke();
        about.Click += (_, _) => AboutRequested?.Invoke();

        // Split pane: the button repeats the last arrangement, the arrow lists them.
        splitPane.Content = "Split pane";
        splitPane.Flyout = splitMenu;
        splitPane.Click += (_, _) => ArrangementRequested?.Invoke(lastArrangement);
        AutomationProperties.SetAutomationId(splitPane, SplitPaneId);
        AutomationProperties.SetName(splitPane, "Split pane");
        foreach (Arrangement arrangement in Arrangements.All)
        {
            Arrangement chosen = arrangement;
            var item = new MenuFlyoutItem { Text = Arrangements.Title(arrangement) };
            AutomationProperties.SetAutomationId(item, ArrangementIdPrefix + arrangement);
            item.Click += (_, _) =>
            {
                lastArrangement = chosen;
                ArrangementRequested?.Invoke(chosen);
            };
            splitMenu.Items.Add(item);
        }

        // Layout: the main button applies the current layout again (puts the panels back), the arrow lists them.
        layout.Flyout = layoutMenu;
        layout.Click += (_, _) => LayoutRequested?.Invoke(currentLayout);
        AutomationProperties.SetAutomationId(layout, LayoutId);
        SetLayouts(Array.Empty<string>(), LayoutNames.Default);

        var left = NewBar(HorizontalAlignment.Left);
        left.PrimaryCommands.Add(newSession);
        left.PrimaryCommands.Add(reconnect);
        left.PrimaryCommands.Add(disconnect);
        left.PrimaryCommands.Add(new AppBarSeparator());
        left.PrimaryCommands.Add(new AppBarElementContainer { Content = splitPane, VerticalContentAlignment = VerticalAlignment.Center });
        left.PrimaryCommands.Add(saveLayout);

        var right = NewBar(HorizontalAlignment.Right);
        right.PrimaryCommands.Add(new AppBarElementContainer { Content = layout, VerticalContentAlignment = VerticalAlignment.Center });
        right.PrimaryCommands.Add(settings);
        right.SecondaryCommands.Add(duplicate);
        right.SecondaryCommands.Add(newFolder);
        right.SecondaryCommands.Add(new AppBarSeparator());
        right.SecondaryCommands.Add(about);
        AutomationProperties.SetName(right, "Commands");

        var row = new Grid { Padding = new Thickness(8, 0, 8, 0) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(left, 0);
        Grid.SetColumn(right, 2);
        row.Children.Add(left);
        row.Children.Add(right);

        // The sketch has the base solid background and a one-pixel divider under the bar.
        var frame = new Border { Child = row, BorderThickness = new Thickness(0, 0, 0, 1) };
        if (Application.Current.Resources.TryGetValue("SolidBackgroundFillColorBaseBrush", out object? background) && background is Brush backgroundBrush)
        {
            frame.Background = backgroundBrush;
        }

        if (Application.Current.Resources.TryGetValue("DividerStrokeColorDefaultBrush", out object? divider) && divider is Brush dividerBrush)
        {
            frame.BorderBrush = dividerBrush;
        }

        host.Content = frame;
        SetSession(hasSession: false, connected: false, tabCount: 0);
    }

    public event Action? NewSessionRequested;

    public event Action? ReconnectRequested;

    public event Action? DisconnectRequested;

    /// <summary>The user chose how the terminals are arranged (or pressed Split pane, which repeats the last choice).</summary>
    public event Action<Arrangement>? ArrangementRequested;

    public event Action? SaveLayoutRequested;

    /// <summary>The user chose a layout (<see cref="LayoutNames.Default"/> for the built-in one), or pressed the button, which asks for the current one again.</summary>
    public event Action<string>? LayoutRequested;

    /// <summary>The user asked to forget a saved layout.</summary>
    public event Action<string>? DeleteLayoutRequested;

    public event Action? SettingsRequested;

    public event Action? DuplicateRequested;

    public event Action? NewFolderRequested;

    public event Action? AboutRequested;

    /// <summary>What there is to act on: whether a session is active, whether it is connected, and how many tabs are open.</summary>
    public void SetSession(bool hasSession, bool connected, int tabCount)
    {
        reconnect.IsEnabled = hasSession && !connected;
        disconnect.IsEnabled = hasSession && connected;
        settings.IsEnabled = hasSession && connected;           // PuTTY's Change Settings needs a running backend, as the details panel's button does
        duplicate.IsEnabled = hasSession;
        splitPane.IsEnabled = tabCount >= 2;                    // one terminal has nothing to arrange
    }

    /// <summary>The saved layouts and which one is current: the drop-down's label and its items.</summary>
    public void SetLayouts(IReadOnlyList<string> names, string current)
    {
        currentLayout = current;
        layout.Content = $"Layout: {current}";
        AutomationProperties.SetName(layout, $"Layout: {current}");
        layoutMenu.Items.Clear();

        layoutMenu.Items.Add(LayoutItem(LayoutNames.Default, string.Equals(current, LayoutNames.Default, StringComparison.OrdinalIgnoreCase)));
        if (names.Count > 0)
        {
            layoutMenu.Items.Add(new MenuFlyoutSeparator());
        }

        foreach (string name in names)
        {
            layoutMenu.Items.Add(LayoutItem(name, string.Equals(name, current, StringComparison.OrdinalIgnoreCase)));
        }

        layoutMenu.Items.Add(new MenuFlyoutSeparator());
        var delete = new MenuFlyoutItem { Text = $"Delete \"{current}\"", IsEnabled = !string.Equals(current, LayoutNames.Default, StringComparison.OrdinalIgnoreCase) };
        AutomationProperties.SetAutomationId(delete, DeleteLayoutId);
        delete.Click += (_, _) => DeleteLayoutRequested?.Invoke(currentLayout);
        layoutMenu.Items.Add(delete);
    }

    private RadioMenuFlyoutItem LayoutItem(string name, bool isCurrent)
    {
        var item = new RadioMenuFlyoutItem { Text = name, GroupName = "layouts", IsChecked = isCurrent };
        AutomationProperties.SetAutomationId(item, LayoutItemIdPrefix + name);
        item.Click += (_, _) => LayoutRequested?.Invoke(name);
        return item;
    }

    private static CommandBar NewBar(HorizontalAlignment alignment) => new()
    {
        DefaultLabelPosition = CommandBarDefaultLabelPosition.Right,
        HorizontalAlignment = alignment,
        Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
        IsDynamicOverflowEnabled = true,
    };

    private static void Configure(AppBarButton button, string label, int glyph, string automationId)
    {
        button.Label = label;
        button.Icon = new FontIcon { Glyph = char.ConvertFromUtf32(glyph) };
        AutomationProperties.SetAutomationId(button, automationId);
        AutomationProperties.SetName(button, label);
    }
}
