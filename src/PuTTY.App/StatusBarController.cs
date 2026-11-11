using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;

namespace PuTTY.App;

/// <summary>
/// The status bar along the bottom of the window (the sketch's frame <c>9:2</c>): a dot and how many sessions are
/// connected on the left; on the right what the active session is: its protocol, the encoding, the terminal's size in
/// cells and the cursor's line and column.
/// </summary>
/// <remarks>Built from code on a plain <see cref="UserControl"/>, like the other panels (NativeAOT rules in CLAUDE.md).</remarks>
internal sealed class StatusBarController
{
    /// <summary>The AutomationIds, for UI Automation checks.</summary>
    public const string SessionsId = "StatusActiveSessions";
    public const string DetailsId = "StatusActiveDetails";

    private readonly Ellipse dot = new() { Width = 7, Height = 7, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock sessions = new() { FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock protocol = new();
    private readonly TextBlock encoding = new();
    private readonly TextBlock size = new();
    private readonly TextBlock position = new();

    public StatusBarController(UserControl host)
    {
        AutomationProperties.SetAutomationId(sessions, SessionsId);
        var left = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        left.Children.Add(dot);
        left.Children.Add(sessions);

        var right = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 24, Margin = new Thickness(0, 0, 16, 0), VerticalAlignment = VerticalAlignment.Center };
        AutomationProperties.SetAutomationId(right, DetailsId);
        foreach (TextBlock text in new[] { protocol, encoding, size, position })
        {
            text.FontSize = 12;
            text.VerticalAlignment = VerticalAlignment.Center;
            Dim(text);
            right.Children.Add(text);
        }

        var bar = new Grid { Height = 26 };
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        bar.Children.Add(left);
        Grid.SetColumn(right, 1);
        bar.Children.Add(right);
        host.Content = bar;
        host.HorizontalContentAlignment = HorizontalAlignment.Stretch;
        SetSessions(0);
        ClearActive();
    }

    /// <summary>How many sessions are connected.</summary>
    public void SetSessions(int connected)
    {
        sessions.Text = connected == 1 ? "1 active session" : $"{connected} active sessions";
        dot.Fill = ThemeBrush(connected > 0 ? "SystemFillColorSuccessBrush" : "TextFillColorDisabledBrush");
    }

    /// <summary>What the active session is: protocol (may be empty), and the terminal's size in cells if it has one yet.</summary>
    public void SetActive(string? protocolName, int? columns, int? rows)
    {
        protocol.Text = protocolName ?? string.Empty;
        encoding.Text = "UTF-8";            // the engine sends UTF-8
        size.Text = columns is { } c && rows is { } r ? $"{c} × {r}" : string.Empty;
    }

    /// <summary>The cursor's line and column, counted from 1 as the status bar of an editor does.</summary>
    public void SetCursor(int line, int column) => position.Text = $"Ln {line}, Col {column}";

    /// <summary>No active session: nothing on the right.</summary>
    public void ClearActive()
    {
        protocol.Text = string.Empty;
        encoding.Text = string.Empty;
        size.Text = string.Empty;
        position.Text = string.Empty;
    }

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
