using System.Diagnostics;
using System.Runtime.InteropServices;

namespace PuTTY.Interop;

/// <summary>The protocols the engine can open.</summary>
public enum SessionProtocol
{
    Raw = 0,
    Telnet = 1,
    Rlogin = 2,
    Ssh = 3,
}

/// <summary>The terminal's title changed (an escape sequence from the server).</summary>
public sealed class SessionTitleEventArgs(string title) : EventArgs
{
    public string Title { get; } = title;
}

/// <summary>The cursor is in another cell: its top-left corner in pixels of the view and the size of a cell.</summary>
public sealed class SessionCursorEventArgs(int x, int y, int width, int height) : EventArgs
{
    public int X { get; } = x;

    public int Y { get; } = y;

    public int Width { get; } = width;

    public int Height { get; } = height;
}

/// <summary>The scrollback changed: <see cref="Total"/> lines in all, <see cref="Start"/> the first one shown, <see cref="Page"/> lines on screen.</summary>
public sealed class SessionScrollbarEventArgs(int total, int start, int page) : EventArgs
{
    public int Total { get; } = total;

    public int Start { get; } = start;

    public int Page { get; } = page;
}

/// <summary>What a bell should do to draw attention to the window (the session's "Taskbar/caption indication on bell" setting).</summary>
public enum BellIndicator
{
    /// <summary>Nothing.</summary>
    Disabled = 0,

    /// <summary>Flash until the window is used.</summary>
    Flash = 1,

    /// <summary>Flash a few times, then stay lit.</summary>
    Steady = 2,
}

/// <summary>The terminal rang its bell. The sound (and a visual bell) have been dealt with by the engine.</summary>
public sealed class SessionBellEventArgs(BellIndicator indicator) : EventArgs
{
    public BellIndicator Indicator { get; } = indicator;
}

/// <summary>The terminal selected text that should go onto the system clipboard.</summary>
public sealed class SessionClipboardEventArgs(string text) : EventArgs
{
    public string Text { get; } = text;
}

/// <summary>What a pointer did over the terminal view.</summary>
public enum MouseAction
{
    Down = 0,
    Up = 1,
    Move = 2,
}

public enum MouseButton
{
    Left = 0,
    Middle = 1,
    Right = 2,
}

/// <summary>Modifier keys held during a pointer event.</summary>
[Flags]
public enum TerminalModifiers
{
    None = 0,
    Shift = 1,
    Control = 2,
    Alt = 4,
}

/// <summary>How <see cref="PuttySession.Scroll"/> moves the scrollback.</summary>
public enum ScrollMode
{
    /// <summary>By a number of lines from where it is now; negative goes up.</summary>
    Lines = 0,

    /// <summary>To a line counted from the top of the scrollback.</summary>
    Absolute = 1,

    Top = 2,

    Bottom = 3,
}

/// <summary>The terminal's size in character cells and the size of one cell in pixels (0 until a view is attached).</summary>
public readonly record struct TerminalGeometry(int Columns, int Rows, int CellWidth, int CellHeight);

/// <summary>A setting that is either decided by the protocol or forced on or off.</summary>
public enum LocalMode
{
    Auto = 0,
    On = 1,
    Off = 2,
}

/// <summary>Bytes received from the remote side.</summary>
public sealed class SessionOutputEventArgs(byte[] data, bool isErrorStream) : EventArgs
{
    public byte[] Data { get; } = data;

    /// <summary>True if the bytes belong to the error stream rather than normal output.</summary>
    public bool IsErrorStream { get; } = isErrorStream;
}

/// <summary>One line of PuTTY's event log, such as "Connecting to 127.0.0.1 port 22".</summary>
public sealed class SessionLogEventArgs(string text) : EventArgs
{
    public string Text { get; } = text;
}

/// <summary>A problem with the session.</summary>
public sealed class SessionErrorEventArgs(string message, bool isFatal) : EventArgs
{
    public string Message { get; } = message;

    /// <summary>True if the connection cannot continue.</summary>
    public bool IsFatal { get; } = isFatal;
}

/// <summary>
/// One connection run by PuTTY's core. Create it, subscribe to the events, then call
/// <see cref="Start"/>: subscribing first means nothing is missed, including the first lines
/// of the event log. Events are raised on the engine thread.
/// </summary>
/// <remarks>
/// <para>
/// Sessions belong to the engine thread: start, send and dispose them there. This is a plain
/// <see cref="IDisposable"/> and not a <c>SafeHandle</c> on purpose. A <c>SafeHandle</c> is
/// released by the finalizer thread, and the engine refuses calls from any thread but its own.
/// A session that is never disposed is closed when the engine shuts down.
/// </para>
/// <para>
/// A handler may dispose the session it is handling an event for; the engine defers the
/// teardown until PuTTY has unwound and stops delivering events at once.
/// </para>
/// </remarks>
public sealed unsafe partial class PuttySession : IDisposable
{
    private readonly string host;
    private readonly int port;
    private readonly SessionProtocol protocol;
    private readonly PuttySessionOptions? options;
    private readonly string? savedName;     // set for a session opened from PuTTY's storage
    private readonly NativeOpener? opener;  // set for a session whose settings come from somewhere else (the settings dialog)

    private nint handle;

    /// <summary>The engine's handle, for the settings dialog opened on this session. Throws once the session is disposed.</summary>
    internal nint NativeHandle => handle != 0 ? handle : throw new ObjectDisposedException(nameof(PuttySession));
    private GCHandle self;          // lets native code find this object again
    private bool started;
    private bool disposed;

    public PuttySession(string host, int port, SessionProtocol protocol, PuttySessionOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(host);
        ArgumentOutOfRangeException.ThrowIfLessThan(port, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(port, 65535);
        this.host = host;
        this.port = port;
        this.protocol = protocol;
        this.options = options;
    }

    /// <summary>How a session that is not opened by host and port asks the engine to open it.</summary>
    internal unsafe delegate int NativeOpener(NativeSessionEvents* events, nint user, out nint session);

    private PuttySession(string description, NativeOpener opener)
    {
        host = description;             // only for messages
        this.opener = opener;
    }

    /// <summary>
    /// Where this session connects, as its settings say now (host, or the serial line; port; protocol; user name; key
    /// file), for opening the same place again when it was not opened from a saved name.
    /// </summary>
    public unsafe SessionEndpoint GetEndpoint()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        NativeSessionEndpoint native = default;
        native.Size = sizeof(NativeSessionEndpoint);
        PuttyEngine.Check(NativeMethods.SessionGetEndpoint(handle, &native), nameof(GetEndpoint));
        return new SessionEndpoint(
            Marshal.PtrToStringUTF8((nint)native.Host) ?? string.Empty,
            native.Port,
            (SavedSessionProtocol)native.Protocol,
            Marshal.PtrToStringUTF8((nint)native.UserName) is { Length: > 0 } user ? user : null,
            Marshal.PtrToStringUTF8((nint)native.KeyFile) is { Length: > 0 } key ? key : null);
    }

    /// <summary>
    /// A new session with a copy of this one's settings (host, port, protocol, user name, everything, including what
    /// Change Settings altered), for Reconnect and Duplicate. Not started yet. This session must still be open when the
    /// new one is started: it is where the settings are copied from.
    /// </summary>
    public PuttySession Clone()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        nint source = handle;
        if (source == 0)
        {
            throw new InvalidOperationException("The session has not been started.");
        }

        return FromOpener(description: "the open session", (NativeSessionEvents* events, nint user, out nint session) =>
            NativeMethods.SessionOpenLike(source, events, user, out session));
    }

    /// <summary>A session opened by a native call of the caller's (the settings dialog's Open); not started yet.</summary>
    internal static PuttySession FromOpener(string description, NativeOpener opener) => new(description, opener);

    private PuttySession(string savedName, PuttySessionOptions? overrides)
    {
        this.savedName = savedName;
        host = savedName;               // only for messages: the real host comes from the saved settings
        options = overrides;
    }

    /// <summary>
    /// A session saved in PuTTY's own storage (the registry, shared with PuTTY itself), to be opened with
    /// all of its settings: host, port and protocol are the saved ones. <paramref name="overrides"/>
    /// replaces the user name, key file, command or Pageant setting on top. Not started yet: subscribe,
    /// then <see cref="Start"/>, which throws if there is no such session. The storage is only read.
    /// </summary>
    public static PuttySession OpenSaved(string name, PuttySessionOptions? overrides = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        return new PuttySession(name, overrides);
    }

    public event EventHandler<SessionOutputEventArgs>? Output;

    public event EventHandler<SessionLogEventArgs>? Log;

    public event EventHandler<SessionErrorEventArgs>? Error;

    /// <summary>The connection has ended. Raised once; the session still has to be disposed.</summary>
    public event EventHandler? Closed;

    /// <summary>The terminal's title changed. Raised on the engine thread.</summary>
    public event EventHandler<SessionTitleEventArgs>? TitleChanged;

    /// <summary>Text was selected and belongs on the system clipboard. The host owns the clipboard. Raised on the engine thread.</summary>
    public event EventHandler<SessionClipboardEventArgs>? ClipboardWrite;

    /// <summary>
    /// The terminal wants to paste (a paste key or mouse button). Read the clipboard, which may be
    /// asynchronous, and answer with <see cref="Paste"/>. Raised on the engine thread.
    /// </summary>
    public event EventHandler? ClipboardPasteRequested;

    /// <summary>
    /// The terminal rang its bell. The engine has already played the sound the session's settings ask for (or the
    /// emulator has drawn its visual bell); this is for the host to draw attention to its window. Raised on the engine thread.
    /// </summary>
    public event EventHandler<SessionBellEventArgs>? Bell;

    /// <summary>The scrollback changed. Raised on the engine thread.</summary>
    public event EventHandler<SessionScrollbarEventArgs>? ScrollbarChanged;

    /// <summary>The terminal's cursor moved to another cell. Raised on the engine thread.</summary>
    public event EventHandler<SessionCursorEventArgs>? CursorMoved;

    /// <summary>The most recent error message, or null if none has been reported.</summary>
    public string? LastError { get; private set; }

    /// <summary>
    /// Starts connecting. This returns at once and the connection completes asynchronously:
    /// watch the events. Throws if the engine rejects it immediately (for example a protocol
    /// that is not built in); the session cannot be used afterwards.
    /// </summary>
    public void Start()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (started)
        {
            throw new InvalidOperationException("The session has already been started.");
        }

        started = true;
        self = GCHandle.Alloc(this);

        NativeSessionEvents events = default;
        events.Output = &OnOutput;
        events.Log = &OnLog;
        events.Error = &OnError;
        events.Closed = &OnClosed;
        events.Prompt = &OnPrompt;
        events.Confirm = &OnConfirm;

        // The settings are UTF-8 copies that only have to live until the engine has read them.
        NativeSessionOptions native_options = default;
        native_options.Size = (uint)sizeof(NativeSessionOptions);
        native_options.Flags = options?.UseSavedDefaults == true ? 1u : 0u;
        native_options.TryAgent = options?.TryAgent is { } tryAgent ? (tryAgent ? 1 : 0) : -1;
        native_options.UserName = (byte*)Marshal.StringToCoTaskMemUTF8(options?.UserName);
        native_options.KeyFile = (byte*)Marshal.StringToCoTaskMemUTF8(options?.KeyFile);
        native_options.Command = (byte*)Marshal.StringToCoTaskMemUTF8(options?.Command);
        int result;
        nint native;
        try
        {
            result = opener is not null
                ? opener(&events, GCHandle.ToIntPtr(self), out native)
                : savedName is not null
                    ? NativeMethods.SessionOpenSaved(savedName, &native_options, &events, GCHandle.ToIntPtr(self), out native)
                    : NativeMethods.SessionOpenEx(
                        host, port, (int)protocol, &native_options, &events, GCHandle.ToIntPtr(self), out native);
        }
        finally
        {
            Marshal.FreeCoTaskMem((nint)native_options.UserName);
            Marshal.FreeCoTaskMem((nint)native_options.KeyFile);
            Marshal.FreeCoTaskMem((nint)native_options.Command);
        }
        if (result < NativeMethods.Ok)
        {
            self.Free();
            disposed = true;
            string detail = LastError is { } message ? $" ({message})" : string.Empty;
            throw new InvalidOperationException(opener is not null
                ? $"Could not open a session from {host}{detail}. Does it name a host?"
                : savedName is not null
                    ? $"Could not open the saved session \"{savedName}\"{detail}."
                    : $"Could not open a {protocol} session to {host}:{port}{detail}.");
        }

        handle = native;

        NativeTerminalEvents terminal = default;
        terminal.Title = &OnTitle;
        terminal.Bell = &OnBell;
        terminal.Scrollbar = &OnScrollbar;
        terminal.ClipboardWrite = &OnClipboardWrite;
        terminal.ClipboardRequestPaste = &OnClipboardRequestPaste;
        terminal.Cursor = &OnCursor;
        PuttyEngine.Check(NativeMethods.SessionSetTerminalEvents(handle, &terminal), nameof(Start));
    }

    /// <summary>
    /// Gives the session a terminal emulator of the given size. From then on the remote side's
    /// output goes to the emulator instead of the <see cref="Output"/> event, and keyboard input
    /// (see <see cref="SetKeyboardTarget"/>) reaches the connection through PuTTY's key
    /// translation. There is no renderer yet. Call it after <see cref="Start"/>.
    /// </summary>
    public void EnableTerminal(int columns, int rows)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (!started)
        {
            throw new InvalidOperationException("The session has not been started.");
        }

        PuttyEngine.Check(NativeMethods.SessionEnableTerminal(handle, columns, rows), nameof(EnableTerminal));
    }

    /// <summary>
    /// Gives the terminal a drawing surface that is a bitmap in memory: for tests and headless
    /// rendering. The terminal is created if needed and sized to the whole cells that fit.
    /// </summary>
    public void AttachOffscreenView(int widthPixels, int heightPixels, string fontFamily, float fontSizePixels)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        PuttyEngine.Check(
            NativeMethods.SessionAttachOffscreenView(handle, widthPixels, heightPixels, fontFamily, fontSizePixels),
            nameof(AttachOffscreenView));
    }

    /// <summary>
    /// Gives the terminal a drawing surface that is plugged into a XAML <c>SwapChainPanel</c>:
    /// <paramref name="panelUnknown"/> is the panel's <c>IUnknown</c> (its ABI pointer). Sizes are
    /// physical pixels.
    /// </summary>
    public void AttachPanelView(nint panelUnknown, int widthPixels, int heightPixels, string fontFamily, float fontSizePixels)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        PuttyEngine.Check(
            NativeMethods.SessionAttachPanelView(handle, panelUnknown, widthPixels, heightPixels, fontFamily, fontSizePixels),
            nameof(AttachPanelView));
    }

    /// <summary>The surface changed size (or the DPI scale did: pass the new physical size). The terminal is refitted.</summary>
    public void ResizeView(int widthPixels, int heightPixels)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        PuttyEngine.Check(NativeMethods.SessionResizeView(handle, widthPixels, heightPixels), nameof(ResizeView));
    }

    /// <summary>
    /// The font the session's settings ask for (Window > Appearance): the name, the height (positive: points, negative:
    /// pixels) and whether it is bold. The renderer takes its font from the host (<see cref="SetFont"/>); this says what to choose.
    /// </summary>
    public unsafe SettingsFont GetFont()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        int height;
        int bold;
        int length = NativeMethods.SessionGetFont(handle, null, 0, &height, &bold);
        PuttyEngine.Check(Math.Min(0, length), nameof(GetFont));
        byte[] bytes = new byte[length + 1];
        fixed (byte* buffer = bytes)
        {
            PuttyEngine.Check(Math.Min(0, NativeMethods.SessionGetFont(handle, buffer, bytes.Length, &height, &bold)), nameof(GetFont));
        }

        return new SettingsFont(System.Text.Encoding.UTF8.GetString(bytes, 0, length), height, bold != 0, 0);
    }

    /// <summary>
    /// The text the terminal shows now (what is drawn, the scrollback position as it is), rows separated by newlines, trailing
    /// blanks and empty rows at the end left out. For what cannot read pixels: UI Automation, a screen reader, a check.
    /// Empty before the first paint. Needs a view (<see cref="AttachPanelView"/> or <see cref="AttachOffscreenView"/>).
    /// </summary>
    public unsafe string GetScreenText()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        int length = NativeMethods.SessionGetScreenText(handle, null, 0, null, null);
        PuttyEngine.Check(Math.Min(0, length), nameof(GetScreenText));
        if (length == 0)
        {
            return string.Empty;
        }

        char[] buffer = new char[length + 1];
        fixed (char* text = buffer)
        {
            PuttyEngine.Check(Math.Min(0, NativeMethods.SessionGetScreenText(handle, text, buffer.Length, null, null)), nameof(GetScreenText));
        }

        return new string(buffer, 0, length);
    }

    /// <summary>
    /// Ends the connection and keeps the session: its terminal, with the screen and the scrollback, stays and can be
    /// <see cref="Clone"/>d for a reconnect. <see cref="Closed"/> is raised (once) and nothing more can be sent. Does nothing
    /// when the connection is over already. Called from inside one of the session's own events it takes effect a moment later.
    /// </summary>
    public void Disconnect()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        PuttyEngine.Check(NativeMethods.SessionDisconnect(handle), nameof(Disconnect));
    }

    /// <summary>A different font or size. The terminal is refitted to the surface.</summary>
    public void SetFont(string fontFamily, float fontSizePixels)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        PuttyEngine.Check(NativeMethods.SessionSetFont(handle, fontFamily, fontSizePixels), nameof(SetFont));
    }

    /// <summary>
    /// A pointer event over the terminal view, in view pixels (physical, as for <see cref="AttachPanelView"/>).
    /// <paramref name="timestampMilliseconds"/> is the event's time on any steady clock; it is how a
    /// double or triple click is told from slow clicks. Move events with a button down are drags.
    /// </summary>
    public void Mouse(MouseAction action, MouseButton button, int x, int y, TerminalModifiers modifiers, uint timestampMilliseconds)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        PuttyEngine.Check(
            NativeMethods.SessionMouse(handle, (int)action, (int)button, x, y, (int)modifiers, timestampMilliseconds),
            nameof(Mouse));
    }

    /// <summary>A wheel step in units of 120 per notch, positive up (right if <paramref name="horizontal"/>).</summary>
    public void Wheel(int delta, bool horizontal, int x, int y, TerminalModifiers modifiers)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        PuttyEngine.Check(NativeMethods.SessionWheel(handle, delta, horizontal ? 1 : 0, x, y, (int)modifiers), nameof(Wheel));
    }

    /// <summary>Moves the scrollback, from the host's scrollbar. <see cref="ScrollbarChanged"/> reports the result.</summary>
    public void Scroll(ScrollMode mode, int amount = 0)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        PuttyEngine.Check(NativeMethods.SessionScroll(handle, (int)mode, amount), nameof(Scroll));
    }

    /// <summary>
    /// Types text that did not come as key messages (what an IME committed, the emoji panel, voice typing): it goes
    /// where typed characters go, the line discipline and the connection. A surrogate pair must be in one call.
    /// </summary>
    public unsafe void TypeText(string text)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentNullException.ThrowIfNull(text);
        fixed (char* p = text)
        {
            PuttyEngine.Check(NativeMethods.SessionTypeText(handle, p, text.Length), nameof(TypeText));
        }
    }

    /// <summary>Pastes text into the terminal (answers <see cref="ClipboardPasteRequested"/>).</summary>
    public unsafe void Paste(string text)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentNullException.ThrowIfNull(text);
        fixed (char* p = text)
        {
            PuttyEngine.Check(NativeMethods.SessionPaste(handle, p, text.Length), nameof(Paste));
        }
    }

    /// <summary>The terminal's size in cells and the cell size in pixels.</summary>
    public TerminalGeometry Geometry
    {
        get
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            PuttyEngine.Check(NativeMethods.SessionTerminalGeometry(handle, out NativeTerminalGeometry g), nameof(Geometry));
            return new TerminalGeometry(g.Columns, g.Rows, g.CellWidth, g.CellHeight);
        }
    }

    /// <summary>For tests: one pixel (0x00RRGGBB) of the last frame of an offscreen view.</summary>
    public uint ReadPixel(int x, int y)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        PuttyEngine.Check(NativeMethods.DiagSessionReadPixel(handle, x, y, out uint rgb), nameof(ReadPixel));
        return rgb;
    }

    /// <summary>
    /// Local echo and local line editing, the two settings that decide whether typing is buffered
    /// until Enter. <see cref="LocalMode.Auto"/> lets the protocol decide: on for Raw, off for SSH.
    /// The session needs <see cref="EnableTerminal"/> first.
    /// </summary>
    public void SetLocalEchoAndEditing(LocalMode echo, LocalMode editing)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        PuttyEngine.Check(
            NativeMethods.SessionSetLocalEchoEdit(handle, (int)echo, (int)editing), nameof(SetLocalEchoAndEditing));
    }

    /// <summary>
    /// Says which session's terminal has keyboard focus, or none (<c>null</c>). While one does,
    /// a message hook on the engine thread turns keyboard messages into terminal input exactly as
    /// PuTTY's own window would; with none it does nothing. Call it from the focus events of the
    /// terminal view, on the engine thread. The session needs <see cref="EnableTerminal"/> first.
    /// </summary>
    public static void SetKeyboardTarget(PuttySession? session)
    {
        nint native = 0;
        if (session is not null)
        {
            ObjectDisposedException.ThrowIf(session.disposed, session);
            native = session.handle;
        }

        int result = NativeMethods.SetKeyTarget(native);
        if (session is null && result == NativeMethods.ErrState)
        {
            // The engine is not running (any more): there is no target left to clear. A closing window loses its focus
            // after the app has stopped the engine, and an exception from that focus event ends the whole process.
            return;
        }

        PuttyEngine.Check(result, nameof(SetKeyboardTarget));
    }

    /// <summary>True while the connection is up.</summary>
    public bool IsConnected
    {
        get
        {
            if (disposed || !started)
            {
                return false;
            }

            int result = NativeMethods.SessionIsConnected(handle);
            PuttyEngine.Check(result, nameof(IsConnected));
            return result == 1;
        }
    }

    /// <summary>Sends bytes to the remote side.</summary>
    public void Send(ReadOnlySpan<byte> data)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (!started)
        {
            throw new InvalidOperationException("The session has not been started.");
        }

        fixed (byte* pointer = data)
        {
            PuttyEngine.Check(NativeMethods.SessionSend(handle, pointer, (uint)data.Length), nameof(Send));
        }
    }

    /// <summary>Closes the connection. Call it on the engine thread.</summary>
    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        if (!started)
        {
            return;
        }

        int result = NativeMethods.SessionClose(handle);
        handle = 0;
        if (self.IsAllocated)
        {
            self.Free();
        }

        // The engine having shut down already closed the session, which is fine. Closing from
        // the wrong thread is a bug in the caller and must not be silent.
        if (result == NativeMethods.ErrThread)
        {
            PuttyEngine.Check(result, nameof(Dispose));
        }
    }

    // ----- Native callbacks -------------------------------------------------------------
    //
    // These run on the engine thread, called from native code. An exception must never
    // propagate back into it, so each one catches everything.

    private static PuttySession? From(nint user) =>
        user == 0 ? null : GCHandle.FromIntPtr(user).Target as PuttySession;

    [UnmanagedCallersOnly]
    private static void OnOutput(nint user, void* data, uint length, int isStderr)
    {
        try
        {
            if (From(user) is { } session)
            {
                byte[] copy = new ReadOnlySpan<byte>(data, (int)length).ToArray();
                session.Output?.Invoke(session, new SessionOutputEventArgs(copy, isStderr != 0));
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"PuttySession.Output handler threw: {ex}");
        }
    }

    [UnmanagedCallersOnly]
    private static void OnLog(nint user, byte* text)
    {
        try
        {
            if (From(user) is { } session)
            {
                string line = Marshal.PtrToStringUTF8((nint)text) ?? string.Empty;
                session.Log?.Invoke(session, new SessionLogEventArgs(line));
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"PuttySession.Log handler threw: {ex}");
        }
    }

    [UnmanagedCallersOnly]
    private static void OnError(nint user, byte* message, int isFatal)
    {
        try
        {
            if (From(user) is { } session)
            {
                string text = Marshal.PtrToStringUTF8((nint)message) ?? string.Empty;
                session.LastError = text;
                session.Error?.Invoke(session, new SessionErrorEventArgs(text, isFatal != 0));
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"PuttySession.Error handler threw: {ex}");
        }
    }

    [UnmanagedCallersOnly]
    private static void OnTitle(nint user, byte* title)
    {
        try
        {
            if (From(user) is { } session)
            {
                string text = Marshal.PtrToStringUTF8((nint)title) ?? string.Empty;
                session.TitleChanged?.Invoke(session, new SessionTitleEventArgs(text));
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"PuttySession.TitleChanged handler threw: {ex}");
        }
    }

    [UnmanagedCallersOnly]
    private static void OnClipboardWrite(nint user, char* text, int units)
    {
        try
        {
            if (From(user) is { } session)
            {
                session.ClipboardWrite?.Invoke(session, new SessionClipboardEventArgs(new string(text, 0, units)));
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"PuttySession.ClipboardWrite handler threw: {ex}");
        }
    }

    [UnmanagedCallersOnly]
    private static void OnClipboardRequestPaste(nint user)
    {
        try
        {
            if (From(user) is { } session)
            {
                session.ClipboardPasteRequested?.Invoke(session, EventArgs.Empty);
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"PuttySession.ClipboardPasteRequested handler threw: {ex}");
        }
    }

    [UnmanagedCallersOnly]
    private static void OnBell(nint user, int indicator)
    {
        try
        {
            if (From(user) is { } session)
            {
                session.Bell?.Invoke(session, new SessionBellEventArgs(indicator is >= 0 and <= 2 ? (BellIndicator)indicator : BellIndicator.Disabled));
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"PuttySession.Bell handler threw: {ex}");
        }
    }

    [UnmanagedCallersOnly]
    private static void OnScrollbar(nint user, int total, int start, int page)
    {
        try
        {
            if (From(user) is { } session)
            {
                session.ScrollbarChanged?.Invoke(session, new SessionScrollbarEventArgs(total, start, page));
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"PuttySession.ScrollbarChanged handler threw: {ex}");
        }
    }

    [UnmanagedCallersOnly]
    private static void OnCursor(nint user, int x, int y, int width, int height)
    {
        try
        {
            if (From(user) is { } session)
            {
                session.CursorMoved?.Invoke(session, new SessionCursorEventArgs(x, y, width, height));
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"PuttySession.CursorMoved handler threw: {ex}");
        }
    }

    [UnmanagedCallersOnly]
    private static void OnClosed(nint user)
    {
        try
        {
            if (From(user) is { } session)
            {
                session.Closed?.Invoke(session, EventArgs.Empty);
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"PuttySession.Closed handler threw: {ex}");
        }
    }
}
