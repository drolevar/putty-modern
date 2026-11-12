using System.Diagnostics;
using System.Runtime.InteropServices;

namespace PuTTY.Interop;

/// <summary>The font families installed on this machine, for a font chooser (WinUI has none of its own).</summary>
public static unsafe class InstalledFonts
{
    /// <summary>The family names, sorted, without duplicates. Call it on the engine thread.</summary>
    public static IReadOnlyList<string> Families()
    {
        var names = new List<string>();
        GCHandle handle = GCHandle.Alloc(names);
        try
        {
            PuttyEngine.Check(NativeMethods.FontsEnumerate(&OnName, GCHandle.ToIntPtr(handle)), nameof(Families));
        }
        finally
        {
            handle.Free();
        }

        names.Sort(StringComparer.CurrentCultureIgnoreCase);
        return names;
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
            PuttyLog.Error("interop", $"InstalledFonts callback threw: {ex}");
        }
    }
}
