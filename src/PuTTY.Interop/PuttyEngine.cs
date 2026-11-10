namespace PuTTY.Interop;

/// <summary>
/// Entry point to the native PuTTY engine (<c>puttyengine.dll</c>).
/// </summary>
/// <remarks>
/// PuTTY's core is single-threaded with global state, so the engine lives on one thread: the
/// one that calls <see cref="Initialize"/>. That thread must run a Win32 message loop (the
/// WinUI UI thread already does; use <see cref="Pump"/> on a thread that has none). Every
/// other call must come from the same thread, otherwise it throws.
/// </remarks>
public static class PuttyEngine
{
    /// <summary>
    /// The ABI version this managed code was written against. It must equal the
    /// native <c>PE_ABI_VERSION</c>; bump both when the C header changes incompatibly.
    /// </summary>
    public const uint ExpectedAbiVersion = 1;

    /// <summary>The ABI version reported by the loaded native engine.</summary>
    public static uint NativeAbiVersion => NativeMethods.AbiVersion();

    /// <summary>
    /// Throws if the native engine's ABI version differs from <see cref="ExpectedAbiVersion"/>.
    /// Call once at startup, before any other engine call.
    /// </summary>
    public static void EnsureCompatible()
    {
        uint native = NativeAbiVersion;
        if (native != ExpectedAbiVersion)
        {
            throw new InvalidOperationException(
                $"puttyengine ABI mismatch: native is {native}, managed expects {ExpectedAbiVersion}.");
        }
    }

    /// <summary>Smoke test that runs PuTTY's Conf store inside the engine.</summary>
    public static bool Probe() => NativeMethods.Probe() != 0;

    /// <summary>
    /// Starts the engine on the calling thread. May be called again after
    /// <see cref="Shutdown"/>, including from a different thread.
    /// </summary>
    public static void Initialize() => Check(NativeMethods.Init(), nameof(Initialize));

    /// <summary>Closes every socket, drains pending work and stops the engine.</summary>
    public static void Shutdown() => Check(NativeMethods.Shutdown(), nameof(Shutdown));

    /// <summary>
    /// Waits up to <paramref name="timeout"/> for window messages and dispatches whatever is
    /// queued. Hosts that already run a message loop never call this; it is for threads that
    /// have none, such as tests. Returns the number of messages dispatched.
    /// </summary>
    public static int Pump(TimeSpan timeout)
    {
        int result = NativeMethods.Pump(ToMilliseconds(timeout));
        if (result < 0)
        {
            Check(result, nameof(Pump));
        }

        return result;
    }

    /// <summary>Hooks tests use to check that the event loop is wired up.</summary>
    public static class Diagnostics
    {
        /// <summary>Queues a deferred callback through PuTTY's top-level callback queue.</summary>
        public static void QueueCallback() =>
            Check(NativeMethods.DiagQueueCallback(), nameof(QueueCallback));

        /// <summary>Schedules a PuTTY timer that fires after <paramref name="delay"/>.</summary>
        public static void ScheduleTimer(TimeSpan delay) =>
            Check(NativeMethods.DiagScheduleTimer(ToMilliseconds(delay)), nameof(ScheduleTimer));

        /// <summary>Reads the counters; <see cref="Initialize"/> resets them to zero.</summary>
        public static DiagnosticCounters GetCounters()
        {
            Check(NativeMethods.DiagGetCounters(out NativeDiagCounters native), nameof(GetCounters));
            return new DiagnosticCounters(native.CallbacksRun, native.TimersFired);
        }
    }

    /// <summary>How many diagnostic callbacks and timers have run since <see cref="Initialize"/>.</summary>
    public readonly record struct DiagnosticCounters(uint CallbacksRun, uint TimersFired);

    private static uint ToMilliseconds(TimeSpan span)
    {
        double ms = span.TotalMilliseconds;
        return ms <= 0 ? 0 : ms >= uint.MaxValue ? uint.MaxValue : (uint)ms;
    }

    internal static void Check(int result, string operation)
    {
        if (result >= NativeMethods.Ok)
        {
            return;
        }

        string reason = result switch
        {
            NativeMethods.ErrState => "the engine is not in the right state (not initialised, or already initialised)",
            NativeMethods.ErrThread => "it must be called on the thread that initialised the engine",
            NativeMethods.ErrFailed => "an operating-system call failed",
            _ => $"unexpected result code {result}",
        };
        throw new InvalidOperationException($"PuttyEngine.{operation} failed: {reason}.");
    }
}
