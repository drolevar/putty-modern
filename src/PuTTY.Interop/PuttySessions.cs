using System.Diagnostics;
using System.Runtime.InteropServices;

namespace PuTTY.Interop;

/// <summary>
/// The sessions saved in PuTTY's own storage (the registry key PuTTY itself uses), which this
/// application shows automatically and never writes to or migrates. Open one with
/// <see cref="PuttySession.OpenSaved"/>.
/// </summary>
public static unsafe class PuttySessions
{
    /// <summary>The name PuTTY gives the settings new sessions start from; it is not a session to open.</summary>
    public const string DefaultSettingsName = "Default Settings";

    /// <summary>
    /// The names of the saved sessions in storage order, including <see cref="DefaultSettingsName"/>
    /// if the user saved one. Call it on the engine thread, after <see cref="PuttyEngine.Initialize"/>.
    /// </summary>
    public static IReadOnlyList<string> List()
    {
        var names = new List<string>();
        GCHandle handle = GCHandle.Alloc(names);
        try
        {
            PuttyEngine.Check(NativeMethods.SessionsEnumerate(&OnName, GCHandle.ToIntPtr(handle)), nameof(List));
        }
        finally
        {
            handle.Free();
        }

        return names;
    }

    /// <summary>
    /// Where a saved session connects to, for showing in a list: its host (the line, for a Serial session), port
    /// and protocol. Null if there is no such session. The storage is only read. Call it on the engine thread.
    /// </summary>
    public static SavedSessionSummary? Describe(string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        const int Capacity = 512;
        byte* host = stackalloc byte[Capacity];
        int rc = NativeMethods.SessionSummary(name, host, Capacity, out int protocol, out int port);
        if (rc == NativeMethods.ErrFailed)
        {
            return null;
        }

        PuttyEngine.Check(rc, nameof(Describe));
        return new SavedSessionSummary(Marshal.PtrToStringUTF8((nint)host) ?? string.Empty, port, (SavedSessionProtocol)protocol);
    }

    [UnmanagedCallersOnly]
    private static void OnName(nint user, byte* name)
    {
        try
        {
            if (GCHandle.FromIntPtr(user).Target is List<string> names && Marshal.PtrToStringUTF8((nint)name) is { } text)
            {
                names.Add(text);
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"PuttySessions.List callback threw: {ex}");
        }
    }
}
