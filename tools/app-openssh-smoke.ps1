<#
.SYNOPSIS
  End-to-end check of SSH in the real app against a real OpenSSH server (Win32-OpenSSH's sshd), not the toy
  test server: host key dialog, a wrong password that is refused and asked for again, the right one, and a
  command typed into the shell whose effect on disk is checked.

.DESCRIPTION
  Runs in Windows Sandbox (tools\run-in-sandbox.ps1 -Smoke openssh), whose guest-run.ps1 installs OpenSSH with
  winget (Microsoft.OpenSSH.Preview) and starts the sshd service if they are not there. The check makes a local
  account for the login and deletes it at the end. Drives the dialogs with UI Automation (winapp); the command is
  typed by posting thread messages to the app's UI thread, as in app-ssh-smoke.ps1.

  Prints PASS or FAIL per check; the exit code is the number of failures.
#>
param(
    [Parameter(Mandatory)] [string] $Exe
)

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
public static class OpenSshSmokeNative
{
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);
    [DllImport("user32.dll", SetLastError = true)] public static extern bool PostThreadMessageW(uint threadId, uint msg, UIntPtr wParam, IntPtr lParam);
}
'@

$user = 'puttytest'
$password = 'Pw-' + [guid]::NewGuid().ToString('N') + '-aA1!'      # new for every run: sshd listens while the account exists
$marker = Join-Path $root 'results\openssh-ok.txt'
$sshd = Get-Service sshd -ErrorAction SilentlyContinue
Check 'the OpenSSH server (sshd) is running' ($sshd -and $sshd.Status -eq 'Running') "($($sshd.Status))"
if (-not ($sshd -and $sshd.Status -eq 'Running')) { exit $failures }

# An account for the login (removed again below). New-LocalUser, not `net user`:
# net asks a question on the console when the password is longer than 14 characters and fails when nobody answers.
# Only an account this check made (it says so in its description) is ever removed: on a machine that is somebody's, an
# account with this name is theirs.
$accountTag = 'putty-modern e2e check (temporary account)'
$existing = Get-LocalUser -Name $user -ErrorAction SilentlyContinue
if ($existing -and $existing.Description -ne $accountTag) {
    Write-Host "FAIL  a local account named $user exists and is not one this check made: it is left alone and the check does not run"
    exit 98
}
if ($existing) { Remove-LocalUser -Name $user }      # left by an earlier run that did not get to clean up
$account = New-LocalUser -Name $user -Password (ConvertTo-SecureString $password -AsPlainText -Force) -Description $accountTag -AccountNeverExpires -PasswordNeverExpires
Check 'a local account for the login was made' ($null -ne $account)
Remove-Item $marker -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force -Path (Split-Path $marker) | Out-Null

$app = $null
$debugger = $null
try {
    $app = Start-Process -FilePath $Exe -ArgumentList @('--connect', "ssh://$user@127.0.0.1:22") -PassThru
    $debugger = Attach-Debugger $app        # only with run-in-sandbox.ps1 -AttachDebugger

    # --- the host key: OpenSSH's own key, asked about because this host has never been seen
    Check 'the host key dialog appears' (Wait-Element 'Connect Once' 40)
    $dialog = Ui @('inspect', '--depth', '14')
    Check 'it names the key type and a SHA256 fingerprint' ($dialog -match 'ssh-(ed25519|rsa|ecdsa)' -and $dialog -match 'SHA256:')
    [void](Ui @('invoke', 'Connect Once'))

    # --- a wrong password is refused by the real server, and PuTTY asks again
    Check 'a password is asked for' (Wait-Element 'PromptField0' 30)
    [void](Ui @('set-value', 'PromptField0', 'not the password'))
    [void](Ui @('invoke', 'OK'))
    Start-Sleep -Seconds 3
    Check 'OpenSSH refuses it and the password is asked for again' (Wait-Element 'PromptField0' 30)
    [void](Ui @('set-value', 'PromptField0', $password))
    [void](Ui @('invoke', 'OK'))

    # --- logged in: type a command into the shell and look for its effect
    Start-Sleep -Seconds 6
    Focus-Terminal
    $app.Refresh()
    $pid2 = 0
    $uiThread = [OpenSshSmokeNative]::GetWindowThreadProcessId($app.MainWindowHandle, [ref]$pid2)
    $command = "echo hello-from-openssh> `"$marker`""
    foreach ($ch in $command.ToCharArray()) {
        [void][OpenSshSmokeNative]::PostThreadMessageW($uiThread, 0x0102, [UIntPtr]::new([uint64][int]$ch), [IntPtr]::new(1))   # WM_CHAR
    }
    $down = [IntPtr]::new(1 -bor (0x1C -shl 16))
    $up = [IntPtr]::new([int64]([uint32](1 -bor (0x1C -shl 16) -bor (1 -shl 30)) -bor 0x80000000))
    [void][OpenSshSmokeNative]::PostThreadMessageW($uiThread, 0x0100, [UIntPtr]::new(13), $down)   # WM_KEYDOWN VK_RETURN
    [void][OpenSshSmokeNative]::PostThreadMessageW($uiThread, 0x0101, [UIntPtr]::new(13), $up)     # WM_KEYUP
    $deadline = (Get-Date).AddSeconds(30)
    while ((Get-Date) -lt $deadline -and -not (Test-Path $marker)) { Start-Sleep -Milliseconds 500 }
    $written = if (Test-Path $marker) { (Get-Content $marker -Raw).Trim() } else { '' }
    Check 'the command ran on the OpenSSH server and left its mark' ($written -eq 'hello-from-openssh') "($written)"
}
finally {
    $closedCleanly = $false
    # With a debugger attached a crashed app stays alive until the debugger has written its stacks and dump;
    # killing it before that leaves nothing to read.
    $patience = if ($debugger) { 180000 } else { 10000 }
    if ($app -and -not $app.HasExited) { [void]$app.CloseMainWindow(); [void]$app.WaitForExit($patience) }
    if ($app) {
        if ($app.HasExited) { $closedCleanly = ($app.ExitCode -eq 0) } else { $app.Kill() }
        Check 'the app exits cleanly when its window is closed' $closedCleanly "(exit code $(if ($app.HasExited) { $app.ExitCode } else { 'still running' }))"
    }
    Remove-LocalUser -Name $user -ErrorAction SilentlyContinue
}
exit $failures
