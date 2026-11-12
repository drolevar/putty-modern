using System.Diagnostics;
using System.Runtime.InteropServices;

namespace PuTTY.Interop;

/// <summary>What kind of process-level warning PuTTY's core raised.</summary>
public enum EngineWarningKind
{
    /// <summary>A problem that stops nothing, for example the random seed file could not be written.</summary>
    NonFatal = 0,

    /// <summary>A loaded SSH-2 private key uses the old, not fully tamperproof file format.</summary>
    OldKeyFormat = 1,
}

/// <summary>The engine is about to end the process: what is known about why.</summary>
public sealed class EngineFatalEventArgs(string message) : EventArgs
{
    public string Message { get; } = message;
}

/// <summary>A warning that belongs to no session.</summary>
public sealed class EngineWarningEventArgs(EngineWarningKind kind, string message) : EventArgs
{
    public EngineWarningKind Kind { get; } = kind;

    public string Message { get; } = message;
}

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
    public static void Initialize()
    {
        Check(NativeMethods.Init(), nameof(Initialize));
        RegisterWarningHandler();
        RegisterFatalHandler();
        RegisterLogHandler();
        PuttyLog.Info("engine", $"engine started, ABI {NativeAbiVersion}");
    }

    /// <summary>
    /// Sends the engine's own trace (native code: sessions opened and closed, the serial line's settings, views, the palette)
    /// to <see cref="PuttyLog"/> at the level it is set to now. Called by <see cref="Initialize"/>; call it again after
    /// <see cref="PuttyLog.Configure"/> if the log was switched on later. Does nothing while the log is off.
    /// </summary>
    public static unsafe void RegisterLogHandler()
    {
        int maxLevel = (int)PuttyLog.Level;
        Check(NativeMethods.SetLogHandler(maxLevel == 0 ? null : &OnLog, 0, maxLevel), nameof(RegisterLogHandler));
    }

    [UnmanagedCallersOnly]
    private static unsafe void OnLog(nint user, int level, byte* message)
    {
        try
        {
            PuttyLog.Write((PuttyLogLevel)Math.Clamp(level, 1, 5), "engine", Marshal.PtrToStringUTF8((nint)message) ?? string.Empty);
        }
        catch (Exception)
        {
            // An exception must never cross back into the engine, and the log is not worth reporting.
        }
    }

    /// <summary>
    /// Raised on the thread that hit a fatal error, just before the process ends: PuTTY's own fatal errors (out of memory
    /// and the like), a failed assertion in its code, any abort. The text is what is known; for an assertion it says the
    /// details went to standard error. A handler may log, save what can be kept (the app saves its workspace) and tell the
    /// user, must not rely on the engine being sound, and must not throw. The process ends when it returns. Raised at most once.
    /// </summary>
    public static event EventHandler<EngineFatalEventArgs>? Fatal;

    private static unsafe void RegisterFatalHandler() =>
        Check(NativeMethods.SetFatalHandler(&OnFatal, 0), nameof(Fatal));

    [UnmanagedCallersOnly]
    private static unsafe void OnFatal(nint user, byte* message)
    {
        try
        {
            string text = Marshal.PtrToStringUTF8((nint)message) ?? string.Empty;
            PuttyLog.Error("engine", $"fatal error, the process is about to end: {text}");
            Fatal?.Invoke(null, new EngineFatalEventArgs(text));
        }
        catch (Exception ex)
        {
            // An exception must never cross back into the engine; and nothing more can be done about this one.
            PuttyLog.Error("interop", $"PuttyEngine.Fatal handler threw: {ex}");
        }
    }

    /// <summary>
    /// Raised on the engine thread for warnings PuTTY raises outside any session (a problem
    /// writing the random seed file, an old-format private key). Without a subscriber they are only
    /// written to the debugger.
    /// </summary>
    public static event EventHandler<EngineWarningEventArgs>? Warning;

    private static unsafe void RegisterWarningHandler() =>
        Check(NativeMethods.SetWarningHandler(&OnWarning, 0), nameof(Warning));

    [UnmanagedCallersOnly]
    private static unsafe void OnWarning(nint user, int kind, byte* message)
    {
        try
        {
            string text = Marshal.PtrToStringUTF8((nint)message) ?? string.Empty;
            PuttyLog.Warning("engine", $"warning ({(EngineWarningKind)kind}): {text}");
            Warning?.Invoke(null, new EngineWarningEventArgs((EngineWarningKind)kind, text));
        }
        catch (Exception ex)
        {
            // An exception must never cross back into the engine.
            PuttyLog.Error("interop", $"PuttyEngine.Warning handler threw: {ex}");
        }
    }

    /// <summary>Closes every socket, drains pending work and stops the engine.</summary>
    public static void Shutdown()
    {
        PuttyLog.Info("engine", "engine stopping");
        Check(NativeMethods.Shutdown(), nameof(Shutdown));
    }

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

        /// <summary>A failed assertion in the engine's own code: <see cref="Fatal"/> is raised and the process aborts. Only for checking a host's handler in a process of its own.</summary>
        public static void RaiseAssertion() => Check(NativeMethods.DiagAssert(), nameof(RaiseAssertion));

        /// <summary>Takes the path of a real fatal error with this text: <see cref="Fatal"/> is raised and the process ends. Only for checking a host's handler in a process of its own.</summary>
        public static void RaiseFatal(string message) =>
            Check(NativeMethods.DiagFatal(message), nameof(RaiseFatal));

        /// <summary>Makes PuTTY's core raise a warning of the given kind, as it would itself.</summary>
        public static void RaiseWarning(EngineWarningKind kind) =>
            Check(NativeMethods.DiagRaiseWarning((int)kind), nameof(RaiseWarning));

        /// <summary>
        /// Draws <paramref name="text"/> with the terminal renderer onto an offscreen bitmap and
        /// reports the cell size and, per character, how many pixels differ from the background.
        /// Needs no window and no running engine.
        /// </summary>
        public static unsafe RenderProbe RenderText(string fontFamily, float sizePx, string text, uint foreground, uint background, bool bold = false)
        {
            Check(NativeMethods.DiagRenderText(fontFamily, sizePx, text, foreground, background, bold ? 1 : 0, out NativeDiagRenderResult native), nameof(RenderText));
            int[] ink = new int[native.Cells];
            for (int i = 0; i < ink.Length; i++)
            {
                ink[i] = native.Ink[i];
            }

            return new RenderProbe(native.CellWidth, native.CellHeight, ink, native.CornerRgb);
        }

        /// <summary>
        /// Makes a throwaway Ed25519 key for tests of key authentication: writes a PuTTY private key
        /// file (.ppk) at <paramref name="path"/>, encrypted if <paramref name="passphrase"/> is given,
        /// and returns the public key as an <c>ssh-ed25519 AAAA...</c> line. Call it on the engine thread.
        /// </summary>
        public static unsafe string GenerateEd25519Key(string path, string? passphrase = null)
        {
            byte* buffer = stackalloc byte[512];
            Check(NativeMethods.DiagGenerateEd25519Key(path, passphrase, buffer, 512), nameof(GenerateEd25519Key));
            return Marshal.PtrToStringUTF8((nint)buffer) ?? string.Empty;
        }

        /// <summary>Reads the counters; <see cref="Initialize"/> resets them to zero.</summary>
        public static DiagnosticCounters GetCounters()
        {
            Check(NativeMethods.DiagGetCounters(out NativeDiagCounters native), nameof(GetCounters));
            return new DiagnosticCounters(native.CallbacksRun, native.TimersFired);
        }
    }

    /// <summary>What <see cref="Diagnostics.RenderText"/> drew: the cell size in pixels and, per character, the pixels that differ from the background.</summary>
    public sealed record RenderProbe(int CellWidth, int CellHeight, int[] Ink, uint CornerRgb);

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
