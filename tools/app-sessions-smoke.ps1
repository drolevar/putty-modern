<#
.SYNOPSIS
  End-to-end check of the Sessions panel in the real app: sessions saved in PuTTY's registry storage are
  listed, one opens and connects, and opening another replaces it after a question.

.DESCRIPTION
  Safe on a developer desktop: no key or mouse is injected (UI Automation patterns only), and the user's
  real PuTTY settings are neither read nor written. The app is started with PUTTYMODERN_TEST_REGISTRY_ROOT
  (see TestRegistry.cs), which points its HKEY_CURRENT_USER at a scratch key this script fills with
  sessions in PuTTY's own format and deletes at the end.

    tools\app-sessions-smoke.ps1 -Exe artifacts\publish\PuTTY.App\release_win-x64\PuTTY.App.exe

  Prints PASS or FAIL per check; the exit code is the number of failures.
#>
param(
    [Parameter(Mandatory)] [string] $Exe,
    [string] $Screenshot = ''
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
if (-not $Screenshot) { $Screenshot = Join-Path $root 'artifacts\spike\app-sessions.png' }
$esc = [string][char]27
$failures = 0
function Check($name, $ok, $detail = '') {
    if ($ok) { Write-Host "PASS  $name $detail" } else { Write-Host "FAIL  $name $detail"; $script:failures++ }
}
function Clean($lines) { ($lines | ForEach-Object { "$_" -replace ($esc + '\[[0-9;]*m'), '' }) -join "`n" }
function Ui { param([string[]] $UiArgs) Clean (& winapp ui @UiArgs -a PuTTY.App 2>&1) }
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

public static class SessionsSmokeNative
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

# --- the scratch "HKEY_CURRENT_USER" with sessions in PuTTY's format
$scratch = "Software\PuttyModernTests\e2e-sessions-$([guid]::NewGuid().ToString('N'))"
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
Save-Session 'Default Settings' 22

$realSessions = 'HKCU:\Software\SimonTatham\PuTTY\Sessions'
$realBefore = if (Test-Path $realSessions) { (Get-ChildItem $realSessions | Measure-Object).Count } else { 0 }

New-Item -ItemType Directory -Force -Path (Split-Path $Screenshot) | Out-Null
$app = $null
try {
    $env:PUTTYMODERN_TEST_REGISTRY_ROOT = $scratch
    $app = Start-Process -FilePath $Exe -PassThru
    $env:PUTTYMODERN_TEST_REGISTRY_ROOT = $null

    Check 'the sessions panel lists a saved session' (Wait-Element 'e2e alpha')
    Check 'it lists the other one too' ((Ui @('search', 'e2e beta')) -match 'Found [1-9]')
    Check '"Default Settings" is not offered as a session' ((Ui @('search', 'Default Settings')) -match 'Found 0')
    Check 'the panel says how many there are' ((Ui @('search', '2 saved sessions')) -match 'Found [1-9]')

    # --- open alpha: select it, press Open
    [void](Ui @('invoke', 'e2e alpha'))
    [void](Ui @('invoke', 'OpenSessionButton'))
    $accept = $alpha.AcceptTcpClientAsync()
    Check 'opening a saved session connects to its saved host and port' ($accept.Wait(20000))
    if ($accept.IsCompleted) {
        $stream = $accept.Result.GetStream()
        $bytes = [Text.Encoding]::UTF8.GetBytes("$esc[32mALPHA IS CONNECTED$esc[0m`r`n")
        $stream.Write($bytes, 0, $bytes.Length)
        Start-Sleep -Seconds 3
        & winapp ui screenshot -a PuTTY.App --output $Screenshot 2>&1 | Out-Null
        $green = if (Test-Path $Screenshot) { [SessionsSmokeNative]::CountColour($Screenshot, 0, 187, 0) } else { 0 }
        Check 'the terminal shows what the first session sends' ($green -gt 30) "($green green pixels)"
        Check 'the document tab is named after the session' ((Ui @('search', 'e2e alpha', '--type', 'TabItem')) -match 'Found [1-9]')

        # --- open beta while alpha is connected: the app asks first
        [void](Ui @('invoke', 'e2e beta'))
        [void](Ui @('invoke', 'OpenSessionButton'))
        Check 'opening a second session while one is connected asks first' (Wait-Element 'Close and open' 15)
        [void](Ui @('invoke', 'Close and open'))
        $acceptBeta = $beta.AcceptTcpClientAsync()
        Check 'the second session connects' ($acceptBeta.Wait(20000))
        if ($acceptBeta.IsCompleted) {
            $streamBeta = $acceptBeta.Result.GetStream()
            $bytes = [Text.Encoding]::UTF8.GetBytes("$esc[31mBETA IS CONNECTED$esc[0m`r`n")
            $streamBeta.Write($bytes, 0, $bytes.Length)
            Start-Sleep -Seconds 3
            $second = $Screenshot -replace '\.png$', '-second.png'
            & winapp ui screenshot -a PuTTY.App --output $second 2>&1 | Out-Null
            $red = [SessionsSmokeNative]::CountColour($second, 187, 0, 0)
            $greenAfter = [SessionsSmokeNative]::CountColour($second, 0, 187, 0)
            Check 'the terminal now shows the second session' ($red -gt 30) "($red red pixels)"
            Check 'and no longer the first' ($greenAfter -lt 5) "($greenAfter green pixels)"
        }

        # the first server noticed that its client went away
        $closed = $false
        $deadline = (Get-Date).AddSeconds(10)
        while ((Get-Date) -lt $deadline -and -not $closed) {
            try { $closed = ($stream.Read((New-Object byte[] 16), 0, 16) -eq 0) } catch { $closed = $true }
            if (-not $closed) { Start-Sleep -Milliseconds 200 }
        }
        Check 'the first connection was closed when it was replaced' $closed
    }
}
finally {
    $env:PUTTYMODERN_TEST_REGISTRY_ROOT = $null
    $closedCleanly = $false
    if ($app -and -not $app.HasExited) { [void]$app.CloseMainWindow(); [void]$app.WaitForExit(10000) }
    if ($app) {
        if ($app.HasExited) { $closedCleanly = ($app.ExitCode -eq 0) } else { $app.Kill() }
        Check 'the app exits cleanly when its window is closed' $closedCleanly "(exit code $(if ($app.HasExited) { $app.ExitCode } else { 'still running' }))"
    }
    $alpha.Stop(); $beta.Stop()
    $realAfter = if (Test-Path $realSessions) { (Get-ChildItem $realSessions | Measure-Object).Count } else { 0 }
    Check "the user's real PuTTY sessions were not touched" ($realBefore -eq $realAfter) "($realBefore before, $realAfter after)"
    Remove-Item -Path "HKCU:\Software\PuttyModernTests\$($scratch.Split('\')[-1])" -Recurse -Force -ErrorAction SilentlyContinue
}
exit $failures
