<#
  Helpers shared by the end-to-end scripts (dot-sourced). They use winapp, and they assume the app runs in
  Windows Sandbox (tools\run-in-sandbox.ps1): they click.
#>

# Gives the terminal the keyboard focus, as a user does, by clicking inside it. The terminal takes keys only
# while it has the focus, and that needs its window to be the active one; in the Sandbox the app is started by
# `wsb exec`, which Windows does not let take the foreground, and `winapp ui focus` refuses to act on a window
# that is not in front. The click goes just left of the terminal's scroll bar, whose place UI Automation reports.
function Focus-Terminal {
    $ErrorActionPreference = 'Continue'      # winapp writes its refusals to stderr; they are not errors of the script
    $esc = [string][char]27
    $properties = (& winapp ui get-property TerminalScrollBar -a PuTTY.App 2>&1 | Out-String) -replace ($esc + '\[[0-9;]*m'), ''
    if ($properties -match 'BoundingRectangle:\s*(-?\d+),(-?\d+),(\d+),(\d+)') {
        $x = [int]$Matches[1] - 120
        $y = [int]$Matches[2] + 120
        & winapp ui click --at "$x,$y" -a PuTTY.App 2>&1 | Out-Null
    }
    Start-Sleep -Milliseconds 800
}

# With -AttachDebugger on tools\run-in-sandbox.ps1 (PUTTYMODERN_DEBUG_CDB=1 in the guest) this attaches cdb
# (C:\debugger\cdb.exe, tools\sandbox\install-debugger.ps1) to a process the script has just started. The
# commands in tools\sandbox\cdb-crash-commands.txt write the stacks of an unhandled crash to
# C:\spike\work\results\cdb.log and a full dump next to it. Returns the debugger's process, or $null.
function Attach-Debugger($Process) {
    if ($env:PUTTYMODERN_DEBUG_CDB -ne '1') { return $null }
    if (-not (Test-Path 'C:\debugger\cdb.exe')) { Write-Host 'No debugger in the Sandbox: tools\sandbox\install-debugger.ps1'; return $null }
    $work = Split-Path -Parent $PSScriptRoot
    $symbols = "srv*C:\symcache*https://msdl.microsoft.com/download/symbols;$work\app"
    $log = Join-Path $work 'results\cdb.log'
    Remove-Item $log, (Join-Path $work 'results\crash.dmp') -ErrorAction SilentlyContinue
    $debugger = Start-Process 'C:\debugger\cdb.exe' -PassThru -WindowStyle Hidden -ArgumentList @(
        '-p', $Process.Id, '-g', '-G', '-y', $symbols, '-logo', $log, '-cf', (Join-Path $PSScriptRoot 'sandbox\cdb-crash-commands.txt'))
    Start-Sleep -Seconds 4            # attaching and loading symbols before the app does anything interesting
    return $debugger
}
