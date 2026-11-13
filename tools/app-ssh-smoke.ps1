<#
.SYNOPSIS
  End-to-end check of SSH in the real app: the app connects to the test SSH server, the host key and
  password dialogs are answered through UI Automation, and a typed command reaches the server.

.DESCRIPTION
  Safe on a developer desktop: no key and no mouse click is injected. The dialogs are driven with UI
  Automation patterns (Invoke on buttons, Value on the password box); typing is posted as thread
  messages to the app's UI thread, as in app-terminal-smoke.ps1. The host key dialog is answered with
  "Connect Once", so nothing is written to the user's PuTTY host key cache. (PuTTY's random seed file is
  rewritten, as it is by any PuTTY run.) Needs Node and the one-time install of the test server:

    tools\start-ssh-test-server.ps1 -Install
    tools\app-ssh-smoke.ps1 -Exe artifacts\publish\PuTTY.App\release_win-x64\PuTTY.App.exe

  Prints PASS or FAIL per check; the exit code is the number of failures.
#>
param(
    [Parameter(Mandatory)] [string] $Exe,
    [string] $Screenshot = ''
)

. (Join-Path $PSScriptRoot 'smoke-common.ps1')
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
if (-not $Screenshot) { $Screenshot = Join-Path $root 'artifacts\spike\app-ssh.png' }
$esc = [string][char]27
$failures = 0
function Check($name, $ok, $detail = '') {
    if ($ok) { Write-Host "PASS  $name $detail" } else { Write-Host "FAIL  $name $detail"; $script:failures++ }
}
function Clean($lines) { ($lines | ForEach-Object { "$_" -replace ($esc + '\[[0-9;]*m'), '' }) -join "`n" }
function Ui { param([string[]] $UiArgs) Clean (& winapp ui @UiArgs -a PuTTY.App 2>&1) }

# Waits until the element is in the app's UI Automation tree.
function Wait-Element($selector, $seconds = 30) {
    $deadline = (Get-Date).AddSeconds($seconds)
    while ((Get-Date) -lt $deadline) {
        if ((Ui @('search', $selector)) -match 'Found [1-9]') { return $true }
        Start-Sleep -Milliseconds 400
    }
    return $false
}

Add-Type -TypeDefinition @'
using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

public static class SshSmokeNative
{
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);
    [DllImport("user32.dll", SetLastError = true)] public static extern bool PostThreadMessageW(uint threadId, uint msg, UIntPtr wParam, IntPtr lParam);

    public static int CountColour(string path, int r, int g, int b)
    {
        using (var bmp = new Bitmap(path))
        {
            var data = bmp.LockBits(new Rectangle(0, 0, bmp.Width, bmp.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            try
            {
                int count = 0;
                byte[] row = new byte[data.Stride];
                for (int y = 0; y < bmp.Height; y++)
                {
                    Marshal.Copy(IntPtr.Add(data.Scan0, y * data.Stride), row, 0, data.Stride);
                    for (int x = 0; x < bmp.Width; x++)
                        if (row[x * 4 + 2] == r && row[x * 4 + 1] == g && row[x * 4] == b) count++;
                }
                return count;
            }
            finally { bmp.UnlockBits(data); }
        }
    }
}
'@ -ReferencedAssemblies System.Drawing

$modules = Join-Path $root 'artifacts\ssh-test-server\node_modules'
if (-not (Test-Path (Join-Path $modules 'ssh2'))) { Write-Host 'The test SSH server is not installed: run tools\start-ssh-test-server.ps1 -Install'; exit 99 }
New-Item -ItemType Directory -Force -Path (Split-Path $Screenshot) | Out-Null
Remove-Item $Screenshot -ErrorAction SilentlyContinue

# The test server; it ends when its stdin closes.
# Node: on PATH on a developer machine, or the copy staged next to the scripts in Windows Sandbox.
$nodeExe = if (Test-Path (Join-Path $root 'artifacts\node\node.exe')) { Join-Path $root 'artifacts\node\node.exe' } else { 'node' }$psi = New-Object System.Diagnostics.ProcessStartInfo $nodeExe
$psi.Arguments = '"' + (Join-Path $root 'tools\ssh-test-server\server.js') + '"'
$psi.RedirectStandardInput = $true
$psi.RedirectStandardOutput = $true
$psi.UseShellExecute = $false
$psi.CreateNoWindow = $true
$psi.EnvironmentVariables['NODE_PATH'] = $modules
$server = [System.Diagnostics.Process]::Start($psi)
$serverEvents = New-Object System.Collections.Concurrent.ConcurrentQueue[string]
$info = $server.StandardOutput.ReadLine() | ConvertFrom-Json
$reader = $server.StandardOutput.ReadLineAsync()
$app = $null
try {
    Check 'the test server started' ($info.port -gt 0) "(port $($info.port), $($info.sha256))"

    $app = Start-Process -FilePath $Exe -ArgumentList @('--connect', "ssh://test@127.0.0.1:$($info.port)") -PassThru

    # --- the host key question
    Check 'the host key dialog appears' (Wait-Element 'Connect Once')
    $dialogText = Ui @('inspect', '--depth', '14')
    Check "the dialog shows the server's fingerprint" ($dialogText -match [regex]::Escape($info.sha256))
    Check 'the dialog offers Accept and Cancel as well' (($dialogText -match 'Accept') -and ($dialogText -match 'Cancel'))
    [void](Ui @('invoke', 'Connect Once'))

    # --- the password question
    Check 'the password dialog appears' (Wait-Element 'PromptField0')
    $set = Ui @('set-value', 'PromptField0', 'secret')
    Check 'the password box takes a value through UI Automation' ($set -match 'Set value') "($($set.Trim()))"
    [void](Ui @('invoke', 'OK'))

    # --- logged in: what the server saw, as it reports it
    function Server-Events {
        $list = @()
        while ($true) {
            if ($reader.IsCompleted) {
                if ($reader.Result) { $script:serverEvents.Enqueue($reader.Result) }
                $script:reader = $server.StandardOutput.ReadLineAsync()
            } else { break }
        }
        return $script:serverEvents.ToArray()
    }
    $deadline = (Get-Date).AddSeconds(30)
    while ((Get-Date) -lt $deadline -and -not ((Server-Events) -contains '{"event":"shell"}')) { Start-Sleep -Milliseconds 300 }
    $events = Server-Events
    Check 'the server saw a successful login' (($events | Where-Object { $_ -match '"event":"auth".*"ok":true' }).Count -gt 0) "($($events -join ' '))"
    Check 'the server started a shell' ($events -contains '{"event":"shell"}')

    # --- the session details panel reads what PuTTY says about the connection
    Check 'the details panel names the key exchange' ((Ui @('get-property', 'SessionDetailsKeyExchange')) -match 'Name:\s*\S')
    Check 'and the cipher' ((Ui @('get-property', 'SessionDetailsCipher')) -match 'Name:\s*\S')
    $shownFingerprint = Ui @('get-property', 'SessionDetailsFingerprint')
    Check 'and the host key fingerprint, which is the one the server has' ($shownFingerprint -match [regex]::Escape($info.sha256)) "($($shownFingerprint -replace '\s+', ' '))"
    Check 'with a button that copies it' ((Ui @('search', 'CopyFingerprintButton')) -match 'Found [1-9]')
    Check 'the connection rows say where it connects' (((Ui @('get-property', 'SessionDetailsHost')) -match '127\.0\.0\.1'))

    # --- type a command (thread messages, as Windows would post them)
    Start-Sleep -Seconds 1
    Focus-Terminal       # the terminal takes keys only with the keyboard focus (tools\smoke-common.ps1)
    $app.Refresh()
    $pid2 = 0
    $uiThread = [SshSmokeNative]::GetWindowThreadProcessId($app.MainWindowHandle, [ref]$pid2)
    foreach ($ch in 'echo hi'.ToCharArray()) {
        [void][SshSmokeNative]::PostThreadMessageW($uiThread, 0x0102, [UIntPtr]::new([uint64][int]$ch), [IntPtr]::new(1))   # WM_CHAR
    }
    $down = [IntPtr]::new(1 -bor (0x1C -shl 16))
    $up = [IntPtr]::new([int64]([uint32](1 -bor (0x1C -shl 16) -bor (1 -shl 30)) -bor 0x80000000))
    [void][SshSmokeNative]::PostThreadMessageW($uiThread, 0x0100, [UIntPtr]::new(13), $down)   # WM_KEYDOWN VK_RETURN
    [void][SshSmokeNative]::PostThreadMessageW($uiThread, 0x0101, [UIntPtr]::new(13), $up)     # WM_KEYUP
    $deadline = (Get-Date).AddSeconds(15)
    while ((Get-Date) -lt $deadline -and -not ((Server-Events) -contains '{"event":"command","text":"echo hi"}')) { Start-Sleep -Milliseconds 300 }
    Check 'the typed command reaches the server' ((Server-Events) -contains '{"event":"command","text":"echo hi"}') "($((Server-Events) -join ' '))"

    # --- and the terminal shows the server's welcome text and the answer
    Start-Sleep -Seconds 2
    & winapp ui screenshot -a PuTTY.App --output $Screenshot 2>&1 | Out-Null
    Check 'a screenshot was taken' (Test-Path $Screenshot)
    if (Test-Path $Screenshot) {
        $grey = [SshSmokeNative]::CountColour($Screenshot, 187, 187, 187)
        Check "the server's text is drawn in the terminal" ($grey -gt 100) "($grey pixels of the default text colour)"
    }
}
finally {
    $closedCleanly = $false
    if ($app -and -not $app.HasExited) { [void]$app.CloseMainWindow(); [void]$app.WaitForExit(10000) }
    if ($app) {
        if ($app.HasExited) { $closedCleanly = ($app.ExitCode -eq 0) } else { $app.Kill() }
        Check 'the app exits cleanly when its window is closed' $closedCleanly "(exit code $(if ($app.HasExited) { $app.ExitCode } else { 'still running' }))"
    }
    try { $server.StandardInput.Close(); if (-not $server.WaitForExit(3000)) { $server.Kill() } } catch { }
}
exit $failures
