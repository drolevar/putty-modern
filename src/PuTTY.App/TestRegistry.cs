using System.Runtime.InteropServices;

namespace PuTTY.App;

/// <summary>
/// A hook for end-to-end tests of the real app, nothing else: when the environment variable
/// <c>PUTTYMODERN_TEST_REGISTRY_ROOT</c> names a key under <c>Software\PuttyModernTests</c>, the app's
/// <c>HKEY_CURRENT_USER</c> is pointed at that key for the whole process, so PuTTY's storage (saved
/// sessions, the host key cache) is a scratch copy a script prepared and the user's real PuTTY settings are
/// neither read nor written. Anything outside that prefix is ignored, so the variable cannot be used to
/// redirect the app elsewhere. The same switch moves the application's own workspace file (folders and recents
/// of the Sessions panel) to <c>%TEMP%\PuttyModernTests\&lt;the key's last name&gt;\workspace.json</c>, so a test never
/// touches the user's <c>%APPDATA%\PuTTY Modern</c>.
/// </summary>
internal static partial class TestRegistry
{
    public const string Variable = "PUTTYMODERN_TEST_REGISTRY_ROOT";
    private const string AllowedPrefix = @"Software\PuttyModernTests\";
    private static readonly nint HkeyCurrentUser = unchecked((nint)(int)0x80000001);   // sign-extended, as predefined handles are
    private const int KeyAllAccess = 0xF003F;

    /// <summary>
    /// <c>PUTTYMODERN_TEST_COLLAPSED_PANELS=1</c> gives a run the real default layout, with Session details and the
    /// Connection log collapsed. Without it a run started with <c>--connect</c> or with the test hook keeps them docked,
    /// because the end-to-end checks read both and a collapsed panel shows nothing until it is opened.
    /// </summary>
    public const string CollapsedPanelsVariable = "PUTTYMODERN_TEST_COLLAPSED_PANELS";

    /// <summary>Where the workspace file lives when the test hook is active; null in normal use.</summary>
    public static string? WorkspaceFileOverride { get; private set; }

    /// <summary>True when the test hook is active.</summary>
    public static bool IsActive => WorkspaceFileOverride is not null;

    /// <summary>
    /// With the test hook active, <c>PUTTYMODERN_TEST_FATAL_AFTER=&lt;seconds&gt;</c> makes the engine hit a fatal error (the
    /// path of PuTTY's own modalfatalbox) that long after the window is up, so a test can see what the app keeps and does
    /// (App.OnEngineFatal): the workspace saved, the reason logged, the process ended with exit code 3.
    /// </summary>
    public const string FatalAfterVariable = "PUTTYMODERN_TEST_FATAL_AFTER";

    /// <summary>With <c>PUTTYMODERN_TEST_FATAL_KIND=assert</c> the error is a failed assertion (an abort) instead of PuTTY's modalfatalbox.</summary>
    public const string FatalKindVariable = "PUTTYMODERN_TEST_FATAL_KIND";

    private static Microsoft.UI.Dispatching.DispatcherQueueTimer? fatalTimer;

    public static void ScheduleFatalFromEnvironment(Microsoft.UI.Dispatching.DispatcherQueue queue)
    {
        if (!IsActive || !double.TryParse(Environment.GetEnvironmentVariable(FatalAfterVariable), System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out double seconds))
        {
            return;
        }

        fatalTimer = queue.CreateTimer();
        fatalTimer.Interval = TimeSpan.FromSeconds(seconds);
        fatalTimer.IsRepeating = false;
        bool assertion = string.Equals(Environment.GetEnvironmentVariable(FatalKindVariable), "assert", StringComparison.OrdinalIgnoreCase);
        fatalTimer.Tick += (_, _) =>
        {
            if (assertion)
            {
                PuTTY.Interop.PuttyEngine.Diagnostics.RaiseAssertion();
            }
            else
            {
                PuTTY.Interop.PuttyEngine.Diagnostics.RaiseFatal("test: a fatal error of the engine");
            }
        };
        fatalTimer.Start();
    }

    public static void ApplyFromEnvironment()
    {
        string? root = Environment.GetEnvironmentVariable(Variable);
        if (string.IsNullOrEmpty(root) || !root.StartsWith(AllowedPrefix, StringComparison.OrdinalIgnoreCase) || root.Contains(".."))
        {
            return;
        }

        if (RegCreateKeyExW(HkeyCurrentUser, root, 0, null, 0, KeyAllAccess, 0, out nint key, out _) != 0)
        {
            return;
        }

        // The key stays open for the life of the process, which is the point of an override.
        RegOverridePredefKey(HkeyCurrentUser, key);
        WorkspaceFileOverride = Path.Combine(Path.GetTempPath(), "PuttyModernTests", root[AllowedPrefix.Length..].Replace('\\', '_'), "workspace.json");
    }

    [LibraryImport("advapi32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int RegCreateKeyExW(nint hKey, string subKey, int reserved, string? cls, int options,
        int samDesired, nint securityAttributes, out nint result, out int disposition);

    [LibraryImport("advapi32.dll")]
    private static partial int RegOverridePredefKey(nint hKey, nint newHKey);
}
