<#
.SYNOPSIS
  End-to-end check that a session's own appearance settings are used by the real app: a saved session that names a
  larger font gets a terminal with fewer, bigger cells than one that has PuTTY's default font, and changing the font of
  a running session (Change Settings, Window > Appearance) refits its terminal.

.DESCRIPTION
  Run it in Windows Sandbox with tools\run-in-sandbox.ps1 -Smoke appearance. The user's real PuTTY settings are neither
  read nor written: the app is started with PUTTYMODERN_TEST_REGISTRY_ROOT (see TestRegistry.cs), which points its
  registry at a scratch key with two saved sessions. The size of a terminal is read from the status bar ("80 x 24").

  Prints PASS or FAIL per check; the exit code is the number of failures.
#>
param(
    [Parameter(Mandatory)] [string] $Exe,
    [string] $Screenshot = ''
)

if ($env:USERNAME -ne 'WDAGUtilityAccount') {
    Write-Host 'This starts the real app and opens windows: run it in Windows Sandbox with tools\run-in-sandbox.ps1 -Smoke appearance.'
    exit 99
}
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
if (-not $Screenshot) { $Screenshot = Join-Path $root 'artifacts\spike\app-appearance.png' }
$failures = 0
$esc = [string][char]27
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

# The terminal's size in cells, from the status bar ("80 x 24", the sign between the numbers is the multiplication sign).
function Get-TerminalColumns {
    $tree = Ui @('inspect', '--depth', '24')
    if ($tree -match '"(\d+) \S+ (\d+)"') { return [int]$Matches[1] }
    return -1
}
function Wait-Columns($predicate, $seconds = 15) {
    $deadline = (Get-Date).AddSeconds($seconds)
    $columns = -1
    while ((Get-Date) -lt $deadline) {
        $columns = Get-TerminalColumns
        if (& $predicate $columns) { return $columns }
        Start-Sleep -Milliseconds 500
    }
    return $columns
}

# --- the scratch "HKEY_CURRENT_USER" with two saved sessions
$leaf = "e2e-appearance-$([guid]::NewGuid().ToString('N'))"
$scratch = "Software\PuttyModernTests\$leaf"
$sessionsKey = "HKCU:\$scratch\Software\SimonTatham\PuTTY\Sessions"
function Save-Session($name, $port, $font = $null) {
    $key = Join-Path $sessionsKey ($name -replace ' ', '%20')
    New-Item -Path $key -Force | Out-Null
    New-ItemProperty -Path $key -Name 'HostName' -Value '127.0.0.1' -PropertyType String | Out-Null
    New-ItemProperty -Path $key -Name 'PortNumber' -Value $port -PropertyType DWord | Out-Null
    New-ItemProperty -Path $key -Name 'Protocol' -Value 'raw' -PropertyType String | Out-Null
    if ($font) {
        # PuTTY writes a font as four values and ignores it unless all four are there
        New-ItemProperty -Path $key -Name 'Font' -Value $font.Name -PropertyType String | Out-Null
        New-ItemProperty -Path $key -Name 'FontIsBold' -Value 0 -PropertyType DWord | Out-Null
        New-ItemProperty -Path $key -Name 'FontCharSet' -Value 0 -PropertyType DWord | Out-Null
        New-ItemProperty -Path $key -Name 'FontHeight' -Value $font.Height -PropertyType DWord | Out-Null
    }
}
$plain = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Loopback, 0); $plain.Start()
$big = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Loopback, 0); $big.Start()
Save-Session 'e2e plain' $plain.LocalEndpoint.Port
Save-Session 'e2e big' $big.LocalEndpoint.Port @{ Name = 'Consolas'; Height = 28 }
$workspaceDir = Join-Path $env:TEMP "PuttyModernTests\$leaf"
New-Item -ItemType Directory -Force -Path (Split-Path $Screenshot) | Out-Null

$app = $null
try {
    $env:PUTTYMODERN_TEST_REGISTRY_ROOT = $scratch
    $app = Start-Process -FilePath $Exe -PassThru
    $env:PUTTYMODERN_TEST_REGISTRY_ROOT = $null
    Check 'the sessions panel lists the saved sessions' ((Wait-Element 'Session:e2e plain' 40) -and (Wait-Element 'Session:e2e big' 10))

    $acceptPlain = $plain.AcceptTcpClientAsync()
    Open-Session 'e2e plain'
    Check 'the session with the default font connects' ($acceptPlain.Wait(20000))
    $plainColumns = Wait-Columns { param($c) $c -gt 20 }
    Check 'its terminal has a size in the status bar' ($plainColumns -gt 20) "($plainColumns columns)"

    $acceptBig = $big.AcceptTcpClientAsync()
    Open-Session 'e2e big'
    Check 'the session with a larger font connects' ($acceptBig.Wait(20000))
    $bigColumns = Wait-Columns { param($c) $c -gt 0 -and $c -ne $plainColumns }
    Check 'its terminal has fewer, bigger cells: the session''s own font is used' (($bigColumns -gt 0) -and ($bigColumns -lt $plainColumns)) "($bigColumns columns against $plainColumns)"
    & winapp ui screenshot -a PuTTY.App --output $Screenshot 2>&1 | Out-Null

    # --- Change Settings on the running session that has the default font: Window > Appearance > Font > Change...
    [void](Ui @('invoke', 'e2e plain', '--type', 'TabItem'))
    $again = Wait-Columns { param($c) $c -eq $plainColumns }
    Check 'the status bar follows the active tab' ($again -eq $plainColumns) "($again columns)"
    [void](Ui @('invoke', 'ChangeSettingsButton'))
    Check 'Session settings opens PuTTY''s Change Settings' (Wait-Element 'Apply' 20 @('--type', 'Button'))
    [void](Ui @('invoke', 'Window', '--type', 'TreeItem'))
    [void](Wait-Element 'Appearance' 15 @('--type', 'TreeItem'))
    [void](Ui @('invoke', 'Appearance', '--type', 'TreeItem'))
    Check 'the Appearance page has the font setting' (Wait-Element 'Font used in the terminal window' 15 @('--type', 'Button'))
    [void](Ui @('invoke', 'Font used in the terminal window', '--type', 'Button'))
    Check 'the font chooser opens' (Wait-Element 'Size (points)' 15)
    [void](Ui @('set-value', 'EditableText', 'Consolas'))
    [void](Ui @('set-value', 'InputBox', '28'))
    [void](Ui @('invoke', 'OK', '--type', 'Button'))
    Check 'the chooser closes' (-not (Wait-Element 'Size (points)' 3))
    [void](Ui @('invoke', 'Apply', '--type', 'Button'))
    $closed = $false
    $deadline = (Get-Date).AddSeconds(15)
    while ((Get-Date) -lt $deadline -and -not $closed) {
        $closed = (Ui @('search', 'Apply', '--type', 'Button')) -match 'Found 0'
        if (-not $closed) { Start-Sleep -Milliseconds 400 }
    }
    Check 'Apply closes the settings window' $closed
    $changed = Wait-Columns { param($c) $c -gt 0 -and $c -ne $plainColumns }
    Check 'the running session took the new font: its terminal was refitted to fewer, bigger cells' (($changed -gt 0) -and ($changed -lt $plainColumns)) "($changed columns, $plainColumns before)"
    & winapp ui screenshot -a PuTTY.App --output ($Screenshot -replace '\.png$', '-changed.png') 2>&1 | Out-Null
}
finally {
    $env:PUTTYMODERN_TEST_REGISTRY_ROOT = $null
    if ($app -and -not $app.HasExited) { $app.Kill() }
    $plain.Stop(); $big.Stop()
    Remove-Item -Path $workspaceDir -Recurse -Force -ErrorAction SilentlyContinue
    Remove-Item -Path "HKCU:\Software\PuttyModernTests\$leaf" -Recurse -Force -ErrorAction SilentlyContinue
}
exit $failures
