using System.Runtime.InteropServices;

namespace PuTTY.Interop;

/// <summary>
/// Raw declarations for the flat C ABI in native/engine/include/putty_engine.h.
/// Keep these in step with that header; every parameter and return type must be blittable.
/// </summary>
internal static partial class NativeMethods
{
    internal const string Library = "puttyengine";

    [LibraryImport(Library, EntryPoint = "pe_abi_version")]
    internal static partial uint AbiVersion();

    [LibraryImport(Library, EntryPoint = "pe_probe")]
    internal static partial int Probe();
}
