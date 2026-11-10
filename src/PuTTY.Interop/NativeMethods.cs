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

    [LibraryImport(Library, EntryPoint = "pe_diag_queue_callback")]
    internal static partial int DiagQueueCallback();

    [LibraryImport(Library, EntryPoint = "pe_diag_schedule_timer")]
    internal static partial int DiagScheduleTimer(uint delayMilliseconds);

    [LibraryImport(Library, EntryPoint = "pe_diag_get_counters")]
    internal static partial int DiagGetCounters(out NativeDiagCounters counters);
}

/// <summary>Mirrors <c>pe_diag_counters</c>.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct NativeDiagCounters
{
    public uint CallbacksRun;
    public uint TimersFired;
}
