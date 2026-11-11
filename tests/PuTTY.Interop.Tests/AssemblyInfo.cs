// PuTTY's core is single-threaded with global state (timer queue, callback queue, socket
// tree), and every test starts its own engine instance, so tests must never overlap.
[assembly: DoNotParallelize]

// The engine is Windows-only, and some tests use the registry.
[assembly: System.Runtime.Versioning.SupportedOSPlatform("windows")]
