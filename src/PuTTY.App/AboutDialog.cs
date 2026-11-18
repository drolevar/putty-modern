using System.Text;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using PuTTY.Interop;
using PuTTY.Sessions;

namespace PuTTY.App;

/// <summary>
/// The About dialog: the program's name and author, what of PuTTY it contains and whose that is, a link to the official PuTTY,
/// and under "Licences" the full texts (<see cref="AboutText"/>). The texts are resources of the exe (see PuTTY.App.csproj), so
/// the notice that PuTTY's licence asks for is inside the program however it was installed. The dialog's title stays "About
/// PuTTY" like the window titles; its text says PuTTY Modern. Built from code like the other dialogs (every control constructed
/// here, so nothing depends on NativeAOT finding a class that only native code creates).
/// </summary>
internal static class AboutDialog
{
    /// <summary>Shows the dialog over <paramref name="root"/> and returns when it is closed (a failure to show it is logged).</summary>
    public static async Task ShowAsync(XamlRoot? root)
    {
        if (root is null)
        {
            return;
        }

        var content = new StackPanel { Spacing = 12, MinWidth = 440 };

        var header = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 14 };
        if (TitleBarIcon.CreateBitmap() is { } bitmap)
        {
            header.Children.Add(new Microsoft.UI.Xaml.Controls.Image { Source = bitmap, Width = 48, Height = 48 });
        }

        var names = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        names.Children.Add(new TextBlock { Text = AboutText.Product, FontSize = 22, FontWeight = FontWeights.SemiBold });
        names.Children.Add(new TextBlock { Text = AboutText.Copyright, Opacity = 0.75 });
        header.Children.Add(names);
        content.Children.Add(header);

        content.Children.Add(new TextBlock { Text = AboutText.Summary, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true });
        content.Children.Add(new TextBlock { Text = AboutText.Credit, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true });

        var link = new HyperlinkButton
        {
            Content = "The official PuTTY",
            NavigateUri = new Uri(AboutText.OfficialPuttyUrl),
            Padding = new Thickness(0),
        };
        AutomationProperties.SetAutomationId(link, "AboutPuttyLink");
        content.Children.Add(link);

        // About 140 KB of text: a read-only TextBox scrolls it by itself and copes with that size, and the text is read only when
        // the section is first opened.
        var licences = new TextBox
        {
            IsReadOnly = true,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            MaxHeight = 260,
            FontFamily = new FontFamily("Cascadia Mono, Consolas"),
            FontSize = 12,
        };
        ScrollViewer.SetVerticalScrollBarVisibility(licences, ScrollBarVisibility.Auto);
        AutomationProperties.SetAutomationId(licences, "AboutLicenceText");
        AutomationProperties.SetName(licences, "Licences");

        var expander = new Expander
        {
            Header = "Licences",
            Content = licences,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
        };
        AutomationProperties.SetAutomationId(expander, "AboutLicences");
        expander.Expanding += (_, _) => licences.Text = LicencesText();
        content.Children.Add(expander);

        var dialog = new ContentDialog
        {
            XamlRoot = root,
            Title = "About PuTTY",
            Content = content,
            CloseButtonText = "Close",
            DefaultButton = ContentDialogButton.Close,
        };
        AutomationProperties.SetAutomationId(dialog, "AboutDialog");

        try
        {
            await dialog.ShowAsync();
        }
        catch (Exception ex)
        {
            PuttyLog.Warning("app", $"The About dialog failed: {ex.Message}");
        }
    }

    private static string LicencesText() =>
        AboutText.LicencesText(Resource("LICENSE.txt"), Resource("THIRD-PARTY-NOTICES.txt"), Resource("dotnet-runtime-THIRD-PARTY-NOTICES.txt"))
            .ReplaceLineEndings("\r\n");

    /// <summary>One of the texts built into the exe, or a note saying it is missing (which a build check would have caught).</summary>
    private static string Resource(string name)
    {
        using Stream? stream = typeof(AboutDialog).Assembly.GetManifestResourceStream(name);
        if (stream is null)
        {
            PuttyLog.Warning("app", $"The resource {name} is not in the exe");
            return $"({name} is missing from this build.)";
        }

        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }
}
