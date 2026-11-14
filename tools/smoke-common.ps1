<#
  Helpers shared by the end-to-end scripts (dot-sourced). They use winapp, and they click: they run on the desktop
  they are started on, which is this machine's (tools\run-checks.ps1) or the Sandbox's (tools\run-in-sandbox.ps1).
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

# Brings the app's main window to the front. In the Sandbox the app is started by `wsb exec`, which Windows does not let
# take the foreground, and winapp refuses to click on a window that is not in front (the same reason as Focus-Terminal), so a
# check whose first action is a click (a right click on a session in the panel) calls this first. On a desktop where the app
# is in front already it changes nothing. It uses what worked in the IME check: share the input queue of the window that has
# the foreground, then SetForegroundWindow and SwitchToThisWindow. The window's size and state stay as they are.
function Bring-AppToFront {
    if (-not ('SmokeForeground' -as [type])) {
        Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class SmokeForeground
{
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] private static extern bool AttachThreadInput(uint from, uint to, bool attach);
    [DllImport("user32.dll")] private static extern bool BringWindowToTop(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hwnd, int command);
    [DllImport("user32.dll")] private static extern void SwitchToThisWindow(IntPtr hwnd, bool altTab);

    public static void Raise(IntPtr hwnd)
    {
        ShowWindow(hwnd, 5);          // SW_SHOW: a visible window stays as it is
        uint ignored;
        uint foregroundThread = GetWindowThreadProcessId(GetForegroundWindow(), out ignored);
        uint thisThread = GetCurrentThreadId();
        if (foregroundThread != 0 && foregroundThread != thisThread) { AttachThreadInput(thisThread, foregroundThread, true); }
        BringWindowToTop(hwnd);
        SetForegroundWindow(hwnd);
        if (foregroundThread != 0 && foregroundThread != thisThread) { AttachThreadInput(thisThread, foregroundThread, false); }
        SwitchToThisWindow(hwnd, true);
    }
}
'@
    }
    $process = Get-Process -Name 'PuTTY.App' -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($process) {
        $process.Refresh()
        if ($process.MainWindowHandle -ne [IntPtr]::Zero) { [SmokeForeground]::Raise($process.MainWindowHandle) }
        Start-Sleep -Milliseconds 500
    }
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
        '-p', $Process.Id, '-G', '-y', $symbols, '-logo', $log, '-cf', (Join-Path $PSScriptRoot 'sandbox\cdb-crash-commands.txt'))
    Start-Sleep -Seconds 4            # attaching and loading symbols before the app does anything interesting
    return $debugger
}
