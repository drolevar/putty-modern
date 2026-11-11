<#
.SYNOPSIS
  End-to-end check that the workspace survives a restart in the real app: two sessions open in two tabs, the
  Connection log panel closed, the app closed and started again; the layout is as it was and both sessions are open
  again and connected.

.DESCRIPTION
  Run it in Windows Sandbox with tools\run-in-sandbox.ps1 -Smoke workspace. Nothing is injected into the desktop (UI
  Automation patterns only), and the user's real PuTTY settings and workspace files are neither read nor written: the
  app is started with PUTTYMODERN_TEST_REGISTRY_ROOT (see TestRegistry.cs), which points its registry at a scratch key
  and its workspace files (folders, layout) at a scratch folder under %TEMP%.

  Prints PASS or FAIL per check; the exit code is the number of failures.
#>
param(
    [Parameter(Mandatory)] [string] $Exe,
    [string] $Screenshot = '',
    [switch] $AllowHostDesktop
)

if ($env:USERNAME -ne 'WDAGUtilityAccount' -and -not $AllowHostDesktop) {
    Write-Host 'This starts the app and drives its windows. Run it in Windows Sandbox with tools\run-in-sandbox.ps1, or pass -AllowHostDesktop to use this desktop.'
    exit 99
}
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
if (-not $Screenshot) { $Screenshot = Join-Path $root 'artifacts\spike\app-workspace.png' }
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
function Is-Closed($client) { $client.Client.Poll(300000, [System.Net.Sockets.SelectMode]::SelectRead) -and $client.Client.Available -eq 0 }

Add-Type -TypeDefinition @'
using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

public static class WorkspaceSmokeNative
{
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
    return $process
}
function Stop-App($process) {
    if ($process -and -not $process.HasExited) { [void]$process.CloseMainWindow(); [void]$process.WaitForExit(15000) }
    if ($process -and -not $process.HasExited) { $process.Kill(); return $false }
    return ($process.ExitCode -eq 0)
}

$app = $null
try {
    # --- the first run: two sessions, and the Connection log panel closed
    $app = Start-App
    Check 'the sessions panel lists the saved sessions' (Wait-Element 'Session:e2e alpha' 40)
    $acceptAlpha = $alpha.AcceptTcpClientAsync()
    [void](Ui @('invoke', 'Session:e2e alpha'))
    [void](Ui @('invoke', 'OpenSessionButton'))
    Check 'the first session connects' ($acceptAlpha.Wait(20000))
    $acceptBeta = $beta.AcceptTcpClientAsync()
    [void](Ui @('invoke', 'Session:e2e beta'))
    [void](Ui @('invoke', 'OpenSessionButton'))
    Check 'the second session connects' ($acceptBeta.Wait(20000))
    Check 'there is a tab for each' ((Wait-Element 'e2e beta' 15 @('--type', 'TabItem')) -and (Ui @('search', 'e2e alpha', '--type', 'TabItem')) -match 'Found [1-9]')

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
        # one tab is showing; the other is one click away. Both terminals must have drawn what their server sent.
        $first = $Screenshot -replace '\.png$', '-first.png'
        & winapp ui screenshot -a PuTTY.App --output $first 2>&1 | Out-Null
        $greenFirst = [WorkspaceSmokeNative]::CountColour($first, 0, 187, 0)
        $redFirst = [WorkspaceSmokeNative]::CountColour($first, 187, 0, 0)
        Check 'the tab that shows draws its server''s text' (($greenFirst -gt 30) -ne ($redFirst -gt 30)) "($greenFirst green, $redFirst red pixels)"
        [void](Ui @('invoke', $(if ($greenFirst -gt 30) { 'e2e beta' } else { 'e2e alpha' }), '--type', 'TabItem'))
        Start-Sleep -Seconds 3
        $other = $Screenshot -replace '\.png$', '-other.png'
        & winapp ui screenshot -a PuTTY.App --output $other 2>&1 | Out-Null
        $greenOther = [WorkspaceSmokeNative]::CountColour($other, 0, 187, 0)
        $redOther = [WorkspaceSmokeNative]::CountColour($other, 187, 0, 0)
        Check 'and the other tab draws its own' (($greenFirst -gt 30) -eq ($redOther -gt 30) -and ($greenOther -gt 30) -ne ($greenFirst -gt 30)) "($greenOther green, $redOther red pixels)"
    }

    Check 'the second run exits cleanly too' (Stop-App $app)
    $app = $null
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
