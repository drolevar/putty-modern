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
