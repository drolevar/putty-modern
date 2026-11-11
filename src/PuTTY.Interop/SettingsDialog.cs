using System.Diagnostics;
using System.Runtime.InteropServices;

namespace PuTTY.Interop;

/// <summary>The kinds of control PuTTY's configuration box is made of.</summary>
public enum SettingsControlType
{
    Text = 0,
    EditBox = 1,
    Radio = 2,
    CheckBox = 3,
    Button = 4,
    ListBox = 5,

    /// <summary>Not a control: the column layout of the controls that follow it in the same group.</summary>
    Columns = 6,
    FileSelect = 7,
    FontSelect = 8,
}

[Flags]
public enum SettingsControlFlags
{
    None = 0,
    Password = 0x001,
    HasList = 0x002,
    DragList = 0x004,
    HScroll = 0x008,
    Default = 0x010,
    Cancel = 0x020,
    JustButton = 0x040,
    ForWriting = 0x080,
    Wrap = 0x100,
}

public enum SettingsFileFilter
{
    AllFiles = 0,
    KeyFiles = 1,
    DynamicLibraries = 2,
    Sounds = 3,
}

/// <summary>One control of the settings dialog, as PuTTY describes it. The state (text, check, list) is read with the dialog's getters.</summary>
public sealed class SettingsControl
{
    public int Id { get; init; }

    public SettingsControlType Type { get; init; }

    public SettingsControlFlags Flags { get; init; }

    /// <summary>The index of the <see cref="SettingsGroup"/> the control belongs to.</summary>
    public int Group { get; init; }

    public int ColumnStart { get; init; }

    public int ColumnSpan { get; init; }

    /// <summary>The share of the width for an edit or list box; 100 puts its label on a line of its own.</summary>
    public int PercentWidth { get; init; }

    /// <summary>Lines of a list box; 0 makes it a drop-down list.</summary>
    public int ListHeight { get; init; }

    /// <summary>0: one selection; 1: individual items; 2: ranges.</summary>
    public int MultiSelect { get; init; }

    /// <summary>Radio: buttons per row. List box: tab-separated columns. Columns: how many columns.</summary>
    public int Columns { get; init; }

    public IReadOnlyList<int> Percentages { get; init; } = [];

    /// <summary>Radio: the labels of the buttons.</summary>
    public IReadOnlyList<string> Buttons { get; init; } = [];

    /// <summary>Radio: a keyboard shortcut letter per button, a space for none.</summary>
    public string Shortcuts { get; init; } = string.Empty;

    /// <summary>The keyboard shortcut letter of the control, or '\0'.</summary>
    public char Shortcut { get; init; }

    /// <summary>The label, or null. For a check box or a push button, its own text.</summary>
    public string? Label { get; init; }

    /// <summary>A file selector's dialog title.</summary>
    public string? Title { get; init; }

    public SettingsFileFilter Filter { get; init; }

    /// <summary>The id of the control this one should line up with, or -1.</summary>
    public int AlignNextTo { get; init; } = -1;

    public bool IsDropDown => Type == SettingsControlType.ListBox && ListHeight == 0;

    public override string ToString() => $"#{Id} {Type} \"{Label}\"";
}

/// <summary>
/// A group of controls: a titled box on a page, or the heading of the page itself (when
/// <see cref="Name"/> is empty). <see cref="Path"/> names the page, such as <c>Connection/SSH/Auth</c>.
/// </summary>
public sealed class SettingsGroup
{
    public int Index { get; init; }

    public string Path { get; init; } = string.Empty;

    public string Name { get; init; } = string.Empty;

    public string Title { get; init; } = string.Empty;

    public List<SettingsControl> Controls { get; } = [];

    public bool IsPageHeading => Name.Length == 0;
}

/// <summary>One entry of a list box or combo box.</summary>
public readonly record struct SettingsListEntry(string Text, int Id, bool Selected);

/// <summary>The value of a font selector.</summary>
public readonly record struct SettingsFont(string Name, int Height, bool Bold, int Charset);

/// <summary>
/// PuTTY's configuration box with the drawing left to the host: PuTTY's own description of the pages and
/// controls, and its own handlers, which keep the settings and the controls consistent (choosing a protocol
/// changes the port, Load fills every page from a saved session). The host draws what <see cref="Groups"/>
/// describes, shows the state it reads, reports what the user does, and redraws what
/// <see cref="ControlChanged"/> names. Everything runs on the engine thread.
/// </summary>
/// <remarks>
/// Open, then read <see cref="Groups"/>; every control already has its first value. Pressing the Open button
/// (a push button labelled "Open") makes PuTTY raise <see cref="Ended"/> with true once the settings name a
/// host: <see cref="CreateSession"/> then gives a session with the settings as edited. The Session page's
/// Save and Delete buttons write to and delete from PuTTY's own storage, as in PuTTY itself.
/// </remarks>
public sealed unsafe class SettingsDialog : IDisposable
{
    private readonly GCHandle self;
    private readonly List<SettingsGroup> groups = [];
    private readonly List<SettingsControl> controls = [];
    private readonly Dictionary<int, SettingsControl> byId = [];
    private nint handle;
    private bool disposed;

    private SettingsDialog()
    {
        self = GCHandle.Alloc(this);
    }

    /// <summary>
    /// Opens the dialog on the built-in defaults, or on the user's saved "Default Settings" with
    /// <paramref name="useSavedDefaults"/>. The first values of all controls are in place when this returns.
    /// </summary>
    public static SettingsDialog Open(bool useSavedDefaults = false)
    {
        var dialog = new SettingsDialog();
        try
        {
            dialog.Start(useSavedDefaults);
            return dialog;
        }
        catch
        {
            dialog.Dispose();
            throw;
        }
    }

    /// <summary>The groups of controls in PuTTY's order; those with the same <see cref="SettingsGroup.Path"/> make up a page.</summary>
    public IReadOnlyList<SettingsGroup> Groups => groups;

    /// <summary>Every control, in PuTTY's order.</summary>
    public IReadOnlyList<SettingsControl> Controls => controls;

    /// <summary>The control with this id (the position PuTTY gave it; a few kinds are left out, so ids have gaps).</summary>
    public SettingsControl Control(int id) => byId[id];

    /// <summary>The value or the list of the control changed since the host last read it: read it again. (The first values are in place when <see cref="Open"/> returns, so there is nothing to subscribe to for them.)</summary>
    public event Action<int>? ControlChanged;

    /// <summary>A control's label was replaced (a button that turns from Add to Replace).</summary>
    public event Action<int, string>? LabelChanged;

    /// <summary>True when a burst of changes begins and false when it ends: a host may postpone redrawing.</summary>
    public event Action<bool>? Updating;

    /// <summary>Something for the user to read; the flag is true for an error.</summary>
    public event Action<string, bool>? MessageRaised;

    public event Action? Beeped;

    /// <summary>PuTTY wants the focus on a control.</summary>
    public event Action<int>? FocusRequested;

    /// <summary>A colour chooser is wanted for the control, starting at the colour; answer with <see cref="ColourChosen"/>.</summary>
    public event Action<int, int, int, int>? ColourRequested;

    /// <summary>The dialog is over: true to go ahead (Open), false when cancelled.</summary>
    public event Action<bool>? Ended;

    private void Start(bool useSavedDefaults)
    {
        NativeConfigEvents events = default;
        events.Group = &OnGroup;
        events.Control = &OnControl;
        events.Changed = &OnChanged;
        events.LabelChanged = &OnLabelChanged;
        events.Update = &OnUpdate;
        events.Message = &OnMessage;
        events.Beep = &OnBeep;
        events.Focus = &OnFocus;
        events.ColourRequest = &OnColourRequest;
        events.End = &OnEnd;

        PuttyEngine.Check(
            NativeMethods.ConfigOpen(useSavedDefaults ? 1u : 0u, &events, GCHandle.ToIntPtr(self), out handle), nameof(Open));
        PuttyEngine.Check(NativeMethods.ConfigDescribe(handle), nameof(Open));
        PuttyEngine.Check(NativeMethods.ConfigRefresh(handle), nameof(Open));
    }

    // ----- What the user does -----------------------------------------------------------------

    /// <summary>Tells the dialog which page is showing, such as <c>Session</c> (PuTTY asks whether a control is visible in one corner case).</summary>
    public void SetPage(string? path) => Check(NativeMethods.ConfigSetPage(Handle, path), nameof(SetPage));

    public void Focused(int id) => Check(NativeMethods.ConfigUserFocus(Handle, id), nameof(Focused));

    /// <summary>The user typed in an edit box (or chose a combo box entry as text).</summary>
    public void SetText(int id, string text) => Check(NativeMethods.ConfigUserSetText(Handle, id, text), nameof(SetText));

    public void SetChecked(int id, bool isChecked) => Check(NativeMethods.ConfigUserSetChecked(Handle, id, isChecked ? 1 : 0), nameof(SetChecked));

    public void SetRadio(int id, int button) => Check(NativeMethods.ConfigUserSetRadio(Handle, id, button), nameof(SetRadio));

    /// <summary>The user selected (or, in a multi-selection list, unselected) the entry at <paramref name="index"/>.</summary>
    public void Select(int id, int index, bool selected = true) => Check(NativeMethods.ConfigUserSelect(Handle, id, index, selected ? 1 : 0), nameof(Select));

    /// <summary>The user pushed a button, or double-clicked a list entry.</summary>
    public void Act(int id) => Check(NativeMethods.ConfigUserAction(Handle, id), nameof(Act));

    /// <summary>The user dragged a list entry (of a control with <see cref="SettingsControlFlags.DragList"/>) to a new position.</summary>
    public void Move(int id, int from, int to) => Check(NativeMethods.ConfigUserMove(Handle, id, from, to), nameof(Move));

    public void SetFile(int id, string path) => Check(NativeMethods.ConfigUserSetFile(Handle, id, path), nameof(SetFile));

    public void SetFont(int id, SettingsFont font) =>
        Check(NativeMethods.ConfigUserSetFont(Handle, id, font.Name, font.Height, font.Bold ? 1 : 0, font.Charset), nameof(SetFont));

    /// <summary>The answer to <see cref="ColourRequested"/>: the chosen colour, or null if the chooser was cancelled.</summary>
    public void ColourChosen(int id, (int R, int G, int B)? colour) =>
        Check(NativeMethods.ConfigColourResult(Handle, id, colour is null ? 0 : 1, colour?.R ?? 0, colour?.G ?? 0, colour?.B ?? 0), nameof(ColourChosen));

    /// <summary>
    /// Loads a saved session (or <c>Default Settings</c>) into every page by doing what the user does: pick it in the
    /// Session page's list and press Load. False if the list has no such entry. PuTTY's own handler does the loading.
    /// </summary>
    public bool LoadSavedSession(string name)
    {
        SettingsControl[] session = [.. groups.Where(g => g.Path == "Session").SelectMany(g => g.Controls)];
        SettingsControl? list = session.FirstOrDefault(c => c.Type == SettingsControlType.ListBox && !c.IsDropDown);
        SettingsControl? load = session.FirstOrDefault(c => c.Type == SettingsControlType.Button && c.Label == "Load");
        if (list is null || load is null)
        {
            return false;
        }

        int at = GetList(list.Id).ToList().FindIndex(e => e.Text == name);
        if (at < 0)
        {
            return false;
        }

        Select(list.Id, at);
        Act(load.Id);
        return true;
    }

    // ----- What the controls hold ---------------------------------------------------------------

    /// <summary>The text of an edit box or a line of text.</summary>
    public string GetText(int id) => ReadString((nint h, byte* buffer, int capacity) => NativeMethods.ConfigGetText(h, id, buffer, capacity));

    public bool GetChecked(int id) => Check(NativeMethods.ConfigGetChecked(Handle, id), nameof(GetChecked)) != 0;

    /// <summary>The selected radio button, or -1.</summary>
    public int GetRadio(int id) => Check(NativeMethods.ConfigGetRadio(Handle, id), nameof(GetRadio));

    public IReadOnlyList<SettingsListEntry> GetList(int id)
    {
        int count = Check(NativeMethods.ConfigListCount(Handle, id), nameof(GetList));
        var entries = new SettingsListEntry[count];
        for (int i = 0; i < count; i++)
        {
            int index = i;
            int[] extra = new int[2];
            string text = ReadString((nint h, byte* buffer, int capacity) =>
            {
                int itemId;
                int selected;
                int length = NativeMethods.ConfigListItem(h, id, index, buffer, capacity, &itemId, &selected);
                extra[0] = itemId;
                extra[1] = selected;
                return length;
            });
            entries[i] = new SettingsListEntry(text, extra[0], extra[1] != 0);
        }

        return entries;
    }

    public string GetFile(int id) => ReadString((nint h, byte* buffer, int capacity) => NativeMethods.ConfigGetFile(h, id, buffer, capacity));

    public SettingsFont GetFont(int id)
    {
        int[] extra = new int[3];
        string name = ReadString((nint h, byte* buffer, int capacity) =>
        {
            int height;
            int bold;
            int charset;
            int length = NativeMethods.ConfigGetFont(h, id, buffer, capacity, &height, &bold, &charset);
            extra[0] = height;
            extra[1] = bold;
            extra[2] = charset;
            return length;
        });
        return new SettingsFont(name, extra[0], extra[1] != 0, extra[2]);
    }

    private delegate int StringReader(nint handle, byte* buffer, int capacity);

    private string ReadString(StringReader read)
    {
        nint h = Handle;
        int length = read(h, null, 0);
        if (length < 0)
        {
            PuttyEngine.Check(length, nameof(ReadString));
        }

        byte[] bytes = new byte[length + 1];
        fixed (byte* buffer = bytes)
        {
            PuttyEngine.Check(Math.Min(0, read(h, buffer, bytes.Length)), nameof(ReadString));
        }

        return System.Text.Encoding.UTF8.GetString(bytes, 0, length);
    }

    // ----- Going ahead ----------------------------------------------------------------------------

    /// <summary>
    /// A session with the settings as they are now, to subscribe to and <see cref="PuttySession.Start"/>:
    /// what the Open button means. Throws from <c>Start</c> if the settings name no host.
    /// </summary>
    public PuttySession CreateSession()
    {
        nint h = Handle;
        return PuttySession.FromOpener("the settings dialog", (NativeSessionEvents* events, nint user, out nint session) =>
            NativeMethods.ConfigStartSession(h, events, user, out session));
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        if (handle != 0)
        {
            // Safe from inside one of our own callbacks: the engine defers the teardown until PuTTY's handler has returned.
            NativeMethods.ConfigClose(handle);
            handle = 0;
        }

        if (self.IsAllocated)
        {
            self.Free();
        }
    }

    private nint Handle
    {
        get
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            return handle;
        }
    }

    private static int Check(int result, string operation)
    {
        if (result < NativeMethods.Ok)
        {
            PuttyEngine.Check(result, operation);
        }

        return result;
    }

    // ----- Native callbacks -------------------------------------------------------------------

    private static SettingsDialog? From(nint user) =>
        user == 0 ? null : GCHandle.FromIntPtr(user).Target as SettingsDialog;

    private static string Utf8(byte* text) => text == null ? string.Empty : Marshal.PtrToStringUTF8((nint)text) ?? string.Empty;

    [UnmanagedCallersOnly]
    private static void OnGroup(nint user, int index, byte* path, byte* name, byte* title)
    {
        try
        {
            if (From(user) is { } dialog)
            {
                dialog.groups.Add(new SettingsGroup { Index = index, Path = Utf8(path), Name = Utf8(name), Title = Utf8(title) });
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"SettingsDialog group callback threw: {ex}");
        }
    }

    [UnmanagedCallersOnly]
    private static void OnControl(nint user, NativeCtrlInfo* info)
    {
        try
        {
            if (From(user) is not { } dialog)
            {
                return;
            }

            var percentages = new int[info->Percentages == null ? 0 : info->NColumns];
            for (int i = 0; i < percentages.Length; i++)
            {
                percentages[i] = info->Percentages[i];
            }

            var buttons = new string[info->Buttons == null ? 0 : info->NButtons];
            for (int i = 0; i < buttons.Length; i++)
            {
                buttons[i] = Utf8(info->Buttons[i]);
            }

            var control = new SettingsControl
            {
                Id = info->Id,
                Type = (SettingsControlType)info->Type,
                Flags = (SettingsControlFlags)info->Flags,
                Group = info->Group,
                ColumnStart = info->ColumnStart,
                ColumnSpan = info->ColumnSpan,
                PercentWidth = info->PercentWidth,
                ListHeight = info->ListHeight,
                MultiSelect = info->MultiSel,
                Columns = info->NColumns,
                Percentages = percentages,
                Buttons = buttons,
                Shortcuts = Utf8(info->Shortcuts),
                Shortcut = (char)info->Shortcut,
                Label = info->Label == null ? null : Utf8(info->Label),
                Title = info->Title == null ? null : Utf8(info->Title),
                Filter = (SettingsFileFilter)info->Filter,
                AlignNextTo = info->AlignNextTo,
            };

            dialog.controls.Add(control);
            dialog.byId[control.Id] = control;
            dialog.groups[info->Group].Controls.Add(control);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"SettingsDialog control callback threw: {ex}");
        }
    }

    [UnmanagedCallersOnly]
    private static void OnChanged(nint user, int id) => Raise(user, dialog => dialog.ControlChanged?.Invoke(id));

    [UnmanagedCallersOnly]
    private static void OnLabelChanged(nint user, int id, byte* text)
    {
        string label = Utf8(text);
        Raise(user, dialog => dialog.LabelChanged?.Invoke(id, label));
    }

    [UnmanagedCallersOnly]
    private static void OnUpdate(nint user, int begin) => Raise(user, dialog => dialog.Updating?.Invoke(begin != 0));

    [UnmanagedCallersOnly]
    private static void OnMessage(nint user, byte* text, int isError)
    {
        string message = Utf8(text);
        Raise(user, dialog => dialog.MessageRaised?.Invoke(message, isError != 0));
    }

    [UnmanagedCallersOnly]
    private static void OnBeep(nint user) => Raise(user, dialog => dialog.Beeped?.Invoke());

    [UnmanagedCallersOnly]
    private static void OnFocus(nint user, int id) => Raise(user, dialog => dialog.FocusRequested?.Invoke(id));

    [UnmanagedCallersOnly]
    private static void OnColourRequest(nint user, int id, int r, int g, int b) =>
        Raise(user, dialog => dialog.ColourRequested?.Invoke(id, r, g, b));

    [UnmanagedCallersOnly]
    private static void OnEnd(nint user, int value) => Raise(user, dialog => dialog.Ended?.Invoke(value != 0));

    /// <summary>Runs a host handler for a native callback; an exception must never go back into PuTTY.</summary>
    private static void Raise(nint user, Action<SettingsDialog> raise)
    {
        try
        {
            if (From(user) is { } dialog)
            {
                raise(dialog);
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"SettingsDialog handler threw: {ex}");
        }
    }
}
