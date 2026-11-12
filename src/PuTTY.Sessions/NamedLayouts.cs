using System.Text.Json;
using System.Text.Json.Serialization;

namespace PuTTY.Sessions;

/// <summary>
/// A layout the user saved by name (Save layout in the command bar): the docking layout (the library's XML: where the panels
/// sit, how big they are, how the terminals are split into groups) and the sessions that were open in which document, so that
/// applying it can open the ones that are not open now.
/// </summary>
public sealed class NamedLayout
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    /// <summary>The docking layout, as the library writes it.</summary>
    [JsonPropertyName("layout")]
    public string Layout { get; set; } = string.Empty;

    /// <summary>The sessions that were open, each named by the id of the document it was in.</summary>
    [JsonPropertyName("sessions")]
    public List<OpenSessionRecord> Sessions { get; set; } = [];
}

/// <summary>The file of named layouts.</summary>
public sealed class NamedLayoutsFile
{
    public const int CurrentVersion = 1;

    [JsonPropertyName("version")]
    public int Version { get; set; } = CurrentVersion;

    [JsonPropertyName("layouts")]
    public List<NamedLayout> Layouts { get; set; } = [];

    /// <summary>The layout of that name (ignoring case), or null.</summary>
    public NamedLayout? Find(string name) =>
        Layouts.FirstOrDefault(l => string.Equals(l.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>The names, in alphabetical order.</summary>
    public IReadOnlyList<string> Names => [.. Layouts.Select(l => l.Name).Order(StringComparer.CurrentCultureIgnoreCase)];

    /// <summary>Saves a layout under its name, replacing the one that has it (ignoring case).</summary>
    public void Upsert(NamedLayout layout)
    {
        ArgumentNullException.ThrowIfNull(layout);
        int index = Layouts.FindIndex(l => string.Equals(l.Name, layout.Name, StringComparison.OrdinalIgnoreCase));
        if (index >= 0)
        {
            Layouts[index] = layout;
        }
        else
        {
            Layouts.Add(layout);
        }
    }

    /// <summary>Forgets a layout; false if there was none of that name.</summary>
    public bool Remove(string name) =>
        Layouts.RemoveAll(l => string.Equals(l.Name, name, StringComparison.OrdinalIgnoreCase)) > 0;
}

/// <summary>The rules for a layout's name.</summary>
public static class LayoutNames
{
    /// <summary>The built-in layout: the panels as the app first shows them, with the open terminals in tabs of one group. It cannot be saved over or deleted.</summary>
    public const string Default = "Default";

    public const int MaxLength = 60;

    /// <summary>What is wrong with a name, in words for the user, or null if it will do.</summary>
    public static string? Problem(string? name)
    {
        string trimmed = name?.Trim() ?? string.Empty;
        if (trimmed.Length == 0)
        {
            return "Give the layout a name.";
        }

        if (trimmed.Length > MaxLength)
        {
            return $"A layout name can have at most {MaxLength} characters.";
        }

        if (string.Equals(trimmed, Default, StringComparison.OrdinalIgnoreCase))
        {
            return $"\"{Default}\" is the built-in layout; choose another name.";
        }

        return null;
    }

    /// <summary>The name as it is stored: without the spaces around it.</summary>
    public static string Normalise(string name) => name.Trim();
}

[JsonSourceGenerationOptions(WriteIndented = true, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(NamedLayoutsFile))]
internal sealed partial class NamedLayoutsJsonContext : JsonSerializerContext;

/// <summary>
/// Reads and writes the named layouts as JSON next to the other workspace files. Like the other stores it never throws for
/// the file's state: a missing, damaged or newer file gives an empty list (a newer one is not overwritten by the next
/// save: <see cref="Save"/> refuses), and a write goes to a temporary file that is then moved over the old one.
/// </summary>
public sealed class NamedLayoutStore(string path)
{
    /// <summary><c>%APPDATA%\PuTTY Modern\layouts.json</c>.</summary>
    public static string DefaultPath { get; } = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PuTTY Modern", "layouts.json");

    public string FilePath { get; } = path;

    /// <summary>True when the file is from a newer version of the app: it is not read, and not written over.</summary>
    public bool IsFromNewerVersion { get; private set; }

    public NamedLayoutsFile Load()
    {
        IsFromNewerVersion = false;
        if (!File.Exists(FilePath))
        {
            return new NamedLayoutsFile();
        }

        try
        {
            using FileStream stream = File.OpenRead(FilePath);
            NamedLayoutsFile? file = JsonSerializer.Deserialize(stream, NamedLayoutsJsonContext.Default.NamedLayoutsFile);
            if (file is null)
            {
                return new NamedLayoutsFile();
            }

            if (file.Version > NamedLayoutsFile.CurrentVersion)
            {
                IsFromNewerVersion = true;
                return new NamedLayoutsFile();
            }

            file.Layouts ??= [];
            foreach (NamedLayout layout in file.Layouts)
            {
                layout.Sessions ??= [];
            }

            file.Layouts.RemoveAll(l => string.IsNullOrWhiteSpace(l.Name) || string.IsNullOrWhiteSpace(l.Layout));
            return file;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return new NamedLayoutsFile();
        }
    }

    /// <summary>Writes the file; false (nothing written) when the file on disk is from a newer version.</summary>
    public bool Save(NamedLayoutsFile file)
    {
        ArgumentNullException.ThrowIfNull(file);
        if (IsFromNewerVersion)
        {
            return false;
        }

        string? directory = System.IO.Path.GetDirectoryName(FilePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        file.Version = NamedLayoutsFile.CurrentVersion;
        string temporary = FilePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (FileStream stream = new(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, file, NamedLayoutsJsonContext.Default.NamedLayoutsFile);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporary, FilePath, overwrite: true);
            return true;
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
