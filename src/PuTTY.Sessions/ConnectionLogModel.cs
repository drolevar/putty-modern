namespace PuTTY.Sessions;

/// <summary>One line of the connection log: when it arrived, which session it belongs to, what PuTTY said, and whether it is an error.</summary>
public sealed record LogEntry(DateTime Time, string Session, string Text, bool Error);

/// <summary>
/// What the view has to do to show the model as it is now. A reset replaces everything (<see cref="Added"/> is then the whole
/// content); otherwise the oldest <see cref="Removed"/> entries go from the front and <see cref="Added"/> are appended.
/// </summary>
public sealed record LogChange(bool IsReset, int Removed, IReadOnlyList<LogEntry> Added);

/// <summary>
/// The Connection panel's lines, with no controls in it: the last <c>capacity</c> lines of every session, an optional filter
/// that shows one session's, and the changes a view has to apply as the lines arrive. The view asks for them at its own pace
/// (<see cref="TakeChange"/>), so a flood of lines costs one update, not one per line.
/// </summary>
/// <remarks>
/// Lines are kept in ring buffers, so adding one (and dropping the oldest when the buffer is full) costs the same however many
/// there are. A change that is bigger than <c>resetThreshold</c> lines is reported as a reset instead, since showing the last
/// few thousand lines at once is cheaper for a virtualized view than applying them one by one.
/// </remarks>
public sealed class ConnectionLogModel
{
    private readonly Ring all;
    private readonly int resetThreshold;
    private readonly List<string> sessions = [];
    private readonly HashSet<string> known = [];
    private readonly List<LogEntry> pendingAdded = [];
    private Ring? filtered;                 // the lines of the chosen session; null when every session is shown
    private string? filter;
    private int pendingRemoved;
    private bool pendingReset;
    private int mirrorCount;                // how many lines the view had after the last change it was given

    /// <param name="capacity">How many lines are kept; the oldest go first.</param>
    /// <param name="resetThreshold">A change of more lines than this is reported as a reset.</param>
    public ConnectionLogModel(int capacity = 5000, int resetThreshold = 64)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(resetThreshold, 1);
        all = new Ring(capacity);
        this.resetThreshold = resetThreshold;
    }

    /// <summary>The names of the sessions that have logged anything, in the order they first did.</summary>
    public IReadOnlyList<string> Sessions => sessions;

    /// <summary>The session whose lines are shown, or null for every session.</summary>
    public string? Filter => filter;

    /// <summary>How many lines are shown under the current filter.</summary>
    public int VisibleCount => (filtered ?? all).Count;

    /// <summary>The line at a position among the lines shown, oldest first.</summary>
    public LogEntry VisibleAt(int index) => (filtered ?? all)[index];

    /// <summary>True when the view has changes to apply.</summary>
    public bool HasPending => pendingReset || pendingRemoved > 0 || pendingAdded.Count > 0;

    /// <summary>Adds a line. Returns true when it is the first line of a session the log had not met.</summary>
    public bool Add(LogEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        bool newSession = known.Add(entry.Session);
        if (newSession)
        {
            sessions.Add(entry.Session);
        }

        LogEntry? dropped = all.Add(entry);
        if (filtered is null)
        {
            Note(dropped is not null, entry);
        }
        else
        {
            bool droppedShown = dropped is not null && dropped.Session == filter;
            if (droppedShown)
            {
                filtered.RemoveFirst();         // the oldest line of the buffer is the oldest of its session too
            }

            bool shown = entry.Session == filter;
            if (shown)
            {
                filtered.Add(entry);            // there is room: a line was just made for it, or none was shown
            }

            if (droppedShown || shown)
            {
                Note(droppedShown, shown ? entry : null);
            }
        }

        return newSession;
    }

    /// <summary>Shows one session's lines, or every session's with null.</summary>
    public void SetFilter(string? session)
    {
        if (session == filter)
        {
            return;
        }

        filter = session;
        if (session is null)
        {
            filtered = null;
        }
        else
        {
            filtered = new Ring(all.Capacity);
            for (int i = 0; i < all.Count; i++)
            {
                if (all[i].Session == session)
                {
                    filtered.Add(all[i]);
                }
            }
        }

        Reset();
    }

    /// <summary>Forgets every line, of every session. The sessions that were met stay known.</summary>
    public void Clear()
    {
        all.Clear();
        filtered?.Clear();
        Reset();
    }

    /// <summary>What the view has to do to show the lines as they are now. Calling it settles the changes.</summary>
    public LogChange TakeChange()
    {
        // Removals that reach past what the view had would remove lines it was never given.
        bool reset = pendingReset || pendingRemoved > mirrorCount;
        Ring shown = filtered ?? all;
        LogChange change = reset
            ? new LogChange(true, 0, shown.ToArray())
            : new LogChange(false, pendingRemoved, pendingAdded.ToArray());

        pendingAdded.Clear();
        pendingRemoved = 0;
        pendingReset = false;
        mirrorCount = shown.Count;
        return change;
    }

    private void Note(bool removedOne, LogEntry? added)
    {
        if (pendingReset)
        {
            return;
        }

        if (removedOne)
        {
            pendingRemoved++;
        }

        if (added is not null)
        {
            pendingAdded.Add(added);
        }

        if (pendingRemoved + pendingAdded.Count > resetThreshold)
        {
            Reset();
        }
    }

    private void Reset()
    {
        pendingReset = true;
        pendingAdded.Clear();
        pendingRemoved = 0;
    }

    // A fixed-size buffer that drops its oldest entry when full.
    private sealed class Ring(int capacity)
    {
        private readonly LogEntry?[] items = new LogEntry?[capacity];
        private int head;

        public int Capacity => items.Length;

        public int Count { get; private set; }

        public LogEntry this[int index] => items[(head + index) % items.Length]!;

        // Returns the entry that made room, or null when there was room.
        public LogEntry? Add(LogEntry entry)
        {
            if (Count == items.Length)
            {
                LogEntry? dropped = items[head];
                items[head] = entry;
                head = (head + 1) % items.Length;
                return dropped;
            }

            items[(head + Count) % items.Length] = entry;
            Count++;
            return null;
        }

        public void RemoveFirst()
        {
            items[head] = null;
            head = (head + 1) % items.Length;
            Count--;
        }

        public void Clear()
        {
            Array.Clear(items);
            head = 0;
            Count = 0;
        }

        public LogEntry[] ToArray()
        {
            var copy = new LogEntry[Count];
            for (int i = 0; i < Count; i++)
            {
                copy[i] = this[i];
            }

            return copy;
        }
    }
}
