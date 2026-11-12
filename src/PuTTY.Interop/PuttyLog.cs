using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace PuTTY.Interop;

/// <summary>How much <see cref="PuttyLog"/> writes: each level includes the ones before it.</summary>
public enum PuttyLogLevel
{
    /// <summary>Nothing is written (the default).</summary>
    Off = 0,

    /// <summary>Things that failed.</summary>
    Error = 1,

    /// <summary>Things that went wrong without stopping anything, and refusals.</summary>
    Warning = 2,

    /// <summary>What the app does: sessions opened and closed, the serial line's settings, views attached, fonts.</summary>
    Information = 3,

    /// <summary>Why it did it: the terminal's fit to its window, the palette, PuTTY's own event-log lines.</summary>
    Debug = 4,

    /// <summary>Everything, including every colour of the palette and the size of each chunk of output.</summary>
    Trace = 5,
}

/// <summary>
/// The app's diagnostic log, a plain text file. <b>Off by default</b>: nothing is created or written until it is switched
/// on with <see cref="Configure"/>, which the app does from the environment variable <c>PUTTYMODERN_LOG</c> (a level name)
/// or the command-line option <c>--log[=level]</c>. The file is <c>%LOCALAPPDATA%\PuTTY Modern\logs\putty-modern-DATE-PID.log</c>
/// unless <c>PUTTYMODERN_LOG_FILE</c> or <c>--log-file=path</c> names another. The engine's own trace (native code) and PuTTY's
/// event-log lines arrive here too.
/// </summary>
/// <remarks>
/// Safe from any thread. A line is flushed as it is written, so what happened just before a hang or a crash is in the file.
/// The default folder keeps the ten newest files. The text of a password or of what the user types is never logged.
/// </remarks>
public static class PuttyLog
{
    private const int KeptFiles = 10;

    private static readonly Lock Gate = new();
    private static StreamWriter? writer;
    private static volatile int level;

    /// <summary>What is written now; <see cref="PuttyLogLevel.Off"/> until <see cref="Configure"/> succeeds.</summary>
    public static PuttyLogLevel Level => (PuttyLogLevel)level;

    /// <summary>The file being written, or null while the log is off.</summary>
    public static string? FilePath { get; private set; }

    /// <summary>The folder the log files go to unless another file is named.</summary>
    public static string DefaultFolder { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PuTTY Modern", "logs");

    /// <summary>True when a message at <paramref name="messageLevel"/> would be written. Check before building an expensive message.</summary>
    public static bool IsEnabled(PuttyLogLevel messageLevel) => (int)messageLevel <= level && level != 0;

    /// <summary>Reads a level name (<c>error</c>, <c>warning</c>, <c>info</c>, <c>debug</c>, <c>trace</c>, <c>off</c>, or a digit 0 to 5); <c>1</c> and <c>true</c> alone are not levels here, so see <see cref="TryParseSwitch"/>.</summary>
    public static bool TryParseLevel(string? text, out PuttyLogLevel result)
    {
        result = PuttyLogLevel.Off;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        switch (text.Trim().ToLowerInvariant())
        {
            case "off" or "none" or "0": result = PuttyLogLevel.Off; return true;
            case "error" or "err" or "1": result = PuttyLogLevel.Error; return true;
            case "warning" or "warn" or "2": result = PuttyLogLevel.Warning; return true;
            case "information" or "info" or "3": result = PuttyLogLevel.Information; return true;
            case "debug" or "4": result = PuttyLogLevel.Debug; return true;
            case "trace" or "verbose" or "5": result = PuttyLogLevel.Trace; return true;
            default: return false;
        }
    }

    /// <summary>
    /// The level the user asked for with the environment (<c>PUTTYMODERN_LOG</c>, <c>PUTTYMODERN_LOG_FILE</c>) and the
    /// command line (<c>--log</c>, <c>--log=level</c>, <c>--log-file=path</c> or <c>--log-file path</c>; the command line wins).
    /// A bare <c>--log</c>, or a value that is no level name (<c>1</c>, <c>true</c>), means <see cref="PuttyLogLevel.Debug"/>.
    /// Returns <see cref="PuttyLogLevel.Off"/> when nothing was asked for.
    /// </summary>
    public static PuttyLogLevel TryParseSwitch(IReadOnlyList<string> args, out string? file)
    {
        PuttyLogLevel requested = PuttyLogLevel.Off;
        file = Environment.GetEnvironmentVariable("PUTTYMODERN_LOG_FILE");
        if (Environment.GetEnvironmentVariable("PUTTYMODERN_LOG") is { Length: > 0 } fromEnvironment)
        {
            requested = TryParseLevel(fromEnvironment, out PuttyLogLevel parsed) ? parsed : PuttyLogLevel.Debug;
        }

        for (int i = 0; i < args.Count; i++)
        {
            string arg = args[i];
            if (arg.Equals("--log", StringComparison.OrdinalIgnoreCase))
            {
                requested = PuttyLogLevel.Debug;
            }
            else if (arg.StartsWith("--log=", StringComparison.OrdinalIgnoreCase))
            {
                requested = TryParseLevel(arg["--log=".Length..], out PuttyLogLevel parsed) ? parsed : PuttyLogLevel.Debug;
            }
            else if (arg.StartsWith("--log-file=", StringComparison.OrdinalIgnoreCase))
            {
                file = arg["--log-file=".Length..];
            }
            else if (arg.Equals("--log-file", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Count)
            {
                file = args[++i];
            }
        }

        if (string.IsNullOrWhiteSpace(file))
        {
            file = null;
        }

        // Naming a file without a level means the user wants the log.
        return requested == PuttyLogLevel.Off && file is not null ? PuttyLogLevel.Debug : requested;
    }

    /// <summary>
    /// Switches the log on at <paramref name="newLevel"/>, writing to <paramref name="file"/> or, when that is null, to a
    /// new file in <see cref="DefaultFolder"/> (older files beyond the ten newest are deleted). <see cref="PuttyLogLevel.Off"/>
    /// closes the log. Returns false, and leaves the log off, when the file cannot be opened; logging must never stop the app.
    /// </summary>
    public static bool Configure(PuttyLogLevel newLevel, string? file = null)
    {
        lock (Gate)
        {
            CloseLocked();
            if (newLevel == PuttyLogLevel.Off)
            {
                return true;
            }

            try
            {
                string path;
                if (file is null)
                {
                    Directory.CreateDirectory(DefaultFolder);
                    PruneDefaultFolder();
                    path = Path.Combine(DefaultFolder, string.Create(CultureInfo.InvariantCulture,
                        $"putty-modern-{DateTime.Now:yyyyMMdd-HHmmss}-{Environment.ProcessId}.log"));
                }
                else
                {
                    path = Path.GetFullPath(file);
                    if (Path.GetDirectoryName(path) is { Length: > 0 } directory)
                    {
                        Directory.CreateDirectory(directory);
                    }
                }

                var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
                writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true };
                FilePath = path;
                level = (int)newLevel;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                Debug.WriteLine($"The log file could not be opened: {ex.Message}");
                writer = null;
                FilePath = null;
                level = 0;
                return false;
            }

            WriteLocked(PuttyLogLevel.Information, "log", string.Create(CultureInfo.InvariantCulture,
                $"log started at level {newLevel}, process {Environment.ProcessId}, {Environment.OSVersion.VersionString}, .NET {Environment.Version}"));
            return true;
        }
    }

    /// <summary>Closes the file; later messages are dropped until <see cref="Configure"/> is called again.</summary>
    public static void Close()
    {
        lock (Gate)
        {
            CloseLocked();
        }
    }

    public static void Error(string category, string message) => Write(PuttyLogLevel.Error, category, message);

    public static void Error(string category, string message, Exception exception) =>
        Write(PuttyLogLevel.Error, category, $"{message}: {exception}");

    public static void Warning(string category, string message) => Write(PuttyLogLevel.Warning, category, message);

    public static void Warning(string category, string message, Exception exception) =>
        Write(PuttyLogLevel.Warning, category, $"{message}: {exception.GetType().Name}: {exception.Message}");

    public static void Info(string category, string message) => Write(PuttyLogLevel.Information, category, message);

    /// <summary>Debug level: why the app did what it did.</summary>
    public static void Detail(string category, string message) => Write(PuttyLogLevel.Debug, category, message);

    public static void Trace(string category, string message) => Write(PuttyLogLevel.Trace, category, message);

    /// <summary>Writes one line (or more, if the message has line breaks: those are indented) when the level is enabled.</summary>
    public static void Write(PuttyLogLevel messageLevel, string category, string message)
    {
        if (messageLevel <= PuttyLogLevel.Warning)
        {
            Debug.WriteLine($"{category}: {message}");     // still there for a debugger when the file log is off (debug builds)
        }

        if (!IsEnabled(messageLevel))
        {
            return;
        }

        lock (Gate)
        {
            WriteLocked(messageLevel, category, message);
        }
    }

    private static void WriteLocked(PuttyLogLevel messageLevel, string category, string message)
    {
        if (writer is null)
        {
            return;
        }

        string tag = messageLevel switch
        {
            PuttyLogLevel.Error => "ERR",
            PuttyLogLevel.Warning => "WRN",
            PuttyLogLevel.Information => "INF",
            PuttyLogLevel.Debug => "DBG",
            _ => "TRC",
        };
        string text = message.Contains('\n') ? message.Replace("\r\n", "\n").Replace("\n", Environment.NewLine + "    ") : message;
        try
        {
            writer.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {tag} [{Environment.CurrentManagedThreadId}] {category}: {text}"));
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            // A disk that filled up or a file that went away: the log stops, the app does not.
            writer = null;
            level = 0;
        }
    }

    private static void CloseLocked()
    {
        level = 0;
        FilePath = null;
        try
        {
            writer?.Dispose();
        }
        catch (IOException)
        {
            // nothing more can be done about a log file that cannot be closed
        }

        writer = null;
    }

    private static void PruneDefaultFolder()
    {
        try
        {
            FileInfo[] files = new DirectoryInfo(DefaultFolder).GetFiles("putty-modern-*.log");
            Array.Sort(files, (a, b) => b.LastWriteTimeUtc.CompareTo(a.LastWriteTimeUtc));
            for (int i = KeptFiles - 1; i < files.Length; i++)      // room for the one about to be made
            {
                files[i].Delete();
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // an old log that cannot be deleted is not worth a failure
        }
    }
}
