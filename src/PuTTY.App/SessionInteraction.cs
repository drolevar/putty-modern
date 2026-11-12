using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using PuTTY.Interop;

namespace PuTTY.App;

/// <summary>
/// Asks the user what a session's engine asks: passwords, passphrases and keyboard-interactive
/// questions, the SSH host key, weak-algorithm warnings, and tells them about fatal errors. Each is a
/// <see cref="ContentDialog"/>, one at a time (WinUI allows one per window), answered whenever the
/// user gets to it: the engine waits, the UI thread, which is also the engine thread, does not.
/// </summary>
/// <remarks>
/// Built from code, with no XAML and no subclass of a control, like <see cref="TerminalViewController"/>:
/// every control here is constructed by this code, so NativeAOT keeps its projection (see the anchors
/// rule in CLAUDE.md). The buttons carry PuTTY's own wording, which its host key text refers to
/// ("press Accept").
/// </remarks>
internal sealed class SessionInteraction : IDisposable
{
    private readonly PuttySession session;
    private readonly Func<XamlRoot?> xamlRoot;
    private readonly Action returnFocus;
    private Task tail = Task.CompletedTask;
    private ContentDialog? open;
    private bool ended;

    public SessionInteraction(PuttySession session, Func<XamlRoot?> xamlRoot, Action returnFocus)
    {
        this.session = session;
        this.xamlRoot = xamlRoot;
        this.returnFocus = returnFocus;
        session.PromptRequested += OnPrompt;
        session.ConfirmationRequested += OnConfirmation;
        session.Error += OnError;
        session.Closed += OnClosed;
    }

    public void Dispose()
    {
        session.PromptRequested -= OnPrompt;
        session.ConfirmationRequested -= OnConfirmation;
        session.Error -= OnError;
        session.Closed -= OnClosed;
        Close();
    }

    /// <summary>The session is over: a question still on screen is no use, and none is shown any more.</summary>
    private void Close()
    {
        ended = true;
        try
        {
            open?.Hide();
        }
        catch (Exception)
        {
            // already closing
        }
    }

    private void OnClosed(object? sender, EventArgs e) => Close();

    // ----- Showing one dialog at a time -------------------------------------------------------

    /// <param name="isQuestion">
    /// True for a question of the session, which is withdrawn if the session ends first; false for
    /// news about the end of it, which is shown regardless.
    /// </param>
    private async Task<ContentDialogResult> ShowAsync(ContentDialog dialog, bool isQuestion = true)
    {
        Task previous = tail;
        var gate = new TaskCompletionSource();
        tail = gate.Task;
        try
        {
            await previous;
            if (isQuestion && ended)
            {
                return ContentDialogResult.None;
            }

            open = isQuestion ? dialog : null;
            return await dialog.ShowAsync();
        }
        catch (Exception ex)
        {
            // for instance another dialog got in first: treat it as a refusal, never as a crash
            PuttyLog.Warning("app", $"Dialog failed: {ex.Message}");
            return ContentDialogResult.None;
        }
        finally
        {
            open = null;
            gate.SetResult();
            returnFocus();
        }
    }

    private ContentDialog? NewDialog(string title)
    {
        XamlRoot? root = xamlRoot();
        return root is null ? null : new ContentDialog { XamlRoot = root, Title = title };
    }

    // ----- Prompts ----------------------------------------------------------------------------

    private async void OnPrompt(object? sender, SessionPromptEventArgs e)
    {
        string title = e.Name.Length > 0 ? e.Name : "Authentication";
        ContentDialog? dialog = NewDialog(title);
        if (dialog is null)
        {
            e.Cancel();
            return;
        }

        var panel = new StackPanel { Spacing = 8, MinWidth = 380 };
        if (e.Instruction.Length > 0)
        {
            panel.Children.Add(new TextBlock { Text = e.Instruction, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true });
        }

        var inputs = new List<Control>();
        for (int i = 0; i < e.Fields.Count; i++)
        {
            panel.Children.Add(new TextBlock { Text = e.Fields[i].Text, TextWrapping = TextWrapping.Wrap });
            Control input = e.Fields[i].Echo
                ? new TextBox()
                : new PasswordBox { PasswordRevealMode = PasswordRevealMode.Peek };
            AutomationProperties.SetAutomationId(input, $"PromptField{i}");
            AutomationProperties.SetName(input, e.Fields[i].Text);
            inputs.Add(input);
            panel.Children.Add(input);
        }

        dialog.Content = panel;
        dialog.PrimaryButtonText = "OK";
        dialog.CloseButtonText = "Cancel";
        dialog.DefaultButton = ContentDialogButton.Primary;
        if (inputs.Count > 0)
        {
            dialog.Opened += (_, _) => inputs[0].Focus(FocusState.Programmatic);
        }

        ContentDialogResult result = await ShowAsync(dialog);

        if (result != ContentDialogResult.Primary)
        {
            e.Cancel();
            return;
        }

        string[] answers = new string[inputs.Count];
        for (int i = 0; i < answers.Length; i++)
        {
            answers[i] = inputs[i] switch
            {
                PasswordBox password => password.Password,
                TextBox text => text.Text,
                _ => string.Empty,
            };
        }

        e.Respond(answers);
    }

    // ----- Confirmations ----------------------------------------------------------------------

    private async void OnConfirmation(object? sender, SessionConfirmEventArgs e)
    {
        string title = e.Items.FirstOrDefault(i => i.Kind == DialogTextKind.Title).Text ?? string.Empty;
        ContentDialog? dialog = NewDialog(title.Length > 0 ? title : "Confirm");
        if (dialog is null)
        {
            e.Cancel();
            return;
        }

        var panel = new StackPanel { Spacing = 10, MinWidth = 440 };
        var more = new StackPanel { Spacing = 4 };
        foreach (DialogTextItem item in e.Items)
        {
            switch (item.Kind)
            {
                case DialogTextKind.Paragraph:
                    panel.Children.Add(new TextBlock { Text = item.Text, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true });
                    break;
                case DialogTextKind.Display:
                    panel.Children.Add(new TextBlock
                    {
                        Text = item.Text,
                        FontFamily = new FontFamily("Consolas"),
                        TextWrapping = TextWrapping.Wrap,
                        IsTextSelectionEnabled = true,
                    });
                    break;
                case DialogTextKind.ScaryHeading:
                    panel.Children.Add(new TextBlock
                    {
                        Text = item.Text,
                        FontWeight = FontWeights.Bold,
                        Foreground = new SolidColorBrush(Colors.Crimson),
                        TextWrapping = TextWrapping.Wrap,
                    });
                    break;
                case DialogTextKind.MoreInfoKey:
                    more.Children.Add(new TextBlock { Text = item.Text, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 6, 0, 0) });
                    break;
                case DialogTextKind.MoreInfoValueShort:
                case DialogTextKind.MoreInfoValueBlob:
                    more.Children.Add(new TextBlock
                    {
                        Text = item.Text,
                        FontFamily = new FontFamily("Consolas"),
                        TextWrapping = TextWrapping.Wrap,
                        IsTextSelectionEnabled = true,
                    });
                    break;
                default:
                    break;      // the title is the dialog's; the prompt and the batch-mode text are for a terminal
            }
        }

        if (more.Children.Count > 0)
        {
            panel.Children.Add(new Expander
            {
                Header = "More information",
                Content = new ScrollViewer { MaxHeight = 220, Content = more },
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
            });
        }

        dialog.Content = new ScrollViewer { Content = panel, MaxHeight = 520 };
        if (e.Kind == SessionConfirmKind.HostKey)
        {
            // PuTTY's own words for the buttons, which its text refers to ("press Accept").
            dialog.PrimaryButtonText = "Accept";
            dialog.SecondaryButtonText = "Connect Once";
            dialog.CloseButtonText = "Cancel";
            dialog.DefaultButton = ContentDialogButton.None;
        }
        else if (e.Kind == SessionConfirmKind.LogFileExists)
        {
            // What PuTTY's own box offers when the session log file exists; Overwrite replaces data, so it is not the default.
            dialog.PrimaryButtonText = "Append";
            dialog.SecondaryButtonText = "Overwrite";
            dialog.CloseButtonText = "Disable logging";
            dialog.DefaultButton = ContentDialogButton.Primary;
        }
        else
        {
            dialog.PrimaryButtonText = "Yes";
            dialog.CloseButtonText = "No";
            dialog.DefaultButton = ContentDialogButton.Close;
        }

        ContentDialogResult result = await ShowAsync(dialog);

        if (e.Kind == SessionConfirmKind.LogFileExists)
        {
            switch (result)
            {
                case ContentDialogResult.Primary:
                    e.Append();
                    break;
                case ContentDialogResult.Secondary:
                    e.Overwrite();
                    break;
                default:
                    e.Cancel();         // no logging for this session; the connection goes on
                    break;
            }

            return;
        }

        switch (result)
        {
            case ContentDialogResult.Primary:
                e.Accept();
                break;
            case ContentDialogResult.Secondary:
                e.ConnectOnce();
                break;
            default:
                e.Cancel();
                break;
        }
    }

    // ----- Errors -----------------------------------------------------------------------------

    /// <summary>A plain message with an OK button, for when no session exists to ask or tell.</summary>
    public static async Task ShowMessageAsync(XamlRoot? root, string title, string message)
    {
        if (root is null)
        {
            return;
        }

        var dialog = new ContentDialog
        {
            XamlRoot = root,
            Title = title,
            Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true, MinWidth = 380 },
            CloseButtonText = "OK",
            DefaultButton = ContentDialogButton.Close,
        };
        try
        {
            await dialog.ShowAsync();
        }
        catch (Exception ex)
        {
            PuttyLog.Warning("app", $"Dialog failed: {ex.Message}");
        }
    }

    private async void OnError(object? sender, SessionErrorEventArgs e)
    {
        if (!e.IsFatal)
        {
            return;
        }

        ContentDialog? dialog = NewDialog("Connection error");
        if (dialog is null)
        {
            return;
        }

        dialog.Content = new TextBlock { Text = e.Message, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true, MinWidth = 380 };
        dialog.CloseButtonText = "OK";
        dialog.DefaultButton = ContentDialogButton.Close;

        await ShowAsync(dialog, isQuestion: false);
    }
}
