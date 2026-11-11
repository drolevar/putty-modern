using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using PuTTY.Interop;
using Windows.ApplicationModel.DataTransfer;

namespace PuTTY.App;

/// <summary>
/// The Session details panel (the sketch's inspector, frame <c>9:2</c>): the active session's name, whether it is connected,
/// where it connects (host, port, protocol, user name), what an SSH connection negotiated (key exchange, cipher, the host
/// key's fingerprint, with a button that copies it), and the buttons Session settings (PuTTY's Change Settings), Duplicate
/// session and Reconnect.
/// </summary>
/// <remarks>Built from code on a plain <see cref="UserControl"/>, like the other panels (NativeAOT rules in CLAUDE.md).</remarks>
internal sealed class SessionDetailsController
{
    /// <summary>The AutomationIds, for UI Automation checks.</summary>
    public const string SettingsId = "ChangeSettingsButton";
    public const string ReconnectId = "ReconnectButton";
    public const string DuplicateId = "DuplicateButton";
    public const string CopyFingerprintId = "CopyFingerprintButton";
    public const string NameId = "SessionDetailsName";
    public const string StatusId = "SessionDetailsStatus";
    public const string HostId = "SessionDetailsHost";
    public const string KeyExchangeId = "SessionDetailsKeyExchange";
    public const string CipherId = "SessionDetailsCipher";
    public const string FingerprintId = "SessionDetailsFingerprint";

    private readonly TextBlock name = new();
    private readonly TextBlock where = new();
    private readonly TextBlock status = new();
    private readonly Button settings = new();
    private readonly Button reconnect = new();
    private readonly Button duplicate = new();
    private readonly Button copyFingerprint = new();
    private readonly StackPanel connection = new() { Spacing = 6 };
    private readonly TextBlock host = new();
    private readonly TextBlock port = new();
    private readonly TextBlock protocol = new();
    private readonly TextBlock userName = new();
    private readonly StackPanel secure = new() { Spacing = 4, Visibility = Visibility.Collapsed };
    private readonly TextBlock keyExchange = new();
    private readonly TextBlock cipher = new();
    private readonly TextBlock fingerprint = new();
    private bool hasSession;
    private string? fingerprintText;

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

        // CONNECTION: where it connects, as the session's settings say now
        var connectionCaption = Caption("CONNECTION");
        connection.Margin = new Thickness(0, 16, 0, 0);
        connection.Children.Add(connectionCaption);
        connection.Children.Add(Row("Host", this.host, HostId));
        connection.Children.Add(Row("Port", port));
        connection.Children.Add(Row("Protocol", protocol));
        connection.Children.Add(Row("Username", userName));

        // SECURE CONNECTION: what the SSH connection negotiated
        var heading = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        heading.Children.Add(new FontIcon { Glyph = char.ConvertFromUtf32(0xE72E), FontSize = 14, VerticalAlignment = VerticalAlignment.Center });   // Lock
        heading.Children.Add(new TextBlock { Text = "Secure connection", FontWeight = FontWeights.SemiBold });
        secure.Margin = new Thickness(0, 16, 0, 0);
        secure.Children.Add(heading);
        secure.Children.Add(Stacked("Key exchange", keyExchange, KeyExchangeId));
        secure.Children.Add(Stacked("Cipher", cipher, CipherId));
        secure.Children.Add(Stacked("Host key fingerprint", fingerprint, FingerprintId));
        copyFingerprint.Content = "Copy fingerprint";
        copyFingerprint.HorizontalAlignment = HorizontalAlignment.Stretch;
        copyFingerprint.Margin = new Thickness(0, 6, 0, 0);
        copyFingerprint.Click += (_, _) => CopyFingerprint();
        AutomationProperties.SetAutomationId(copyFingerprint, CopyFingerprintId);
        AutomationProperties.SetName(copyFingerprint, "Copy fingerprint");
        secure.Children.Add(copyFingerprint);

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
        stack.Children.Add(connection);
        stack.Children.Add(secure);
        stack.Children.Add(settings);
        stack.Children.Add(duplicate);
        stack.Children.Add(reconnect);
        var scroll = new ScrollViewer { Content = stack, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        host.Content = scroll;
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

    /// <summary>Where the active session connects (null: not known, the rows are hidden).</summary>
    public void ShowEndpoint(SessionEndpoint? endpoint)
    {
        connection.Visibility = endpoint is null ? Visibility.Collapsed : Visibility.Visible;
        if (endpoint is null)
        {
            return;
        }

        host.Text = endpoint.Host;
        port.Text = endpoint.Protocol == SavedSessionProtocol.Serial ? "-" : endpoint.Port.ToString(System.Globalization.CultureInfo.CurrentCulture);
        protocol.Text = endpoint.Protocol switch
        {
            SavedSessionProtocol.Raw => "Raw",
            SavedSessionProtocol.Telnet => "Telnet",
            SavedSessionProtocol.Rlogin => "Rlogin",
            SavedSessionProtocol.Ssh => "SSH",
            SavedSessionProtocol.Serial => "Serial",
            _ => "Other",
        };
        userName.Text = endpoint.UserName ?? "-";
    }

    /// <summary>What the active session's SSH connection negotiated (null or nothing known: the section is hidden).</summary>
    public void ShowFacts(ConnectionFacts? facts)
    {
        bool known = facts is { Any: true };
        secure.Visibility = known ? Visibility.Visible : Visibility.Collapsed;
        if (!known)
        {
            fingerprintText = null;
            return;
        }

        keyExchange.Text = facts!.KeyExchange ?? "-";
        cipher.Text = facts.Cipher is null ? "-" : facts.Mac is null ? facts.Cipher : $"{facts.Cipher}, {facts.Mac}";
        fingerprintText = facts.Fingerprint;
        fingerprint.Text = facts.HostKey ?? "-";
        copyFingerprint.IsEnabled = fingerprintText is not null;
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
        connection.Visibility = Visibility.Collapsed;
        secure.Visibility = Visibility.Collapsed;
        fingerprintText = null;
    }

    private void CopyFingerprint()
    {
        if (fingerprintText is null)
        {
            return;
        }

        var package = new DataPackage();
        package.SetText(fingerprintText);
        Clipboard.SetContent(package);
    }

    /// <summary>A small grey caption over a group of rows.</summary>
    private static TextBlock Caption(string text)
    {
        var caption = new TextBlock { Text = text, FontSize = 11 };
        Dim(caption);
        return caption;
    }

    /// <summary>"Host        10.20.0.12": a label on the left and the value on the right.</summary>
    private static Grid Row(string label, TextBlock value, string? automationId = null)
    {
        var row = new Grid { ColumnSpacing = 12 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(96) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var text = new TextBlock { Text = label };
        Dim(text);
        value.TextTrimming = TextTrimming.CharacterEllipsis;
        Grid.SetColumn(value, 1);
        row.Children.Add(text);
        row.Children.Add(value);
        if (automationId is not null)
        {
            AutomationProperties.SetAutomationId(value, automationId);
        }

        return row;
    }

    /// <summary>A label with its value under it (for values that are long: they wrap).</summary>
    private static StackPanel Stacked(string label, TextBlock value, string automationId)
    {
        var stack = new StackPanel { Spacing = 0, Margin = new Thickness(0, 6, 0, 0) };
        var text = new TextBlock { Text = label };
        Dim(text);
        value.TextWrapping = TextWrapping.Wrap;
        value.IsTextSelectionEnabled = true;
        AutomationProperties.SetAutomationId(value, automationId);
        stack.Children.Add(text);
        stack.Children.Add(value);
        return stack;
    }

    private static void Dim(TextBlock text)
    {
        if (Application.Current.Resources.TryGetValue("TextFillColorSecondaryBrush", out object? value) && value is Brush brush)
        {
            text.Foreground = brush;
        }
    }
}
