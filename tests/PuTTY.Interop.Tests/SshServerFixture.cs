using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace PuTTY.Interop.Tests;

/// <summary>
/// The test SSH server (<c>tools/ssh-test-server/server.js</c>, on the ssh2 npm package) as a
/// child process. It listens on loopback on a port it picks, reports the port and its host key's
/// fingerprints, and ends when this object is disposed (its stdin closes). Needs Node and the
/// one-time <c>tools/start-ssh-test-server.ps1 -Install</c>; without them a test is inconclusive,
/// not failed.
/// </summary>
internal sealed class SshServerFixture : IDisposable
{
    private readonly Process process;
    private readonly List<string> events = [];

    private SshServerFixture(Process process, JsonElement info)
    {
        this.process = process;
        // What the server reports after its first line (logins, shells, commands): read for as long as it lives.
        _ = Task.Run(async () =>
        {
            try
            {
                while (await process.StandardOutput.ReadLineAsync() is { } line)
                {
                    lock (events)
                    {
                        events.Add(line);
                    }
                }
            }
            catch (Exception)
            {
                // the process ended
            }
        });
        Port = info.GetProperty("port").GetInt32();
        HostKeyType = info.GetProperty("hostKeyType").GetString()!;
        Sha256 = info.GetProperty("sha256").GetString()!;
        Md5 = info.GetProperty("md5").GetString()!;
    }

    public const string User = "test";
    public const string Password = "secret";

    public int Port { get; }

    public string HostKeyType { get; }

    public string Sha256 { get; }

    public string Md5 { get; }

    /// <summary>The JSON lines the server has reported so far, such as <c>{"event":"command","text":"echo hi"}</c>.</summary>
    public string[] Events { get { lock (events) { return [.. events]; } } }

    /// <summary>Starts the server, or marks the test inconclusive if it cannot be started.</summary>
    /// <param name="arguments">Arguments for server.js, such as <c>--methods keyboard-interactive</c> or <c>--port 2222</c>.</param>
    public static SshServerFixture Start(params string[] arguments)
    {
        string root = RepositoryRoot();
        string script = Path.Combine(root, "tools", "ssh-test-server", "server.js");
        string modules = Path.Combine(root, "artifacts", "ssh-test-server", "node_modules");
        if (!Directory.Exists(Path.Combine(modules, "ssh2")))
        {
            Assert.Inconclusive("The test SSH server is not installed: run tools\\start-ssh-test-server.ps1 -Install");
        }

        var start = new ProcessStartInfo("node")
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        start.ArgumentList.Add(script);
        foreach (string argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        start.Environment["NODE_PATH"] = modules;

        Process? process;
        try
        {
            process = Process.Start(start);
        }
        catch (System.ComponentModel.Win32Exception)
        {
            Assert.Inconclusive("Node is not on PATH, so the test SSH server cannot run.");
            throw;
        }

        if (process is null)
        {
            Assert.Inconclusive("Node did not start.");
        }

        try
        {
            Task<string?> firstLine = process!.StandardOutput.ReadLineAsync();
            if (!firstLine.Wait(TimeSpan.FromSeconds(20)) || firstLine.Result is not { Length: > 0 } line)
            {
                string errors = process.HasExited ? process.StandardError.ReadToEnd() : "(no output in 20 s)";
                throw new InvalidOperationException($"The test SSH server did not report a port: {errors}");
            }

            using JsonDocument document = JsonDocument.Parse(line);
            return new SshServerFixture(process, document.RootElement.Clone());
        }
        catch
        {
            Kill(process!);
            throw;
        }
    }

    public void Dispose() => Kill(process);

    private static void Kill(Process process)
    {
        try
        {
            process.StandardInput.Close();
            if (!process.WaitForExit(2000))
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(2000);
            }
        }
        catch (InvalidOperationException)
        {
            // already gone
        }
        finally
        {
            process.Dispose();
        }
    }

    private static string RepositoryRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "PuTTY.slnx")))
            {
                return dir.FullName;
            }
        }

        throw new InvalidOperationException("PuTTY.slnx was not found above " + AppContext.BaseDirectory);
    }
}

/// <summary>
/// Points the process's <c>HKEY_CURRENT_USER</c> at a scratch registry key for as long as it lives, so
/// PuTTY's host key cache (and anything else the engine keeps in the registry) goes there and the
/// user's real PuTTY settings and known hosts are never read or written by a test.
/// </summary>
internal sealed partial class RegistrySandbox : IDisposable
{
    private const string ScratchRoot = @"Software\PuttyModernTests";
    private static readonly nint HkeyCurrentUser = unchecked((nint)(int)0x80000001);   // sign-extended, as the predefined handles are
    private const int KeyAllAccess = 0xF003F;

    private readonly string path;
    private readonly string seedDirectory;
    private nint key;

    public RegistrySandbox()
    {
        // Leftovers of a run that was killed are cleared first.
        RegDeleteTree(HkeyCurrentUser, ScratchRoot);
        path = $@"{ScratchRoot}\{Guid.NewGuid():N}";
        int result = RegCreateKeyExW(HkeyCurrentUser, path, 0, null, 0, KeyAllAccess, 0, out key, out _);
        if (result != 0)
        {
            throw new InvalidOperationException($"Could not create the scratch registry key (error {result}).");
        }

        result = RegOverridePredefKey(HkeyCurrentUser, key);
        if (result != 0)
        {
            RegCloseKey(key);
            throw new InvalidOperationException($"RegOverridePredefKey failed (error {result}).");
        }

        // PuTTY keeps a random seed file and rewrites it whenever its random number generator starts.
        // Left alone it would be the user's own file in %LOCALAPPDATA%; the registry decides where it is.
        seedDirectory = Path.Combine(Path.GetTempPath(), "puttymodern-test-seed-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(seedDirectory);
        using var putty = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(@"Software\SimonTatham\PuTTY");
        putty.SetValue("RandSeedFile", Path.Combine(seedDirectory, "seed.rnd"));
    }

    /// <summary>The names of the host keys PuTTY has cached in the sandbox, such as <c>ssh-ed25519@22:127.0.0.1</c>.</summary>
    public string[] CachedHostKeys()
    {
        using var hostKeys = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\SimonTatham\PuTTY\SshHostKeys");
        return hostKeys?.GetValueNames() ?? [];
    }

    public void Dispose()
    {
        RegOverridePredefKey(HkeyCurrentUser, 0);
        if (key != 0)
        {
            RegCloseKey(key);
            key = 0;
        }

        RegDeleteTree(HkeyCurrentUser, path);
        try
        {
            Directory.Delete(seedDirectory, recursive: true);
        }
        catch (IOException)
        {
            // a scratch directory in %TEMP%: the next run does not depend on it
        }
    }

    [LibraryImport("advapi32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int RegCreateKeyExW(nint hKey, string subKey, int reserved, string? cls, int options,
        int samDesired, nint securityAttributes, out nint result, out int disposition);

    [LibraryImport("advapi32.dll")]
    private static partial int RegOverridePredefKey(nint hKey, nint newHKey);

    [LibraryImport("advapi32.dll")]
    private static partial int RegCloseKey(nint hKey);

    [LibraryImport("advapi32.dll", EntryPoint = "RegDeleteTreeW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int RegDeleteTree(nint hKey, string subKey);
}
