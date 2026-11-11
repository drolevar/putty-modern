<#
.SYNOPSIS
  End-to-end check that the workspace survives a restart in the real app: two sessions open in two tabs, one tab
  dragged into a group of its own below the other (a split), the Connection log panel closed, the app closed and
  started again; the layout is as it was (still split, the panel still closed) and both sessions are open again and
  connected.

.DESCRIPTION
  Run it in Windows Sandbox with tools\run-in-sandbox.ps1 -Smoke workspace: it drags a tab with the real mouse, so it
  refuses to run anywhere else. The user's real PuTTY settings and workspace files are neither read nor written: the
  app is started with PUTTYMODERN_TEST_REGISTRY_ROOT (see TestRegistry.cs), which points its registry at a scratch key
  and its workspace files (folders, layout) at a scratch folder under %TEMP%.

  Prints PASS or FAIL per check; the exit code is the number of failures.
#>
param(
    [Parameter(Mandatory)] [string] $Exe,
    [string] $Screenshot = ''
)

if ($env:USERNAME -ne 'WDAGUtilityAccount') {
    Write-Host 'This drags a tab with the real mouse: run it in Windows Sandbox with tools\run-in-sandbox.ps1 -Smoke workspace.'
    exit 99
}
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
if (-not $Screenshot) { $Screenshot = Join-Path $root 'artifacts\spike\app-workspace.png' }
. (Join-Path $PSScriptRoot 'smoke-common.ps1')       # Attach-Debugger (run-in-sandbox.ps1 -AttachDebugger)
$esc = [string][char]27
$failures = 0
function Check($name, $ok, $detail = '') {
    if ($ok) { Write-Host "PASS  $name $detail" } else { Write-Host "FAIL  $name $detail"; $script:failures++ }
}
function Clean($lines) { ($lines | ForEach-Object { "$_" -replace ($esc + '\[[0-9;]*m'), '' }) -join "`n" }
function Ui { param([string[]] $UiArgs) Clean (& winapp ui @UiArgs -a PuTTY.App 2>&1) }
function Wait-Element($selector, $seconds = 30, [string[]] $Extra = @()) {
    $deadline = (Get-Date).AddSeconds($seconds)
    while ((Get-Date) -lt $deadline) {
        if ((Ui (@('search', $selector) + $Extra)) -match 'Found [1-9]') { return $true }
        Start-Sleep -Milliseconds 400
    }
    return $false
}
# Selects a saved session and presses Open. The button is enabled a moment after the selection (slower still with a
# debugger attached), and UI Automation refuses to press a disabled one, so try again for a few seconds.
function Open-Session($name) {
    [void](Ui @('invoke', "Session:$name"))
    for ($i = 0; $i -lt 20; $i++) {
        try { [void](Ui @('invoke', 'OpenSessionButton')); return } catch { Start-Sleep -Milliseconds 500 }
    }
    [void](Ui @('invoke', 'OpenSessionButton'))
}
function Is-Closed($client) { $client.Client.Poll(300000, [System.Net.Sockets.SelectMode]::SelectRead) -and $client.Client.Available -eq 0 }

Add-Type -AssemblyName System.Windows.Forms
Add-Type -TypeDefinition @'
using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

public static class WorkspaceSmokeNative
{
    [DllImport("user32.dll")] public static extern void mouse_event(uint flags, uint dx, uint dy, int data, UIntPtr extra);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
    [DllImport("user32.dll")] public static extern void SwitchToThisWindow(IntPtr hwnd, bool altTab);
    [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
    public const uint MOVE = 1, ABSOLUTE = 0x8000, LEFTDOWN = 2, LEFTUP = 4;

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

[void][WorkspaceSmokeNative]::SetProcessDPIAware()
function Move-To($x, $y) {
    $screen = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
    $dx = [uint32][Math]::Ceiling($x * 65535.0 / ($screen.Width - 1))
    $dy = [uint32][Math]::Ceiling($y * 65535.0 / ($screen.Height - 1))
    [WorkspaceSmokeNative]::mouse_event([WorkspaceSmokeNative]::MOVE -bor [WorkspaceSmokeNative]::ABSOLUTE, $dx, $dy, 0, [UIntPtr]::Zero)
    Start-Sleep -Milliseconds 60
}

# Drags a tab into a group of its own below the others: pick up its header, take it to the middle of the document area,
# where the docking library shows a compass of drop guides, and let go over the one that points down (measured in the
# Sandbox: it is about 27 px below the pointer when the pointer is at 40% of the window's height).
function Split-Below($process, $tabName) {
    $process.Refresh()
    [WorkspaceSmokeNative]::SwitchToThisWindow($process.MainWindowHandle, $true)
    Start-Sleep -Seconds 1
    $rect = New-Object WorkspaceSmokeNative+RECT
    [void][WorkspaceSmokeNative]::GetWindowRect($process.MainWindowHandle, [ref]$rect)
    $tabInfo = Ui @('get-property', $tabName, '--type', 'TabItem')
    if ($tabInfo -notmatch 'BoundingRectangle:\s*(-?\d+),(-?\d+),(\d+),(\d+)') { return $false }
    $tx = [int]$Matches[1] + [int]$Matches[3] / 2
    $ty = [int]$Matches[2] + [int]$Matches[4] / 2
    $cx = [int](($rect.Left + $rect.Right) / 2)
    $cy = [int]($rect.Top + ($rect.Bottom - $rect.Top) * 0.4)
    Move-To $tx $ty
    [WorkspaceSmokeNative]::mouse_event([WorkspaceSmokeNative]::LEFTDOWN, 0, 0, 0, [UIntPtr]::Zero)
    Start-Sleep -Milliseconds 120
    for ($i = 1; $i -le 14; $i++) { Move-To ($tx + ($cx - $tx) * $i / 14) ($ty + ($cy - $ty) * $i / 14) }
    Start-Sleep -Milliseconds 900
    Move-To ($cx + 2) ($cy + 2)
    Start-Sleep -Milliseconds 700
    Move-To ($cx + 3) ($cy + 27)
    Start-Sleep -Milliseconds 700
    [WorkspaceSmokeNative]::mouse_event([WorkspaceSmokeNative]::LEFTUP, 0, 0, 0, [UIntPtr]::Zero)
    Start-Sleep -Seconds 2
    return $true
}

# --- the scratch "HKEY_CURRENT_USER" with two saved sessions
$leaf = "e2e-workspace-$([guid]::NewGuid().ToString('N'))"
$scratch = "Software\PuttyModernTests\$leaf"
$sessionsKey = "HKCU:\$scratch\Software\SimonTatham\PuTTY\Sessions"
function Save-Session($name, $port) {
    $key = Join-Path $sessionsKey ($name -replace ' ', '%20')
    New-Item -Path $key -Force | Out-Null
    New-ItemProperty -Path $key -Name 'HostName' -Value '127.0.0.1' -PropertyType String | Out-Null
    New-ItemProperty -Path $key -Name 'PortNumber' -Value $port -PropertyType DWord | Out-Null
    New-ItemProperty -Path $key -Name 'Protocol' -Value 'raw' -PropertyType String | Out-Null
}
$alpha = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Loopback, 0); $alpha.Start()
$beta = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Loopback, 0); $beta.Start()
Save-Session 'e2e alpha' $alpha.LocalEndpoint.Port
Save-Session 'e2e beta' $beta.LocalEndpoint.Port

$workspaceDir = Join-Path $env:TEMP "PuttyModernTests\$leaf"
$layoutFile = Join-Path $workspaceDir 'layout.json'
$realLayout = Join-Path $env:APPDATA 'PuTTY Modern\layout.json'
$realLayoutBefore = if (Test-Path $realLayout) { (Get-Item $realLayout).LastWriteTimeUtc.Ticks } else { 0 }
New-Item -ItemType Directory -Force -Path (Split-Path $Screenshot) | Out-Null

function Start-App {
    $env:PUTTYMODERN_TEST_REGISTRY_ROOT = $scratch
    $process = Start-Process -FilePath $Exe -PassThru
    $env:PUTTYMODERN_TEST_REGISTRY_ROOT = $null
    $script:debugger = Attach-Debugger $process
    return $process
}
function Stop-App($process) {
    if ($process -and -not $process.HasExited) { [void]$process.CloseMainWindow(); [void]$process.WaitForExit($(if ($script:debugger) { 120000 } else { 15000 })) }
    if ($process -and -not $process.HasExited) { Write-Host '      the app did not exit within 15 seconds of its window being closed; it was killed'; $process.Kill(); return $false }
    Write-Host "      the app exited with code $($process.ExitCode)"
    return ($process.ExitCode -eq 0)
}

# What an unhandled exception in the app says (CrashLog.cs); the guest is disposable, so the file may be removed.
$crashLog = Join-Path $env:LOCALAPPDATA 'PuTTY Modern\crash.log'
Remove-Item $crashLog -ErrorAction SilentlyContinue
$app = $null
try {
    # --- the first run: two sessions, and the Connection log panel closed
    $app = Start-App
    Check 'the sessions panel lists the saved sessions' (Wait-Element 'Session:e2e alpha' 40)
    $acceptAlpha = $alpha.AcceptTcpClientAsync()
    Open-Session 'e2e alpha'
    Check 'the first session connects' ($acceptAlpha.Wait(20000))
    $acceptBeta = $beta.AcceptTcpClientAsync()
    Open-Session 'e2e beta'
    Check 'the second session connects' ($acceptBeta.Wait(20000))
    Check 'there is a tab for each' ((Wait-Element 'e2e beta' 15 @('--type', 'TabItem')) -and (Ui @('search', 'e2e alpha', '--type', 'TabItem')) -match 'Found [1-9]')

    Check 'a tab can be dragged into a group of its own below the other' (Split-Below $app 'e2e beta')
    $bytes = [Text.Encoding]::UTF8.GetBytes("$esc[32mALPHA SPLIT$esc[0m`r`n")
    $acceptAlpha.Result.GetStream().Write($bytes, 0, $bytes.Length)
    $bytes = [Text.Encoding]::UTF8.GetBytes("$esc[31mBETA SPLIT$esc[0m`r`n")
    $acceptBeta.Result.GetStream().Write($bytes, 0, $bytes.Length)
    Start-Sleep -Seconds 3
    $beforeRestart = $Screenshot -replace '\.png$', '-split.png'
    & winapp ui screenshot -a PuTTY.App --output $beforeRestart 2>&1 | Out-Null
    $greenSplit = [WorkspaceSmokeNative]::CountColour($beforeRestart, 0, 187, 0)
    $redSplit = [WorkspaceSmokeNative]::CountColour($beforeRestart, 187, 0, 0)
    Check 'after the split both terminals are on screen at once' (($greenSplit -gt 30) -and ($redSplit -gt 30)) "($greenSplit green, $redSplit red pixels)"

    $tree = Ui @('inspect', '--depth', '16')
    $logClose = if ($tree -match 'Text "Connection log"[\s\S]*?(btn-partclosebutton-[0-9a-f]+)') { $Matches[1] } else { $null }
    Check 'the Connection log panel has a close button' ($null -ne $logClose)
    if ($logClose) { [void](Ui @('invoke', $logClose)) }
    Start-Sleep -Seconds 1
    Check 'the Connection log panel is closed' ((Ui @('search', 'ConnectionLogList')) -match 'Found 0')

    Check 'the first run exits cleanly when its window is closed' (Stop-App $app)
    Check 'closing the app ended both connections' ((Is-Closed $acceptAlpha.Result) -and (Is-Closed $acceptBeta.Result))

    # --- what was saved
    Check 'the workspace was saved' (Test-Path $layoutFile) "($layoutFile)"
    $state = if (Test-Path $layoutFile) { Get-Content $layoutFile -Raw | ConvertFrom-Json } else { $null }
    Check 'it holds the layout' ($state -and $state.layout -and $state.layout.Length -gt 100)
    Check 'and both sessions, by their saved names' ($state -and @($state.sessions).Count -eq 2 -and (@($state.sessions | ForEach-Object savedName) -contains 'e2e alpha') -and (@($state.sessions | ForEach-Object savedName) -contains 'e2e beta'))

    # --- the second run: the same layout, the same sessions, connected again
    $acceptAlpha2 = $alpha.AcceptTcpClientAsync()
    $acceptBeta2 = $beta.AcceptTcpClientAsync()
    $app = Start-App
    Check 'the first session is opened again at start' ($acceptAlpha2.Wait(40000))
    Check 'and the second' ($acceptBeta2.Wait(40000))
    Check 'each has its tab' ((Wait-Element 'e2e alpha' 20 @('--type', 'TabItem')) -and (Ui @('search', 'e2e beta', '--type', 'TabItem')) -match 'Found [1-9]')
    Check 'the Connection log panel is still closed' ((Ui @('search', 'ConnectionLogList')) -match 'Found 0')
    Check 'the status bar counts both' (Wait-Element '2 active sessions' 15)

    if ($acceptAlpha2.IsCompleted -and $acceptBeta2.IsCompleted) {
        $bytes = [Text.Encoding]::UTF8.GetBytes("$esc[32mALPHA RESTORED$esc[0m`r`n")
        $acceptAlpha2.Result.GetStream().Write($bytes, 0, $bytes.Length)
        $bytes = [Text.Encoding]::UTF8.GetBytes("$esc[31mBETA RESTORED$esc[0m`r`n")
        $acceptBeta2.Result.GetStream().Write($bytes, 0, $bytes.Length)
        Start-Sleep -Seconds 3
        # In a split both groups show a tab, so both servers' text is on screen at once; with one group only one would be.
        $restored = $Screenshot -replace '\.png$', '-restored.png'
        & winapp ui screenshot -a PuTTY.App --output $restored 2>&1 | Out-Null
        $greenBack = [WorkspaceSmokeNative]::CountColour($restored, 0, 187, 0)
        $redBack = [WorkspaceSmokeNative]::CountColour($restored, 187, 0, 0)
        Check 'the split is back: both terminals are on screen at once, each with its server''s text' (($greenBack -gt 30) -and ($redBack -gt 30)) "($greenBack green, $redBack red pixels)"
    }
    Check 'the second run exits cleanly too' (Stop-App $app)
    $app = $null
    if (Test-Path $crashLog) { Write-Host '      --- crash.log:'; Get-Content $crashLog | ForEach-Object { Write-Host "      $_" } }
}
finally {
    $env:PUTTYMODERN_TEST_REGISTRY_ROOT = $null
    if ($app -and -not $app.HasExited) { $app.Kill() }
    $alpha.Stop(); $beta.Stop()
    $realLayoutAfter = if (Test-Path $realLayout) { (Get-Item $realLayout).LastWriteTimeUtc.Ticks } else { 0 }
    Check "the user's real workspace layout was not touched" ($realLayoutBefore -eq $realLayoutAfter)
    Remove-Item -Path $workspaceDir -Recurse -Force -ErrorAction SilentlyContinue
    Remove-Item -Path "HKCU:\Software\PuttyModernTests\$leaf" -Recurse -Force -ErrorAction SilentlyContinue
}
exit $failures
