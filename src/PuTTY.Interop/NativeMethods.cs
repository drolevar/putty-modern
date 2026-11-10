using System.Runtime.InteropServices;

namespace PuTTY.Interop;

/// <summary>
/// Raw declarations for the flat C ABI in native/engine/include/putty_engine.h.
/// Keep these in step with that header; every parameter and return type must be blittable.
/// </summary>
internal static partial class NativeMethods
{
    internal const string Library = "puttyengine";

    // Result codes from the header.
    internal const int Ok = 0;
    internal const int ErrState = -1;
    internal const int ErrThread = -2;
    internal const int ErrFailed = -3;

    [LibraryImport(Library, EntryPoint = "pe_abi_version")]
    internal static partial uint AbiVersion();

    [LibraryImport(Library, EntryPoint = "pe_probe")]
    internal static partial int Probe();

    [LibraryImport(Library, EntryPoint = "pe_init")]
    internal static partial int Init();

    [LibraryImport(Library, EntryPoint = "pe_shutdown")]
    internal static partial int Shutdown();

    [LibraryImport(Library, EntryPoint = "pe_pump")]
    internal static partial int Pump(uint timeoutMilliseconds);

    [LibraryImport(Library, EntryPoint = "pe_session_open", StringMarshalling = StringMarshalling.Utf8)]
    internal static unsafe partial int SessionOpen(
        string host, int port, int protocol, NativeSessionEvents* events, nint user, out nint session);

    [LibraryImport(Library, EntryPoint = "pe_session_send")]
    internal static unsafe partial int SessionSend(nint session, byte* data, uint length);

    [LibraryImport(Library, EntryPoint = "pe_session_is_connected")]
    internal static partial int SessionIsConnected(nint session);

    [LibraryImport(Library, EntryPoint = "pe_session_close")]
    internal static partial int SessionClose(nint session);

    [LibraryImport(Library, EntryPoint = "pe_session_enable_terminal")]
    internal static partial int SessionEnableTerminal(nint session, int columns, int rows);

    [LibraryImport(Library, EntryPoint = "pe_session_set_local_echo_edit")]
    internal static partial int SessionSetLocalEchoEdit(nint session, int echo, int edit);

    [LibraryImport(Library, EntryPoint = "pe_set_key_target")]
    internal static partial int SetKeyTarget(nint session);

    [LibraryImport(Library, EntryPoint = "pe_set_warning_handler")]
    internal static unsafe partial int SetWarningHandler(delegate* unmanaged<nint, int, byte*, void> handler, nint user);

    [LibraryImport(Library, EntryPoint = "pe_diag_raise_warning")]
    internal static partial int DiagRaiseWarning(int kind);

    [LibraryImport(Library, EntryPoint = "pe_diag_render_text", StringMarshalling = StringMarshalling.Utf16)]
    internal static partial int DiagRenderText(
        string fontFamily, float sizePx, string text, uint fgRgb, uint bgRgb, int bold, out NativeDiagRenderResult result);

    [LibraryImport(Library, EntryPoint = "pe_diag_queue_callback")]
    internal static partial int DiagQueueCallback();

    [LibraryImport(Library, EntryPoint = "pe_diag_schedule_timer")]
    internal static partial int DiagScheduleTimer(uint delayMilliseconds);

    [LibraryImport(Library, EntryPoint = "pe_diag_get_counters")]
    internal static partial int DiagGetCounters(out NativeDiagCounters counters);
}

/// <summary>
/// Mirrors <c>pe_session_events</c>: function pointers the engine calls on its thread. Each
/// first argument is the <c>user</c> pointer given to <c>pe_session_open</c>.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct NativeSessionEvents
{
    public delegate* unmanaged<nint, void*, uint, int, void> Output;   // user, data, length, isStderr
    public delegate* unmanaged<nint, byte*, void> Log;                 // user, utf8 text
    public delegate* unmanaged<nint, byte*, int, void> Error;          // user, utf8 message, isFatal
    public delegate* unmanaged<nint, void> Closed;                     // user
}

/// <summary>Mirrors <c>pe_diag_render_result</c>.</summary>
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct NativeDiagRenderResult
{
    public int CellWidth;
    public int CellHeight;
    public int Cells;
    public fixed int Ink[64];
    public uint CornerRgb;
}

/// <summary>Mirrors <c>pe_diag_counters</c>.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct NativeDiagCounters
{
    public uint CallbacksRun;
    public uint TimersFired;
}
