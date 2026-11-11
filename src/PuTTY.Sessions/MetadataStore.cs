using System.Text.Json;
using System.Text.Json.Serialization;

namespace PuTTY.Sessions;

[JsonSourceGenerationOptions(WriteIndented = true, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(SessionMetadata))]
internal sealed partial class MetadataJsonContext : JsonSerializerContext;

/// <summary>
/// Reads and writes <see cref="SessionMetadata"/> as a JSON file of the application's own. The file is
/// not essential: a missing, damaged or unreadable one gives an empty set, never an exception, and a damaged
/// file is moved aside rather than overwritten, so nothing the user might want back is destroyed silently.
/// </summary>
public sealed class MetadataStore(string path)
{
    /// <summary><c>%APPDATA%\PuTTY Modern\workspace.json</c>, next to nothing PuTTY owns.</summary>
    public static string DefaultPath { get; } = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PuTTY Modern", "workspace.json");

    public string FilePath { get; } = path;

    /// <summary>Why the last <see cref="Load"/> gave an empty or read-only set, or null if it read the file normally.</summary>
    public string? LoadProblem { get; private set; }

    public SessionMetadata Load()
    {
        LoadProblem = null;
        if (!File.Exists(FilePath))
        {
            return new SessionMetadata();
        }

        try
        {
            using FileStream stream = File.OpenRead(FilePath);
            SessionMetadata? loaded = JsonSerializer.Deserialize(stream, MetadataJsonContext.Default.SessionMetadata)
                ?? throw new JsonException("The file is empty.");
            loaded.Folders ??= [];
            loaded.Sessions ??= [];

            if (loaded.Version > SessionMetadata.CurrentVersion)
            {
                // A newer version wrote this. Showing what is understood is fine; saving over it would
                // throw away whatever the newer version added.
                loaded.IsReadOnly = true;
                LoadProblem = $"The file is from a newer version (format {loaded.Version}) and will not be changed.";
            }

            return loaded;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            LoadProblem = $"The file could not be read ({ex.Message}).";
            if (ex is JsonException)
            {
                SetAside();
            }

            return new SessionMetadata();
        }
    }

    /// <summary>Writes the file: to a temporary file next to it, then moved over it, so a crash never leaves half a file.</summary>
    public void Save(SessionMetadata metadata)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        if (metadata.IsReadOnly)
        {
            throw new InvalidOperationException("This file came from a newer version and is not saved over.");
        }

        string? directory = System.IO.Path.GetDirectoryName(FilePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        metadata.Version = SessionMetadata.CurrentVersion;
        string temporary = FilePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (FileStream stream = new(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, metadata, MetadataJsonContext.Default.SessionMetadata);
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

    /// <summary>Moves a damaged file out of the way (kept, not deleted) so the next save does not destroy it.</summary>
    private void SetAside()
    {
        try
        {
            string aside = $"{FilePath}.damaged-{DateTime.UtcNow:yyyyMMddHHmmss}";
            File.Move(FilePath, aside, overwrite: true);
            LoadProblem += $" It was kept as {System.IO.Path.GetFileName(aside)}.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // could not move it: the next save will replace it, which is the best that can be done
        }
    }
}
