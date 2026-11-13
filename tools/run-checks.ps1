<#
.SYNOPSIS
  Runs the end-to-end checks of the real app on this machine's own desktop (no Windows Sandbox).

.DESCRIPTION
  Each check (tools\app-<name>-smoke.ps1) starts the app, drives its windows through UI Automation (the winapp CLI,
  which must be on PATH) and reads screenshots, so while it runs the app's windows are on your desktop and the
  checks that click or drag use the real mouse. Output goes to the console and to artifacts\check-results\<name>.txt;
  the exit code is the number of FAIL lines (plus one for a check that died without saying why).

    tools\run-checks.ps1                                   # the eight that change nothing on the machine
    tools\run-checks.ps1 -Smoke settings,ssh
    tools\run-checks.ps1 -Smoke all                        # also openssh, agent, x11 and mouse
    tools\run-checks.ps1 -AppDir artifacts\bin\PuTTY.App\release_win-x64    # the non-AOT build

  Before it starts it makes sure no PuTTY.App is running: the checks address the app by its process name, so another
  instance (yours, with its sessions) would be driven or stopped. Close it first.

  What each group does to the machine:
    terminal ssh sessions settings workspace appearance features crash
        Start the app and, for ssh, settings and x11, a toy SSH server written in Node (npm package ssh2, installed
        into artifacts\ssh-test-server). The app's settings go to a scratch registry key and %TEMP% folder, never to
        your PuTTY sessions or %APPDATA%\PuTTY Modern. workspace (and mouse) move the real mouse: hands off.
    openssh agent    (need an elevated PowerShell)
        Use the Windows OpenSSH server (sshd on port 22) with a temporary local account (marked in its description,
        random password, removed at the end); agent makes it an administrator and moves the administrators'
        authorized keys file aside (put back at the end) and starts Pageant with a test key. Refuse to run if a
        Pageant is already running or an account of that name is not theirs.
    x11              (needs an elevated PowerShell)
        Starts VcXsrv (an X server, access control off) with a firewall rule that lets in this machine only, removed
        at the end. Refuses to run if a VcXsrv is already running.
    mouse
        Drags, double-clicks and turns the wheel with the real mouse.

  What is missing is reported with the command that installs it. With -InstallMissing the script installs it with
  winget (OpenSSH, PuTTY for Pageant, VcXsrv: they stay installed) and starts sshd if it is stopped (and stops it
  again at the end).
#>
param(
    [string[]] $Smoke = @('terminal', 'ssh', 'sessions', 'settings', 'workspace', 'appearance', 'features', 'crash'),
    [string] $AppDir = 'artifacts\publish\PuTTY.App\release_win-x64',
    [switch] $InstallMissing
)

$ErrorActionPreference = 'Stop'
$Smoke = @($Smoke | ForEach-Object { $_ -split ',' } | Where-Object { $_ })        # `powershell -File x.ps1 -Smoke a,b` arrives as the one string "a,b"
if ($Smoke -contains 'all') { $Smoke = @('terminal', 'ssh', 'sessions', 'settings', 'workspace', 'appearance', 'features', 'crash', 'openssh', 'agent', 'x11', 'mouse') }
$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$appFolder = if ([IO.Path]::IsPathRooted($AppDir)) { $AppDir } else { Join-Path $root $AppDir }
$exe = Join-Path $appFolder 'PuTTY.App.exe'
$results = Join-Path $root 'artifacts\check-results'
New-Item -ItemType Directory -Force -Path $results | Out-Null

if (-not (Test-Path $exe)) {
    Write-Host "No PuTTY.App.exe in $appFolder. Publish first: dotnet publish src\PuTTY.App -c Release -r win-x64 (or, for the non-AOT build, dotnet build src\PuTTY.App -c Release -p:PublishAot=false)."
    exit 97
}
if (-not (Get-Command winapp -ErrorAction SilentlyContinue)) {
    Write-Host 'The winapp CLI is not on PATH: the checks drive the app with it (winapp ui ...). Install it, then run this again.'
    exit 93
}
$running = Get-Process -Name 'PuTTY.App' -ErrorAction SilentlyContinue
if ($running) {
    Write-Host "PuTTY.App is already running (pid $($running[0].Id)). The checks address the app by its process name and would drive or stop it: close it and run this again."
    exit 98
}

$unknown = @($Smoke | Where-Object { $_ -notin 'terminal', 'ssh', 'sessions', 'settings', 'workspace', 'appearance', 'features', 'crash', 'openssh', 'agent', 'x11', 'mouse' })
if ($unknown.Count -gt 0) { Write-Host "No such check: $($unknown -join ', ')"; exit 98 }

$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
$sshdExe = 'C:\Program Files\OpenSSH\sshd.exe'
$pageantExe = 'C:\Program Files\PuTTY\pageant.exe'
$vcxsrvExe = 'C:\Program Files\VcXsrv\vcxsrv.exe'
$startedSshd = $false

# Makes ready what a check needs. Returns a reason when it cannot be (the check is then skipped, not failed).
function Prepare([string] $name) {
    if ($name -in 'ssh', 'settings', 'x11') {
        if (-not (Get-Command node -ErrorAction SilentlyContinue)) { return 'Node is not installed (the toy SSH server runs on it): winget install OpenJS.NodeJS.LTS' }
        if (-not (Test-Path (Join-Path $root 'artifacts\ssh-test-server\node_modules\ssh2'))) {
            Write-Host 'Installing the test SSH server''s package with npm (into artifacts\ssh-test-server) ...'
            & (Join-Path $root 'tools\start-ssh-test-server.ps1') -Install
            if (-not (Test-Path (Join-Path $root 'artifacts\ssh-test-server\node_modules\ssh2'))) { return 'npm did not install ssh2' }
        }
    }
    if (($name -in 'openssh', 'agent', 'x11') -and -not $isAdmin) {
        return 'it needs an elevated PowerShell (a local account, the OpenSSH server, a firewall rule)'
    }
    if ($name -in 'openssh', 'agent') {
        if (-not (Test-Path $sshdExe)) {
            if (-not $InstallMissing) { return 'the OpenSSH server is not installed: winget install --id Microsoft.OpenSSH.Preview --exact (or run with -InstallMissing)' }
            Write-Host 'Installing OpenSSH (client and server) with winget ...'
            & winget install --id Microsoft.OpenSSH.Preview --exact --silent --accept-package-agreements --accept-source-agreements
            if (-not (Test-Path $sshdExe)) { return 'winget did not install the OpenSSH server' }
        }
        $service = Get-Service sshd -ErrorAction SilentlyContinue
        if (-not $service) { return 'the sshd service is not registered' }
        if ($service.Status -ne 'Running') {
            if (-not $InstallMissing) { return 'the sshd service is stopped: Start-Service sshd (or run with -InstallMissing, which stops it again afterwards)' }
            Start-Service sshd
            $script:startedSshd = $true
        }
    }
    if ($name -eq 'agent') {
        if (-not (Test-Path $pageantExe)) {
            if (-not $InstallMissing) { return 'Pageant is not installed: winget install --id PuTTY.PuTTY --exact (or run with -InstallMissing)' }
            Write-Host 'Installing PuTTY (for Pageant) with winget ...'
            & winget install --id PuTTY.PuTTY --exact --silent --accept-package-agreements --accept-source-agreements
            if (-not (Test-Path $pageantExe)) { return 'winget did not install PuTTY' }
        }
        if (-not (Test-Path (Join-Path $root 'artifacts\agent-keys\agent.ppk'))) {
            Write-Host 'Making the key for the agent check (dotnet test) ...'
            & dotnet test --project (Join-Path $root 'tests\PuTTY.Interop.Tests') -c Release --filter 'Name=Writes_the_key_the_agent_smoke_check_uses' | Out-Null
            if (-not (Test-Path (Join-Path $root 'artifacts\agent-keys\agent.ppk'))) { return 'the key was not made' }
        }
    }
    if ($name -eq 'x11' -and -not (Test-Path $vcxsrvExe)) {
        if (-not $InstallMissing) { return 'the X server is not installed: winget install --id marha.VcXsrv --exact (or run with -InstallMissing)' }
        Write-Host 'Installing VcXsrv (an X server) with winget ...'
        & winget install --id marha.VcXsrv --exact --silent --accept-package-agreements --accept-source-agreements
        if (-not (Test-Path $vcxsrvExe)) { return 'winget did not install VcXsrv' }
    }
    return $null
}

$failures = 0
$skipped = @()
$summary = @()
try {
    foreach ($name in $Smoke) {
        $script = Join-Path $root $(if ($name -eq 'mouse') { 'tools\app-mouse-e2e.ps1' } else { "tools\app-$name-smoke.ps1" })
        if (-not (Test-Path $script)) { Write-Host "No such script: $script"; $failures++; continue }

        $why = Prepare $name
        if ($why) {
            Write-Host "== ${name}: SKIPPED, $why"
            $skipped += $name
            continue
        }

        $arguments = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $script, '-Exe', $exe)
        if ($name -eq 'mouse') { $arguments += '-IKnowThisDrivesTheMouse' }
        if ($name -in 'workspace', 'mouse') {
            Write-Host "== ${name}: this drives the real mouse for a minute or two. Hands off the mouse and keyboard (starting in 5 s)."
            Start-Sleep -Seconds 5
        }
        else {
            Write-Host "== $name"
        }

        $out = Join-Path $results "$name.txt"
        $lines = @(& powershell.exe @arguments 2>&1 | ForEach-Object { "$_" } | Tee-Object -FilePath $out | ForEach-Object { Write-Host $_; $_ })
        $code = $LASTEXITCODE
        $passed = @($lines | Where-Object { $_ -match '^PASS' }).Count
        $failed = @($lines | Where-Object { $_ -match '^FAIL' }).Count
        Write-Host "-- ${name}: $passed passed, $failed failed (exit code $code)"
        $summary += "{0,-10} {1,3} passed {2,3} failed" -f $name, $passed, $failed
        $failures += $failed
        if ($failed -eq 0 -and $code -ne 0) { $failures++; $summary[-1] += "  (exit code $code with no FAIL line)" }

        # Whatever the check left running is its own: nothing else was, when this started.
        Get-Process -Name 'PuTTY.App' -ErrorAction SilentlyContinue | Stop-Process -Force
    }
}
finally {
    if ($startedSshd) { Stop-Service sshd -ErrorAction SilentlyContinue }
}

Write-Host ''
Write-Host '== Summary'
$summary | ForEach-Object { Write-Host $_ }
if ($skipped.Count -gt 0) { Write-Host "Skipped: $($skipped -join ', ') (see above for why)" }
exit $failures
