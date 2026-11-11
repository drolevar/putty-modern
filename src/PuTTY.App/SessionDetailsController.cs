using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace PuTTY.App;

/// <summary>
/// The Session details panel: the open session's name, where it connects, whether it is connected, and the
/// Session settings button that opens PuTTY's Change Settings on it (the sketch's inspector, frame <c>9:2</c>; its
/// key exchange, cipher and host key rows need engine accessors that do not exist yet).
/// </summary>
/// <remarks>Built from code on a plain <see cref="UserControl"/>, like the other panels (NativeAOT rules in CLAUDE.md).</remarks>
internal sealed class SessionDetailsController
{
    /// <summary>The AutomationIds, for UI Automation checks.</summary>
    public const string SettingsId = "ChangeSettingsButton";
    public const string ReconnectId = "ReconnectButton";
    public const string DuplicateId = "DuplicateButton";
    public const string NameId = "SessionDetailsName";
    public const string StatusId = "SessionDetailsStatus";

    private readonly TextBlock name = new();
    private readonly TextBlock where = new();
    private readonly TextBlock status = new();
    private readonly Button settings = new();
    private readonly Button reconnect = new();
    private readonly Button duplicate = new();
    private bool hasSession;

    public SessionDetailsController(UserControl host)
    {
        name.FontSize = 18;
        name.FontWeight = FontWeights.SemiBold;
        name.TextTrimming = TextTrimming.CharacterEllipsis;
        AutomationProperties.SetAutomationId(name, NameId);

        where.FontSize = 12;
        where.TextTrimming = TextTrimming.CharacterEllipsis;
        Dim(where);

        status.Margin = new Thickness(0, 12, 0, 0);
        AutomationProperties.SetAutomationId(status, StatusId);

        settings.Content = "Session settings";
        settings.HorizontalAlignment = HorizontalAlignment.Stretch;
        settings.Margin = new Thickness(0, 20, 0, 0);
        settings.Click += (_, _) => SettingsRequested?.Invoke();
        AutomationProperties.SetAutomationId(settings, SettingsId);
        AutomationProperties.SetName(settings, "Session settings");

        reconnect.Content = "Reconnect";
        reconnect.HorizontalAlignment = HorizontalAlignment.Stretch;
        reconnect.Margin = new Thickness(0, 8, 0, 0);
        reconnect.Click += (_, _) => ReconnectRequested?.Invoke();
        AutomationProperties.SetAutomationId(reconnect, ReconnectId);
        AutomationProperties.SetName(reconnect, "Reconnect");

        duplicate.Content = "Duplicate session";
        duplicate.HorizontalAlignment = HorizontalAlignment.Stretch;
        duplicate.Margin = new Thickness(0, 8, 0, 0);
        duplicate.Click += (_, _) => DuplicateRequested?.Invoke();
        AutomationProperties.SetAutomationId(duplicate, DuplicateId);
        AutomationProperties.SetName(duplicate, "Duplicate session");

        var stack = new StackPanel { Margin = new Thickness(16), Spacing = 2 };
        stack.Children.Add(name);
        stack.Children.Add(where);
        stack.Children.Add(status);
        stack.Children.Add(settings);
        stack.Children.Add(duplicate);
        stack.Children.Add(reconnect);
        host.Content = stack;
        Clear();
    }

    /// <summary>The user pressed Session settings.</summary>
    public event Action? SettingsRequested;

    /// <summary>The user pressed Reconnect (enabled once the connection has ended).</summary>
    public event Action? ReconnectRequested;

    /// <summary>The user pressed Duplicate session: another tab with a copy of this session's settings.</summary>
    public event Action? DuplicateRequested;

    /// <summary>A session is open: its name, where it connects (may be null), and that it is connected.</summary>
    public void Show(string title, string? description)
    {
        name.Text = title;
        where.Text = description ?? string.Empty;
        where.Visibility = string.IsNullOrEmpty(description) ? Visibility.Collapsed : Visibility.Visible;
        hasSession = true;
        SetConnected(true);
    }

    /// <summary>The connection came up or went away.</summary>
    public void SetConnected(bool connected)
    {
        status.Text = connected ? "Connected" : "Not connected";
        settings.IsEnabled = connected && hasSession;       // PuTTY's Change Settings needs a running backend
        reconnect.IsEnabled = !connected && hasSession;     // there is nothing to reconnect while connected
        duplicate.IsEnabled = hasSession;
    }

    /// <summary>No session.</summary>
    public void Clear()
    {
        name.Text = "No session";
        where.Text = string.Empty;
        where.Visibility = Visibility.Collapsed;
        status.Text = "Open a saved session, or start a new one.";
        hasSession = false;
        settings.IsEnabled = false;
        reconnect.IsEnabled = false;
        duplicate.IsEnabled = false;
    }

    private static void Dim(TextBlock text)
    {
        if (Application.Current.Resources.TryGetValue("TextFillColorSecondaryBrush", out object? value) && value is Brush brush)
        {
            text.Foreground = brush;
        }
    }
}
