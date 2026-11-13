<#
.SYNOPSIS
  End-to-end check of X11 forwarding in the real app: a saved SSH session with X11 forwarding on connects to the test SSH
  server, which asks for an X11 channel the way the server of a remote program that wants to draw would; PuTTY connects it
  to the real X server on the guest (VcXsrv) and the X server's answer to the connection setup comes back through the tunnel.

.DESCRIPTION
  Runs in Windows Sandbox (tools\run-in-sandbox.ps1 -Smoke x11), whose guest-run.ps1 installs VcXsrv (an X server) with
  winget and the test server's package with npm. The check starts VcXsrv on display :0 with access control off, starts the
  test SSH server (tools\ssh-test-server\server.js, which accepts x11-req and opens an x11 channel once the shell is up,
  and reports what the X server answered), and the app against a scratch registry with a saved session whose X11 settings
  are on (display 127.0.0.1:0). The user's real PuTTY settings are neither read nor written (TestRegistry.cs).

  The Windows OpenSSH server does not forward X11, so this is the toy server of the other SSH checks, a protocol-level test
  double: a pass says PuTTY's own X11 code works behind the engine's event loop and reaches a real X server, not that any
  particular server's X11 forwarding does.

  Prints PASS or FAIL per check; the exit code is the number of failures.
#>
param(
    [Parameter(Mandatory)] [string] $Exe
)

if ($env:USERNAME -ne 'WDAGUtilityAccount') {
    Write-Host 'This starts an X server and drives the app: run it in Windows Sandbox with tools\run-in-sandbox.ps1 -Smoke x11.'
    exit 99
}
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
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
# Opens a saved session from its context menu (a right click on its row, then Open): the Sessions panel has no Open button.
# The menu appears a moment after the click, so try again for a few seconds.
function Open-Session($name) {
    [void](Ui @('click', "Session:$name", '--right'))
    for ($i = 0; $i -lt 20; $i++) {
        try { [void](Ui @('invoke', 'Open', '--type', 'MenuItem')); return } catch { Start-Sleep -Milliseconds 500 }
    }
    [void](Ui @('invoke', 'Open', '--type', 'MenuItem'))
}

$vcxsrv = 'C:\Program Files\VcXsrv\vcxsrv.exe'
Check 'the X server (VcXsrv) is installed' (Test-Path $vcxsrv)
$modules = Join-Path $root 'artifacts\ssh-test-server\node_modules'
Check 'the test SSH server''s package is installed' (Test-Path (Join-Path $modules 'ssh2'))
if ($failures -gt 0) { exit $failures }

# --- the scratch "HKEY_CURRENT_USER" with the saved session
$leaf = "e2e-x11-$([guid]::NewGuid().ToString('N'))"
$scratch = "Software\PuttyModernTests\$leaf"
$workDir = Join-Path $env:TEMP "PuttyModernTests\$leaf"
New-Item -ItemType Directory -Force -Path $workDir | Out-Null

$xserver = $null
$server = $null
$app = $null
try {
    # --- the X server, on display :0 (port 6000), with access control off: any cookie is accepted.
    # It listens on every interface, which makes Windows Firewall ask whether to allow it: a dialog that then stays in the
    # foreground of the guest and makes every later check that needs the foreground (a right-click, a drag) refuse to act.
    # A rule that allows it first means the question is never asked.
    Get-NetFirewallRule -DisplayName 'puttymodern-vcxsrv' -ErrorAction SilentlyContinue | Remove-NetFirewallRule
    [void](New-NetFirewallRule -DisplayName 'puttymodern-vcxsrv' -Direction Inbound -Program $vcxsrv -Action Allow -Profile Any)
    Get-Process -Name vcxsrv -ErrorAction SilentlyContinue | Stop-Process -Force
    $xserver = Start-Process -FilePath $vcxsrv -ArgumentList @(':0', '-ac', '-nowgl', '-multiwindow', '-noreset') -PassThru
    $listening = $false
    $deadline = (Get-Date).AddSeconds(40)
    while ((Get-Date) -lt $deadline -and -not $listening) {
        try { $probe = New-Object System.Net.Sockets.TcpClient; $probe.Connect('127.0.0.1', 6000); $probe.Dispose(); $listening = $true } catch { Start-Sleep -Milliseconds 500 }
    }
    Check 'the X server listens on display :0 (port 6000)' $listening

    # --- the test SSH server; it ends when its stdin closes
    $psi = New-Object System.Diagnostics.ProcessStartInfo 'node'
    $psi.Arguments = '"' + (Join-Path $root 'tools\ssh-test-server\server.js') + '"'
    $psi.RedirectStandardInput = $true
    $psi.RedirectStandardOutput = $true
    $psi.UseShellExecute = $false
    $psi.CreateNoWindow = $true
    $psi.EnvironmentVariables['NODE_PATH'] = $modules
    $server = [System.Diagnostics.Process]::Start($psi)
    $info = $server.StandardOutput.ReadLine() | ConvertFrom-Json
    Check 'the test SSH server started' ($info.port -gt 0) "(port $($info.port))"
    $events = New-Object System.Collections.Concurrent.ConcurrentQueue[string]
    $reader = $server.StandardOutput.ReadLineAsync()
    function Server-Events {
        while ($script:reader.IsCompleted) {
            if ($script:reader.Result) { $script:events.Enqueue($script:reader.Result) }
            $script:reader = $script:server.StandardOutput.ReadLineAsync()
        }
        return $script:events.ToArray()
    }

    # --- a saved session with X11 forwarding on, to that server
    $key = "HKCU:\$scratch\Software\SimonTatham\PuTTY\Sessions\e2e%20x11"
    New-Item -Path $key -Force | Out-Null
    New-ItemProperty -Path $key -Name 'HostName' -Value '127.0.0.1' -PropertyType String | Out-Null
    New-ItemProperty -Path $key -Name 'PortNumber' -Value $info.port -PropertyType DWord | Out-Null
    New-ItemProperty -Path $key -Name 'Protocol' -Value 'ssh' -PropertyType String | Out-Null
    New-ItemProperty -Path $key -Name 'UserName' -Value 'test' -PropertyType String | Out-Null
    New-ItemProperty -Path $key -Name 'X11Forward' -Value 1 -PropertyType DWord | Out-Null
    New-ItemProperty -Path $key -Name 'X11Display' -Value '127.0.0.1:0' -PropertyType String | Out-Null

    $env:PUTTYMODERN_TEST_REGISTRY_ROOT = $scratch
    $app = Start-Process -FilePath $Exe -PassThru
    $env:PUTTYMODERN_TEST_REGISTRY_ROOT = $null
    Check 'the sessions panel lists the saved session' (Wait-Element 'Session:e2e x11' 40)
    Open-Session 'e2e x11'
    Check 'the host key dialog appears' (Wait-Element 'Connect Once' 40)
    [void](Ui @('invoke', 'Connect Once'))
    Check 'the password dialog appears' (Wait-Element 'PromptField0' 30)
    [void](Ui @('set-value', 'PromptField0', 'secret'))
    [void](Ui @('invoke', 'OK'))

    # --- what the server saw and what the X server answered through the tunnel
    $deadline = (Get-Date).AddSeconds(40)
    while ((Get-Date) -lt $deadline -and -not (@(Server-Events) -match '"event":"x11-reply"')) { Start-Sleep -Milliseconds 300 }
    $seen = @(Server-Events)
    Check 'the server was asked for X11 forwarding' (($seen -match '"event":"x11-req".*"ok":true').Count -gt 0) "($($seen -join ' '))"
    Check 'the server opened an X11 channel and PuTTY accepted it' (($seen -match '"event":"x11-open".*"ok":true').Count -gt 0)
    $reply = ($seen | Where-Object { $_ -match '"event":"x11-reply"' } | Select-Object -First 1)
    Check 'the real X server answered the connection setup through the tunnel: success' ($reply -and ($reply | ConvertFrom-Json).status -eq 1) "($reply)"
}
finally {
    $env:PUTTYMODERN_TEST_REGISTRY_ROOT = $null
    if ($app -and -not $app.HasExited) { $app.Kill() }
    if ($server) { try { $server.StandardInput.Close(); if (-not $server.WaitForExit(3000)) { $server.Kill() } } catch { } }
    Get-Process -Name vcxsrv -ErrorAction SilentlyContinue | Stop-Process -Force
    Get-NetFirewallRule -DisplayName 'puttymodern-vcxsrv' -ErrorAction SilentlyContinue | Remove-NetFirewallRule
    Remove-Item -Path $workDir -Recurse -Force -ErrorAction SilentlyContinue
    Remove-Item -Path "HKCU:\Software\PuttyModernTests\$leaf" -Recurse -Force -ErrorAction SilentlyContinue
}
exit $failures
