using System.Runtime.CompilerServices;

// Every signature crossing the native boundary is blittable, so the runtime's
// built-in marshalling is switched off. This also makes LibraryImport the only
// supported interop mechanism here, which is what NativeAOT needs.
[assembly: DisableRuntimeMarshalling]
