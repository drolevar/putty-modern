namespace PuTTY.Interop;

/// <summary>Entry point to the native PuTTY engine (<c>puttyengine.dll</c>).</summary>
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
}
