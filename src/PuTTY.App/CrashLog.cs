using System.Globalization;
using System.Text;

namespace PuTTY.App;

/// <summary>
/// Keeps what an unhandled exception said: XAML turns one into a fail-fast of the whole process ("stowed exception",
/// exit code 0xC000027B) and the native stack of that shows nothing of the managed code that threw it. The text goes to
/// <c>%LOCALAPPDATA%\PuTTY Modern\crash.log</c> (appended, never read by the app).
/// </summary>
internal static class CrashLog
{
    internal static string FilePath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PuTTY Modern", "crash.log");

    /// <summary>Hooks the places where an exception can end the process.</summary>
    internal static void Install(Microsoft.UI.Xaml.Application application)
    {
        application.UnhandledException += (_, e) => Write("Application.UnhandledException", e.Exception, e.Message);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Write("AppDomain.UnhandledException", e.ExceptionObject as Exception, null);
    }

    internal static void Write(string source, Exception? exception, string? message)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            var text = new StringBuilder();
            text.Append(CultureInfo.InvariantCulture, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {source}").AppendLine();
            if (message is { Length: > 0 })
            {
                text.AppendLine(message);
            }

            text.AppendLine(exception?.ToString() ?? "(no exception object)");
            text.AppendLine();
            File.AppendAllText(FilePath, text.ToString());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // nothing more can be done about a crash log that cannot be written
        }
    }
}
