<#
.SYNOPSIS
  Types pinyin through a real IME (Rime Weasel, installed in the Sandbox with winget) into the app's terminal and
  reports what reaches the server and what the screen shows: the measurement for Beads putty-modern-88o.2.

.DESCRIPTION
  Runs in Windows Sandbox only (tools\run-in-sandbox.ps1 -Smoke ime): it activates the IME's input profile for the
  session and injects real key events with SendInput. The app is started against a loopback server, the terminal is
  given the keyboard focus the way a user does, then `n i h a o` and Space are typed. Weasel turns them into the
  composition "ni hao" and the first candidate, the two characters U+4F60 U+597D, on Space; if the app's terminal
  gets them, the server receives their UTF-8 bytes E4 BD A0 E5 A5 BD.

  Screenshots of the whole screen (the IME draws its candidate window as a window of its own, which a screenshot of
  the app's window would not show) are written to artifacts\spike\ime-composing.png and ime-committed.png.
  Prints PASS or FAIL per check; the exit code is the number of failures.
#>
param(
    [Parameter(Mandatory)] [string] $Exe,
    [switch] $AllowHostDesktop
)

if ($env:USERNAME -ne 'WDAGUtilityAccount') {
    Write-Host 'This switches the input method and injects keys: run it in Windows Sandbox with tools\run-in-sandbox.ps1 -Smoke ime.'
    exit 99
}
. (Join-Path $PSScriptRoot 'smoke-common.ps1')
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$failures = 0
# A trace that survives the script being stopped abruptly (output through a redirect may be lost): one line per step.
$stepLog = Join-Path $root 'results\ime-steps.log'
Remove-Item $stepLog -ErrorAction SilentlyContinue
function Step($text) { [IO.File]::AppendAllText($stepLog, ("{0:HH:mm:ss.fff} {1}`r`n" -f (Get-Date), $text)) }
function Check($name, $ok, $detail = '') {
    if ($ok) { Write-Host "PASS  $name $detail" } else { Write-Host "FAIL  $name $detail"; $script:failures++ }
}

Add-Type -TypeDefinition @'
using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Windows.Forms;

public static class ImeSmokeNative
{
    // ITfInputProcessorProfileMgr (msctf.h): only ActivateProfile is called, and it is the first method after IUnknown's.
    [ComImport, Guid("71c6e74c-0f28-11d8-a82a-00065b84435c"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ITfInputProcessorProfileMgr
    {
        [PreserveSig] int ActivateProfile(uint profileType, ushort langid, ref Guid clsid, ref Guid profile, IntPtr hkl, uint flags);
    }

    /// <summary>Makes a TSF text service (an IME) the input method of the whole session, as choosing it in the language bar does.</summary>
    public static int Activate(ushort langid, string clsid, string profile)
    {
        object manager = Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("33C53A50-F456-4884-B049-85FD643ECFED")));
        Guid c = new Guid(clsid), p = new Guid(profile);
        const uint TF_PROFILETYPE_INPUTPROCESSOR = 1, TF_IPPMF_FORSESSION = 0x20000000, TF_IPPMF_DONTCARECURRENTINPUTLANGUAGE = 0x4;
        return ((ITfInputProcessorProfileMgr)manager).ActivateProfile(
            TF_PROFILETYPE_INPUTPROCESSOR, langid, ref c, ref p, IntPtr.Zero, TF_IPPMF_FORSESSION | TF_IPPMF_DONTCARECURRENTINPUTLANGUAGE);
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr LoadKeyboardLayoutW(string id, uint flags);

    /// <summary>Back to a plain keyboard layout (for example "00000409", US English) as the session's input method.</summary>
    public static int ActivateKeyboardLayout(ushort langid, string layoutId)
    {
        object manager = Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("33C53A50-F456-4884-B049-85FD643ECFED")));
        Guid none = Guid.Empty;
        const uint TF_PROFILETYPE_KEYBOARDLAYOUT = 2, TF_IPPMF_FORSESSION = 0x20000000, TF_IPPMF_DONTCARECURRENTINPUTLANGUAGE = 0x4;
        return ((ITfInputProcessorProfileMgr)manager).ActivateProfile(
            TF_PROFILETYPE_KEYBOARDLAYOUT, langid, ref none, ref none, LoadKeyboardLayoutW(layoutId, 0), TF_IPPMF_FORSESSION | TF_IPPMF_DONTCARECURRENTINPUTLANGUAGE);
    }

    [StructLayout(LayoutKind.Sequential)] private struct KEYBDINPUT { public ushort wVk; public ushort wScan; public uint dwFlags; public uint time; public IntPtr dwExtraInfo; }
    [StructLayout(LayoutKind.Sequential)] private struct MOUSEINPUT { public int dx, dy; public uint mouseData, dwFlags, time; public IntPtr dwExtraInfo; }
    [StructLayout(LayoutKind.Explicit)] private struct InputUnion { [FieldOffset(0)] public KEYBDINPUT ki; [FieldOffset(0)] public MOUSEINPUT mi; }
    [StructLayout(LayoutKind.Sequential)] private struct INPUT { public uint type; public InputUnion u; }
    [DllImport("user32.dll", SetLastError = true)] private static extern uint SendInput(uint count, INPUT[] inputs, int size);
    [DllImport("user32.dll")] private static extern uint MapVirtualKeyW(uint code, uint mapType);

    /// <summary>Presses and releases a virtual key with real key events (what the system hands the input method).</summary>
    public static void Key(ushort vk)
    {
        ushort scan = (ushort)MapVirtualKeyW(vk, 0);
        INPUT[] events = new INPUT[2];
        events[0].type = 1; events[0].u.ki.wVk = vk; events[0].u.ki.wScan = scan;
        events[1].type = 1; events[1].u.ki.wVk = vk; events[1].u.ki.wScan = scan; events[1].u.ki.dwFlags = 2;   // KEYEVENTF_KEYUP
        SendInput(2, events, Marshal.SizeOf(typeof(INPUT)));
    }

    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hwnd, int command);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowTextW(IntPtr hwnd, System.Text.StringBuilder text, int count);
    [DllImport("user32.dll")] private static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);

    /// <summary>The title and handle of the window that has the keyboard.</summary>
    public static string Foreground()
    {
        IntPtr hwnd = GetForegroundWindow();
        var title = new System.Text.StringBuilder(256);
        GetWindowTextW(hwnd, title, 256);
        return hwnd.ToInt64() + " \"" + title + "\"";
    }

    [DllImport("user32.dll", SetLastError = true)] private static extern bool SystemParametersInfoW(uint action, uint param, IntPtr value, uint flags);

    /// <summary>Lets any process take the foreground at once (SPI_SETFOREGROUNDLOCKTIMEOUT = 0): the app was started by `wsb exec`, which Windows would not let become active. Fine in a disposable Sandbox.</summary>
    public static void AllowForeground()
    {
        SystemParametersInfoW(0x2001, 0, IntPtr.Zero, 2);
    }

    /// <summary>Brings a window of another process to the front: a tap on Alt first, which lets SetForegroundWindow through.</summary>
    public static void Raise(IntPtr hwnd)
    {
        keybd_event(0x12, 0, 0, UIntPtr.Zero);
        keybd_event(0x12, 0, 2, UIntPtr.Zero);
        ShowWindow(hwnd, 9);          // SW_RESTORE
        // Share the input queue of whoever has the foreground now, which makes SetForegroundWindow legal; SwitchToThisWindow ignores the lock anyway.
        uint ignored;
        uint foregroundThread = GetWindowThreadProcessId(GetForegroundWindow(), out ignored);
        uint thisThread = GetCurrentThreadId();
        if (foregroundThread != 0 && foregroundThread != thisThread) { AttachThreadInput(thisThread, foregroundThread, true); }
        BringWindowToTop(hwnd);
        SetForegroundWindow(hwnd);
        if (foregroundThread != 0 && foregroundThread != thisThread) { AttachThreadInput(thisThread, foregroundThread, false); }
        SwitchToThisWindow(hwnd, true);
    }

    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] private static extern bool AttachThreadInput(uint from, uint to, bool attach);
    [DllImport("user32.dll")] private static extern bool BringWindowToTop(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern void SwitchToThisWindow(IntPtr hwnd, bool altTab);

    public static void Screenshot(string path)
    {
        Rectangle bounds = Screen.PrimaryScreen.Bounds;
        using (var bitmap = new Bitmap(bounds.Width, bounds.Height))
        {
            using (var g = Graphics.FromImage(bitmap)) { g.CopyFromScreen(bounds.Location, Point.Empty, bounds.Size); }
            bitmap.Save(path, ImageFormat.Png);
        }
    }
}
'@ -ReferencedAssemblies System.Drawing, System.Windows.Forms

$weaselClsid = '{A3F4CDED-B1E9-41EE-9CA6-7B4D0DE6CB0A}'
$weaselProfile = '{3D02CAB6-2B8E-4781-BA20-1C9267529467}'
$pictures = Join-Path $root 'artifacts\spike'
New-Item -ItemType Directory -Force -Path $pictures | Out-Null

$weasel = Get-ChildItem 'C:\Program Files\Rime' -Directory -ErrorAction SilentlyContinue
Check 'Rime Weasel is installed (winget install Rime.Weasel)' ($null -ne $weasel)
if (-not $weasel) { exit $failures }

# If the app stops answering Windows ("Not Responding" in its title bar), say where its UI thread is: a stack read
# without stopping the process (cdb -pv, from the debugger copy tools\sandbox\install-debugger.ps1 makes in C:\debugger).
function Test-Responding($label) {
    $app.Refresh()
    if ($app.HasExited) { Write-Host "      the app has exited after $label (exit code $($app.ExitCode))"; return $false }
    if ($app.Responding) { return $true }
    Write-Host "      THE APP IS NOT RESPONDING after $label"
    if (Test-Path 'C:\debugger\cdb.exe') {
        $symbols = "srv*C:\symcache*https://msdl.microsoft.com/download/symbols;$root\app"
        $report = Join-Path $root 'results\hang-stacks.txt'
        & 'C:\debugger\cdb.exe' -pv -p $app.Id -y $symbols -c '~0kn 40;~*kn 12;q' 2>&1 | Out-File -FilePath $report -Encoding utf8
        Write-Host "      stacks of its threads are in $report; the UI thread:"
        Get-Content $report | Where-Object { $_ -match '^\s*[0-9a-f]{2}\s+[0-9a-f`]+\s+[0-9a-f`]+\s+\S+!' } | Select-Object -First 14 | ForEach-Object { Write-Host "        $_" }
    }
    return $false
}

$listener = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Loopback, 0)
$listener.Start()
$port = $listener.LocalEndpoint.Port
$app = $null
try {
    $app = Start-Process -FilePath $Exe -ArgumentList @('--connect', "raw://127.0.0.1:$port") -PassThru
    $accept = $listener.AcceptTcpClientAsync()
    Check 'the app connects to the server' ($accept.Wait(30000))
    $stream = $accept.Result.GetStream()
    $banner = [Text.Encoding]::UTF8.GetBytes("IME test: type pinyin below`r`n")
    $stream.Write($banner, 0, $banner.Length)
    Start-Sleep -Seconds 3

    function Read-Server {
        $got = New-Object System.Collections.Generic.List[byte]
        $chunk = New-Object byte[] 256
        while ($stream.DataAvailable) { $n = $stream.Read($chunk, 0, $chunk.Length); for ($i = 0; $i -lt $n; $i++) { $got.Add($chunk[$i]) } }
        return ,$got
    }

    # Baseline: with the plain keyboard, an injected key reaches the server. If this fails, the keys never reach the app.
    $app.Refresh()
    [ImeSmokeNative]::AllowForeground()
    [ImeSmokeNative]::Raise($app.MainWindowHandle)
    Start-Sleep -Milliseconds 800
    Focus-Terminal
    Write-Host ("      foreground window: " + [ImeSmokeNative]::Foreground() + "   (the app's is $($app.MainWindowHandle))")
    [ImeSmokeNative]::Key(0x58)      # x
    [ImeSmokeNative]::Key(0x0D)      # Enter: a raw session edits the line locally and sends it on Enter
    Start-Sleep -Seconds 1
    $plain = ((Read-Server).ToArray() | ForEach-Object { '{0:X2}' -f $_ }) -join ' '
    Check 'the app is responding before any IME is active' (Test-Responding 'the plain key')
    Check 'an injected key reaches the server before any IME is active' ($plain -eq '78 0D 0A') "(received: $plain)"

    Step 'activating the IME'
    $hr = [ImeSmokeNative]::Activate(0x0804, $weaselClsid, $weaselProfile)
    Step 'activated'
    Check 'the IME is made the session''s input method' ($hr -eq 0) ("(HRESULT 0x{0:X8})" -f $hr)
    Start-Sleep -Seconds 4          # the IME's server process starts on first use and may deploy its schemas
    Step 'slept after activation'
    if (-not (Test-Responding 'the IME was activated')) { Check 'the app is responding after the IME is activated' $false; exit $failures }
    [ImeSmokeNative]::Raise($app.MainWindowHandle)
    Focus-Terminal                  # click in the terminal again so that its window picks the input method up
    Write-Host ("      foreground window: " + [ImeSmokeNative]::Foreground())
    Start-Sleep -Seconds 1
    [ImeSmokeNative]::Screenshot((Join-Path $pictures 'ime-active.png'))

    # n i h a o: the composition "ni hao" with the first candidate U+4F60 U+597D
    foreach ($vk in 0x4E, 0x49, 0x48, 0x41, 0x4F) { [ImeSmokeNative]::Key($vk); Start-Sleep -Milliseconds 250 }
    Start-Sleep -Seconds 1
    Check 'the app is responding while the composition is open' (Test-Responding 'the composition')
    [ImeSmokeNative]::Screenshot((Join-Path $pictures 'ime-composing.png'))
    $beforeCommit = Read-Server
    Check 'nothing is sent to the server while the composition is still open' ($beforeCommit.Count -eq 0) ("(received: " + (($beforeCommit | ForEach-Object { '{0:X2}' -f $_ }) -join ' ') + ")")

    [ImeSmokeNative]::Key(0x20)      # Space chooses the first candidate
    Start-Sleep -Seconds 2
    [ImeSmokeNative]::Screenshot((Join-Path $pictures 'ime-committed.png'))
    [ImeSmokeNative]::Key(0x0D)      # Enter sends the line
    Start-Sleep -Seconds 1
    Check 'the app is responding after the commit' (Test-Responding 'the commit')
    $received = Read-Server
    $hex = ($received | ForEach-Object { '{0:X2}' -f $_ }) -join ' '
    $text = [Text.Encoding]::UTF8.GetString($received.ToArray())
    Check 'the committed text reaches the server as UTF-8 (U+4F60 U+597D) and CR LF' ($hex -eq 'E4 BD A0 E5 A5 BD 0D 0A') "(received: $hex = [$text])"
}
finally {
    if ($app -and -not $app.HasExited) { [void]$app.CloseMainWindow(); [void]$app.WaitForExit(10000); if (-not $app.HasExited) { $app.Kill() } }
    $listener.Stop()
    # back to the plain keyboard for whatever runs next in this Sandbox
    [void][ImeSmokeNative]::ActivateKeyboardLayout(0x0409, '00000409')
}
exit $failures
