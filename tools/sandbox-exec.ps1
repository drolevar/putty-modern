<#
.SYNOPSIS
  Runs a command inside Windows Sandbox and prints what it wrote: for installing and inspecting things in the
  guest (winget, services, files), where tools\run-in-sandbox.ps1 is for the end-to-end checks.

.DESCRIPTION
  The Sandbox must be running and logged in. The command goes through a one-line .cmd in the shared folder
  (artifacts\sandbox-share\work\results), because wsb does not survive nested quotes, and its output comes back
  through a file. -RunAs System runs it without a desktop and with full rights (services, accounts); the default,
  ExistingLogin, is the logged-in user on the guest's desktop (an administrator in the Sandbox).

    tools\sandbox-exec.ps1 'winget search openssh'
    tools\sandbox-exec.ps1 'net user test secret /add' -RunAs System
#>
param(
    [Parameter(Mandatory, Position = 0)] [string] $Command,
    [ValidateSet('ExistingLogin', 'System')] [string] $RunAs = 'ExistingLogin',
    [int] $TimeoutSeconds = 900
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$results = Join-Path $root 'artifacts\sandbox-share\work\results'
New-Item -ItemType Directory -Force -Path $results | Out-Null

$ids = @(& wsb list 2>&1 | Where-Object { $_ -match '^[0-9a-f-]{36}$' })
if ($ids.Count -eq 0) { Write-Host 'Windows Sandbox is not running. Start it from the Start menu.'; exit 96 }

$launcher = Join-Path $results 'exec.cmd'
$output = Join-Path $results 'exec.txt'
Remove-Item $output -ErrorAction SilentlyContinue
Set-Content -Path $launcher -Encoding ASCII -Value "($Command) > C:\spike\work\results\exec.txt 2>&1"
$run = & wsb exec --id $ids[0] -r $RunAs -d 'C:\spike\work' -c 'cmd.exe /c C:\spike\work\results\exec.cmd' 2>&1
if (Test-Path $output) { Get-Content $output | ForEach-Object { Write-Host $_ } } else { Write-Host "(no output came back) $($run | Out-String)" }
if (($run | Out-String) -match 'exited with code: (-?\d+)') { exit [int]$Matches[1] }
