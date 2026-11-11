<#
.SYNOPSIS
  Runs one of the end-to-end scripts inside Windows Sandbox. Started by tools\run-in-sandbox.ps1 through
  `wsb exec`; not meant to be run by hand.

.DESCRIPTION
  Uses the Sandbox's own tools: winget, Node and the winapp CLI are installed there. A process started by
  `wsb exec` gets the environment of the logon, which predates those installs, so PATH is read again from the
  registry first. Whatever else the app or the checks need is installed with winget, once: the C++ runtime for
  puttyengine.dll, and for the SSH checks the test server's one npm package. Then it clears away any app left
  over from an earlier run and runs tools\app-<Smoke>-smoke.ps1 against the app staged next to it.
#>
param(
    [Parameter(Mandatory)] [string] $Smoke,
    [Parameter(Mandatory)] [string] $Exe
)

$work = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)      # ...\work (tools\sandbox\guest-run.ps1)

# The environment of the logon is older than what was installed since: read PATH again.
$env:PATH = [Environment]::GetEnvironmentVariable('PATH', 'Machine') + ';' + [Environment]::GetEnvironmentVariable('PATH', 'User')

function Require($tool) {
    if (-not (Get-Command $tool -ErrorAction SilentlyContinue)) {
        Write-Host "FAIL  $tool is not installed in the Sandbox (install it there with winget)"
        exit 93
    }
}
Require 'winapp'

# The C++ runtime PuTTY's engine DLL links (a fresh Sandbox has none).
if (-not (Test-Path (Join-Path $env:WINDIR 'System32\vcruntime140_1.dll'))) {
    Write-Host 'Installing the Visual C++ runtime with winget ...'
    & winget install --id Microsoft.VCRedist.2015+.x64 --exact --silent --accept-package-agreements --accept-source-agreements
    if (-not (Test-Path (Join-Path $env:WINDIR 'System32\vcruntime140_1.dll'))) { Write-Host 'FAIL  the C++ runtime did not install'; exit 92 }
}

# The SSH checks need Node and the test server's package (installed once, into the shared folder).
if ($Smoke -in 'ssh', 'settings') {
    Require 'node'
    if (-not (Test-Path (Join-Path $work 'artifacts\ssh-test-server\node_modules\ssh2'))) {
        Write-Host 'Installing the test SSH server''s package with npm ...'
        & (Join-Path $work 'tools\start-ssh-test-server.ps1') -Install
        if (-not (Test-Path (Join-Path $work 'artifacts\ssh-test-server\node_modules\ssh2'))) { Write-Host 'FAIL  npm did not install ssh2'; exit 91 }
    }
}

Get-Process -Name 'PuTTY.App' -ErrorAction SilentlyContinue | Stop-Process -Force

$script = Join-Path $work "tools\app-$Smoke-smoke.ps1"
if (-not (Test-Path $script)) { Write-Host "No such script: $script"; exit 98 }
try {
    & $script -Exe $Exe
    $code = $LASTEXITCODE
} catch {
    # Say where a script died; PowerShell's own message does not when the script was started with the call operator.
    Write-Host "FAIL  the script stopped: $($_.Exception.Message)"
    Write-Host $_.InvocationInfo.PositionMessage
    Write-Host $_.ScriptStackTrace
    $code = 1
}
Get-Process -Name 'PuTTY.App' -ErrorAction SilentlyContinue | Stop-Process -Force
exit $code
