using System.Text.Json.Serialization;

namespace PuTTY.Sessions;

/// <summary>
/// What the application knows about sessions that PuTTY's storage does not: the folders the user made
/// and the history of what was opened. It lives in a file of its own (see <see cref="MetadataStore"/>),
/// never in PuTTY's storage and never encoded in a session's name, so PuTTY classic keeps working with
/// the same sessions and an uninstall leaves PuTTY's data exactly as it was.
/// </summary>
/// <remarks>
/// Sessions are keyed by their PuTTY name. A session that vanished from PuTTY's storage keeps its entry
/// (it may come back; <see cref="Prune"/> is the explicit way to forget), and one this file never heard of
/// is simply at the root.
/// </remarks>
public sealed class SessionMetadata
{
    /// <summary>The newest file format this code understands.</summary>
    public const int CurrentVersion = 1;

    [JsonPropertyName("version")]
    public int Version { get; set; } = CurrentVersion;

    [JsonPropertyName("folders")]
    public List<FolderEntry> Folders { get; set; } = [];

    /// <summary>Per session, by PuTTY's name. Case-sensitive, as PuTTY's names are.</summary>
    [JsonPropertyName("sessions")]
    public Dictionary<string, SessionEntry> Sessions { get; set; } = [];

    /// <summary>True when the file was written by a newer version: it is loaded for display, never saved over.</summary>
    [JsonIgnore]
    public bool IsReadOnly { get; internal set; }

    // ----- Folders ------------------------------------------------------------------------

    public FolderEntry? FindFolder(string? id) =>
        id is null ? null : Folders.FirstOrDefault(f => f.Id == id);

    /// <summary>Makes a folder under <paramref name="parentId"/> (null: the top). Sibling names must differ, ignoring case.</summary>
    public FolderEntry CreateFolder(string name, string? parentId = null)
    {
        name = ValidFolderName(name);
        if (parentId is not null && FindFolder(parentId) is null)
        {
            throw new ArgumentException("There is no such parent folder.", nameof(parentId));
        }

        EnsureNoSibling(name, parentId, except: null);
        var folder = new FolderEntry { Id = Guid.NewGuid().ToString("N"), Name = name, ParentId = parentId };
        Folders.Add(folder);
        return folder;
    }

    public void RenameFolder(string id, string newName)
    {
        FolderEntry folder = FindFolder(id) ?? throw new ArgumentException("There is no such folder.", nameof(id));
        newName = ValidFolderName(newName);
        EnsureNoSibling(newName, folder.ParentId, except: folder);
        folder.Name = newName;
    }

    /// <summary>Moves a folder (and everything in it) under another, or to the top with null. A folder cannot go inside itself.</summary>
    public void MoveFolder(string id, string? newParentId)
    {
        FolderEntry folder = FindFolder(id) ?? throw new ArgumentException("There is no such folder.", nameof(id));
        if (newParentId is not null)
        {
            if (FindFolder(newParentId) is null)
            {
                throw new ArgumentException("There is no such parent folder.", nameof(newParentId));
            }

            for (string? up = newParentId; up is not null; up = FindFolder(up)?.ParentId)
            {
                if (up == id)
                {
                    throw new InvalidOperationException("A folder cannot be moved into itself or into one of its own folders.");
                }
            }
        }

        EnsureNoSibling(folder.Name, newParentId, except: folder);
        folder.ParentId = newParentId;
    }

    /// <summary>
    /// Deletes a folder. Its sessions and folders move up to its parent: deleting a folder never loses a
    /// session, and never touches the session in PuTTY's storage.
    /// </summary>
    public void DeleteFolder(string id)
    {
        FolderEntry folder = FindFolder(id) ?? throw new ArgumentException("There is no such folder.", nameof(id));
        foreach (FolderEntry child in Folders.Where(f => f.ParentId == id))
        {
            // A sibling of the same name may already be there; keep both rather than fail: names are a courtesy.
            child.ParentId = folder.ParentId;
        }

        foreach (SessionEntry session in Sessions.Values.Where(s => s.FolderId == id))
        {
            session.FolderId = folder.ParentId;
        }

        Folders.Remove(folder);
    }

    private static string ValidFolderName(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        name = name.Trim();
        if (name.Length == 0)
        {
            throw new ArgumentException("A folder needs a name.", nameof(name));
        }

        return name;
    }

    private void EnsureNoSibling(string name, string? parentId, FolderEntry? except)
    {
        if (Folders.Any(f => f != except && f.ParentId == parentId && f.Name.Equals(name, StringComparison.CurrentCultureIgnoreCase)))
        {
            throw new InvalidOperationException($"There is already a folder called \"{name}\" here.");
        }
    }

    // ----- Sessions -----------------------------------------------------------------------

    public SessionEntry? FindSession(string name) => Sessions.GetValueOrDefault(name);

    /// <summary>Puts a session in a folder (null: the top).</summary>
    public void MoveSession(string name, string? folderId)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        if (folderId is not null && FindFolder(folderId) is null)
        {
            throw new ArgumentException("There is no such folder.", nameof(folderId));
        }

        SessionEntry entry = EntryFor(name);
        entry.FolderId = folderId;
    }

    /// <summary>Notes that a session was opened, for the recents list.</summary>
    public void RecordOpened(string name, DateTimeOffset when)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        SessionEntry entry = EntryFor(name);
        entry.LastOpened = when;
        entry.OpenCount++;
    }

    /// <summary>The most recently opened sessions that still exist, newest first.</summary>
    public IReadOnlyList<string> Recents(IEnumerable<string> existingSessions, int max = 10)
    {
        var existing = new HashSet<string>(existingSessions, StringComparer.Ordinal);
        return
        [
            .. Sessions
                .Where(pair => pair.Value.LastOpened is not null && existing.Contains(pair.Key))
                .OrderByDescending(pair => pair.Value.LastOpened)
                .ThenBy(pair => pair.Key, StringComparer.CurrentCultureIgnoreCase)
                .Take(max)
                .Select(pair => pair.Key),
        ];
    }

    /// <summary>Forgets the entries of sessions that are no longer in PuTTY's storage. Only ever called on purpose.</summary>
    public int Prune(IEnumerable<string> existingSessions)
    {
        var existing = new HashSet<string>(existingSessions, StringComparer.Ordinal);
        string[] gone = [.. Sessions.Keys.Where(name => !existing.Contains(name))];
        foreach (string name in gone)
        {
            Sessions.Remove(name);
        }

        return gone.Length;
    }

    private SessionEntry EntryFor(string name)
    {
        if (!Sessions.TryGetValue(name, out SessionEntry? entry))
        {
            entry = new SessionEntry();
            Sessions[name] = entry;
        }

        return entry;
    }

    // ----- The tree the panel shows ---------------------------------------------------------

    /// <summary>
    /// Folders and sessions as the panel shows them: folders first, each by name, then the sessions by name.
    /// A session that is in a folder this file does not have, or that this file never heard of, is at the top.
    /// Sessions that are not in <paramref name="savedSessions"/> do not appear.
    /// </summary>
    public SessionTreeNode BuildTree(IEnumerable<string> savedSessions)
    {
        var root = new SessionTreeNode(null, string.Empty);
        var byId = new Dictionary<string, SessionTreeNode>();
        foreach (FolderEntry folder in Folders)
        {
            byId[folder.Id] = new SessionTreeNode(folder.Id, folder.Name);
        }

        foreach (FolderEntry folder in Folders)
        {
            SessionTreeNode node = byId[folder.Id];
            SessionTreeNode parent = folder.ParentId is not null && byId.TryGetValue(folder.ParentId, out SessionTreeNode? p) && !IsInCycle(folder)
                ? p
                : root;
            parent.Folders.Add(node);
        }

        foreach (string name in savedSessions)
        {
            string? folderId = Sessions.GetValueOrDefault(name)?.FolderId;
            (folderId is not null && byId.TryGetValue(folderId, out SessionTreeNode? home) ? home : root).Sessions.Add(name);
        }

        root.Sort();
        return root;
    }

    /// <summary>A hand-edited or damaged file could make folders their own ancestors; such folders go to the top.</summary>
    private bool IsInCycle(FolderEntry start)
    {
        int steps = 0;
        for (string? id = start.ParentId; id is not null; id = FindFolder(id)?.ParentId)
        {
            if (id == start.Id || ++steps > Folders.Count)
            {
                return true;
            }
        }

        return false;
    }
}

/// <summary>A folder the user made.</summary>
public sealed class FolderEntry
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    /// <summary>The folder it is inside, or null for the top.</summary>
    [JsonPropertyName("parentId")]
    public string? ParentId { get; set; }
}

/// <summary>What is known about one session.</summary>
public sealed class SessionEntry
{
    /// <summary>The folder the session is in, or null for the top.</summary>
    [JsonPropertyName("folderId")]
    public string? FolderId { get; set; }

    [JsonPropertyName("lastOpened")]
    public DateTimeOffset? LastOpened { get; set; }

    [JsonPropertyName("openCount")]
    public int OpenCount { get; set; }
}

/// <summary>A folder in the tree the panel shows, with its folders and sessions. The root has a null <see cref="Id"/>.</summary>
public sealed class SessionTreeNode(string? id, string name)
{
    public string? Id { get; } = id;

    public string Name { get; } = name;

    public List<SessionTreeNode> Folders { get; } = [];

    public List<string> Sessions { get; } = [];

    internal void Sort()
    {
        Folders.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase));
        Sessions.Sort(StringComparer.CurrentCultureIgnoreCase);
        foreach (SessionTreeNode folder in Folders)
        {
            folder.Sort();
        }
    }
}
