<#
.SYNOPSIS
  End-to-end check of key authentication through Pageant, in the real app, against a real OpenSSH server: the only key
  that can log in is held by Pageant, and the app logs in without asking for a password.

.DESCRIPTION
  Runs in Windows Sandbox (tools\run-in-sandbox.ps1 -Smoke agent), whose guest-run.ps1 installs OpenSSH (sshd) and PuTTY
  (for pageant.exe) with winget. tools\run-in-sandbox.ps1 first has a test make the key (artifacts\agent-keys\agent.ppk
  and agent.pub: the Windows puttygen has no command line). The check makes a local administrator account whose
  authorized keys (C:\ProgramData\ssh\administrators_authorized_keys) hold only that key, starts Pageant with the key,
  connects with the app (host key: Connect Once), and sees that PuTTY's event log says it found the key in Pageant, that
  no password is asked for, and that a command typed into the shell ran. Then Pageant is stopped and the app is started
  again: now the server's password question comes (the control: it was the agent that got it in). The account is removed
  at the end.

  PuTTY's agent client talks to Pageant (a window, and Pageant's own named pipe). It does not talk to the Windows OpenSSH
  agent service (\\.\pipe\openssh-ssh-agent): that is PuTTY's design, not something the app can change.

  Prints PASS or FAIL per check; the exit code is the number of failures.
#>
param(
    [Parameter(Mandatory)] [string] $Exe
)

if ($env:USERNAME -ne 'WDAGUtilityAccount') {
    Write-Host 'This installs an account, starts Pageant and drives the app: run it in Windows Sandbox with tools\run-in-sandbox.ps1 -Smoke agent.'
    exit 99
}
. (Join-Path $PSScriptRoot 'smoke-common.ps1')
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

Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class AgentSmokeNative
{
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);
    [DllImport("user32.dll", SetLastError = true)] public static extern bool PostThreadMessageW(uint threadId, uint msg, UIntPtr wParam, IntPtr lParam);
}
'@

$user = 'puttyagent'
$password = 'Putty-Agent-2026!'
$keys = Join-Path $root 'agent-keys'
$ppk = Join-Path $keys 'agent.ppk'
$pageantExe = 'C:\Program Files\PuTTY\pageant.exe'
$authorizedKeys = Join-Path $env:ProgramData 'ssh\administrators_authorized_keys'
$marker = Join-Path $root 'results\agent-ok.txt'

Check 'the key for the agent was made' ((Test-Path $ppk) -and (Test-Path (Join-Path $keys 'agent.pub')))
Check 'Pageant is installed' (Test-Path $pageantExe)
$sshd = Get-Service sshd -ErrorAction SilentlyContinue
Check 'the OpenSSH server (sshd) is running' ($sshd -and $sshd.Status -eq 'Running') "($($sshd.Status))"
if ($failures -gt 0) { exit $failures }

# An administrator account for the login, whose only authorized key is the agent's. (For members of the Administrators
# group sshd reads administrators_authorized_keys, which needs no user profile to exist yet.)
Remove-LocalUser -Name $user -ErrorAction SilentlyContinue
$account = New-LocalUser -Name $user -Password (ConvertTo-SecureString $password -AsPlainText -Force) -AccountNeverExpires -PasswordNeverExpires
Add-LocalGroupMember -Group 'Administrators' -Member $user
[System.IO.File]::WriteAllText($authorizedKeys, ((Get-Content (Join-Path $keys 'agent.pub') -Raw).Trim() + "`n"), (New-Object System.Text.UTF8Encoding($false)))
& icacls.exe $authorizedKeys /inheritance:r /grant 'Administrators:F' /grant 'SYSTEM:F' | Out-Null
Check 'a local administrator account with the agent''s key as its only key was made' ($null -ne $account -and (Test-Path $authorizedKeys))
Remove-Item $marker -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force -Path (Split-Path $marker) | Out-Null

function Stop-Pageant { Get-Process -Name pageant -ErrorAction SilentlyContinue | Stop-Process -Force }
function Close-App($app) {
    if ($app -and -not $app.HasExited) { [void]$app.CloseMainWindow(); [void]$app.WaitForExit(10000) }
    if ($app -and -not $app.HasExited) { $app.Kill() }
}

$app = $null
try {
    # --- Pageant holds the key
    Stop-Pageant
    $pageant = Start-Process -FilePath $pageantExe -ArgumentList "`"$ppk`"" -PassThru
    Start-Sleep -Seconds 4
    Check 'Pageant is running' (-not $pageant.HasExited)

    # --- the app connects; the host key is new, nothing else is asked
    $app = Start-Process -FilePath $Exe -ArgumentList @('--connect', "ssh://$user@127.0.0.1:22") -PassThru
    Check 'the host key dialog appears' (Wait-Element 'Connect Once' 40)
    [void](Ui @('invoke', 'Connect Once'))
    Check 'PuTTY asked Pageant for its keys and found one (its event log says so)' (Wait-Element 'Pageant has 1 SSH-2 keys' 40)
    Check 'and no password is asked for' (-not (Wait-Element 'PromptField0' 6))

    # --- logged in: type a command into the shell and look for its effect
    Start-Sleep -Seconds 4
    Focus-Terminal
    $app.Refresh()
    $pid2 = 0
    $uiThread = [AgentSmokeNative]::GetWindowThreadProcessId($app.MainWindowHandle, [ref]$pid2)
    $command = "echo hello-from-agent> `"$marker`""
    foreach ($ch in $command.ToCharArray()) {
        [void][AgentSmokeNative]::PostThreadMessageW($uiThread, 0x0102, [UIntPtr]::new([uint64][int]$ch), [IntPtr]::new(1))   # WM_CHAR
    }
    $down = [IntPtr]::new(1 -bor (0x1C -shl 16))
    $up = [IntPtr]::new([int64]([uint32](1 -bor (0x1C -shl 16) -bor (1 -shl 30)) -bor 0x80000000))
    [void][AgentSmokeNative]::PostThreadMessageW($uiThread, 0x0100, [UIntPtr]::new(13), $down)   # WM_KEYDOWN VK_RETURN
    [void][AgentSmokeNative]::PostThreadMessageW($uiThread, 0x0101, [UIntPtr]::new(13), $up)     # WM_KEYUP
    $deadline = (Get-Date).AddSeconds(30)
    while ((Get-Date) -lt $deadline -and -not (Test-Path $marker)) { Start-Sleep -Milliseconds 500 }
    $written = if (Test-Path $marker) { (Get-Content $marker -Raw).Trim() } else { '' }
    Check 'the command ran on the OpenSSH server, logged in by the agent''s key alone' ($written -eq 'hello-from-agent') "($written)"
    Close-App $app
    $app = $null

    # --- the control: without Pageant the server asks for a password
    Stop-Pageant
    Start-Sleep -Seconds 2
    $app = Start-Process -FilePath $Exe -ArgumentList @('--connect', "ssh://$user@127.0.0.1:22") -PassThru
    Check 'the host key dialog appears again (Connect Once remembers nothing)' (Wait-Element 'Connect Once' 40)
    [void](Ui @('invoke', 'Connect Once'))
    Check 'without Pageant the server asks for a password' (Wait-Element 'PromptField0' 40)
}
finally {
    Close-App $app
    Stop-Pageant
    Remove-LocalUser -Name $user -ErrorAction SilentlyContinue
    Remove-Item $authorizedKeys -ErrorAction SilentlyContinue
}
exit $failures
