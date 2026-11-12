using System.Text.Json;
using System.Text.Json.Serialization;

namespace PuTTY.Sessions;

/// <summary>
/// What the application remembers between runs of its workspace: how the panels and document groups are laid out
/// (the docking library's own layout text, kept as it is) and which sessions were open in which document, so that the
/// next start can open them again where they were. Separate from <see cref="SessionMetadata"/> (the folders), which
/// changes whenever the user moves a session, where this changes when the application closes.
/// </summary>
public sealed class WorkspaceState
{
    public const int CurrentVersion = 1;

    [JsonPropertyName("version")]
    public int Version { get; set; } = CurrentVersion;

    /// <summary>The docking layout (the library's XML), or null if none was saved.</summary>
    [JsonPropertyName("layout")]
    public string? Layout { get; set; }

    /// <summary>The sessions that were open, each named by the id of the document it was in.</summary>
    [JsonPropertyName("sessions")]
    public List<OpenSessionRecord> Sessions { get; set; } = [];

    /// <summary>The name of the named layout last applied or saved (shown in the command bar), or null for the built-in Default.</summary>
    [JsonPropertyName("layoutName")]
    public string? LayoutName { get; set; }
}

/// <summary>One open session: where it was in the layout and how to open it again.</summary>
public sealed class OpenSessionRecord
{
    /// <summary>The id of the document (tab) the session was in, as the layout names it.</summary>
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    /// <summary>The tab's title.</summary>
    [JsonPropertyName("title")]
    public string Title { get; set; } = string.Empty;

    /// <summary>The name of a session saved in PuTTY's storage, if it was opened from one: it is opened by name, with all its settings.</summary>
    [JsonPropertyName("savedName")]
    public string? SavedName { get; set; }

    /// <summary>Otherwise where it connected: host (or serial line), port and protocol (raw, telnet, rlogin, ssh), user name and key file.</summary>
    [JsonPropertyName("host")]
    public string? Host { get; set; }

    [JsonPropertyName("port")]
    public int Port { get; set; }

    [JsonPropertyName("protocol")]
    public string? Protocol { get; set; }

    [JsonPropertyName("userName")]
    public string? UserName { get; set; }

    [JsonPropertyName("keyFile")]
    public string? KeyFile { get; set; }

    /// <summary>What the details panel showed under the title ("10.20.0.12 . SSH"), kept so that it reads the same at once.</summary>
    [JsonPropertyName("description")]
    public string? Description { get; set; }
}

[JsonSourceGenerationOptions(WriteIndented = true, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(WorkspaceState))]
internal sealed partial class WorkspaceJsonContext : JsonSerializerContext;

/// <summary>
/// Reads and writes <see cref="WorkspaceState"/> as a JSON file next to the folders file. Like <see cref="MetadataStore"/>
/// it never throws for the file's state: a missing, damaged or newer file gives null (the application starts with its
/// default layout), and a write goes to a temporary file that is then moved over the old one, so a crash leaves no half file.
/// </summary>
public sealed class WorkspaceStateStore(string path)
{
    /// <summary><c>%APPDATA%\PuTTY Modern\layout.json</c>.</summary>
    public static string DefaultPath { get; } = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PuTTY Modern", "layout.json");

    public string FilePath { get; } = path;

    /// <summary>The saved state, or null if there is none or it cannot be used.</summary>
    public WorkspaceState? Load()
    {
        if (!File.Exists(FilePath))
        {
            return null;
        }

        try
        {
            using FileStream stream = File.OpenRead(FilePath);
            WorkspaceState? state = JsonSerializer.Deserialize(stream, WorkspaceJsonContext.Default.WorkspaceState);
            if (state is null || state.Version > WorkspaceState.CurrentVersion)
            {
                return null;        // empty, or written by a newer version: not for this one to interpret
            }

            state.Sessions ??= [];
            return state;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return null;
        }
    }

    public void Save(WorkspaceState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        string? directory = System.IO.Path.GetDirectoryName(FilePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        state.Version = WorkspaceState.CurrentVersion;
        string temporary = FilePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (FileStream stream = new(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, state, WorkspaceJsonContext.Default.WorkspaceState);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporary, FilePath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }
}
