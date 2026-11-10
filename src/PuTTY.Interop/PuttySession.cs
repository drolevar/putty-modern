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
public sealed unsafe class PuttySession : IDisposable
{
    private readonly string host;
    private readonly int port;
    private readonly SessionProtocol protocol;

    private nint handle;
    private GCHandle self;          // lets native code find this object again
    private bool started;
    private bool disposed;

    public PuttySession(string host, int port, SessionProtocol protocol)
    {
        ArgumentException.ThrowIfNullOrEmpty(host);
        ArgumentOutOfRangeException.ThrowIfLessThan(port, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(port, 65535);
        this.host = host;
        this.port = port;
        this.protocol = protocol;
    }

    public event EventHandler<SessionOutputEventArgs>? Output;

    public event EventHandler<SessionLogEventArgs>? Log;

    public event EventHandler<SessionErrorEventArgs>? Error;

    /// <summary>The connection has ended. Raised once; the session still has to be disposed.</summary>
    public event EventHandler? Closed;

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

        int result = NativeMethods.SessionOpen(
            host, port, (int)protocol, &events, GCHandle.ToIntPtr(self), out nint native);
        if (result < NativeMethods.Ok)
        {
            self.Free();
            disposed = true;
            string detail = LastError is { } message ? $" ({message})" : string.Empty;
            throw new InvalidOperationException(
                $"Could not open a {protocol} session to {host}:{port}{detail}.");
        }

        handle = native;
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

        PuttyEngine.Check(NativeMethods.SetKeyTarget(native), nameof(SetKeyboardTarget));
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
