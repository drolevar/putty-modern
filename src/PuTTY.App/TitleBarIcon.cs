using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using PuTTY.Interop;
using Windows.Storage.Streams;

namespace PuTTY.App;

/// <summary>
/// The image the title bars show in their icon slot. The <c>TitleBar</c> control draws its <c>IconSource</c> and nothing else
/// (not the window's icon, see <see cref="AppIcon"/>), and an image reached through <c>ms-appx:///</c> is a file in the app's
/// folder, which is one more component in the MSI. So the picture (<c>Assets\TitleBarIcon.png</c>, drawn by
/// <c>tools\make-app-art.ps1</c>) is a resource of the exe and is decoded from memory here.
/// </summary>
internal static class TitleBarIcon
{
    private const string ResourceName = "TitleBarIcon.png";

    /// <summary>The icon source for a <c>TitleBar</c>, or <c>null</c> (the slot stays empty) when the picture cannot be loaded.</summary>
    public static IconSource? Create()
    {
        try
        {
            using Stream? resource = typeof(TitleBarIcon).Assembly.GetManifestResourceStream(ResourceName);
            if (resource is null)
            {
                PuttyLog.Warning("app", $"The resource {ResourceName} is not in the exe: the title bar has no icon");
                return null;
            }

            byte[] bytes = new byte[resource.Length];
            resource.ReadExactly(bytes);

            var stream = new InMemoryRandomAccessStream();
            using (var writer = new DataWriter(stream))
            {
                writer.WriteBytes(bytes);
                writer.StoreAsync().GetAwaiter().GetResult();       // an in-memory stream: it completes at once
                writer.DetachStream();
            }

            stream.Seek(0);
            var image = new BitmapImage();
            image.SetSource(stream);
            return new ImageIconSource { ImageSource = image };
        }
        catch (Exception ex)
        {
            PuttyLog.Warning("app", "The title bar's icon could not be loaded", ex);
            return null;
        }
    }
}
