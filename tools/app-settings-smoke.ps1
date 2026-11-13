<#
.SYNOPSIS
  End-to-end check of the settings window in the real app: it shows PuTTY's configuration box, edits reach
  PuTTY's handlers, Save writes a session into PuTTY's storage, Open starts a session with what was typed,
  and its context menu shows a saved session's settings.

.DESCRIPTION
  Safe on a developer desktop: no key or mouse is injected (UI Automation patterns only), and the user's real
  PuTTY settings are neither read nor written: the app runs with PUTTYMODERN_TEST_REGISTRY_ROOT (see
  TestRegistry.cs), a scratch registry key this script fills and deletes. Needs Node and the one-time install
  of the test SSH server (tools\start-ssh-test-server.ps1 -Install): the session it opens is SSH.

    tools\app-settings-smoke.ps1 -Exe artifacts\publish\PuTTY.App\release_win-x64\PuTTY.App.exe

  Prints PASS or FAIL per check; the exit code is the number of failures.
#>
param(
    [Parameter(Mandatory)] [string] $Exe,
    [switch] $AllowHostDesktop
)

if ($env:USERNAME -ne 'WDAGUtilityAccount' -and -not $AllowHostDesktop) {
    Write-Host 'This starts the app and drives its windows. Run it in Windows Sandbox with tools\run-in-sandbox.ps1, or pass -AllowHostDesktop to use this desktop.'
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
function Gone($selector, $seconds = 15) {
    $deadline = (Get-Date).AddSeconds($seconds)
    while ((Get-Date) -lt $deadline) {
        if ((Ui @('search', $selector)) -match 'Found 0') { return $true }
        Start-Sleep -Milliseconds 400
    }
    return $false
}

$modules = Join-Path $root 'artifacts\ssh-test-server\node_modules'
if (-not (Test-Path (Join-Path $modules 'ssh2'))) { Write-Host 'The test SSH server is not installed: run tools\start-ssh-test-server.ps1 -Install'; exit 99 }

# The test SSH server, which ends when its stdin closes.
# Node: on PATH on a developer machine, or the copy staged next to the scripts in Windows Sandbox.
$nodeExe = if (Test-Path (Join-Path $root 'artifacts\node\node.exe')) { Join-Path $root 'artifacts\node\node.exe' } else { 'node' }$psi = New-Object System.Diagnostics.ProcessStartInfo $nodeExe
$psi.Arguments = '"' + (Join-Path $root 'tools\ssh-test-server\server.js') + '"'
$psi.RedirectStandardInput = $true
$psi.RedirectStandardOutput = $true
$psi.UseShellExecute = $false
$psi.CreateNoWindow = $true
$psi.EnvironmentVariables['NODE_PATH'] = $modules
$server = [System.Diagnostics.Process]::Start($psi)
$info = $server.StandardOutput.ReadLine() | ConvertFrom-Json
$script:reader = $server.StandardOutput.ReadLineAsync()
$script:events = New-Object System.Collections.Generic.List[string]
function Server-Events {
    while ($script:reader.IsCompleted) {
        if ($script:reader.Result) { $script:events.Add($script:reader.Result) }
        $script:reader = $server.StandardOutput.ReadLineAsync()
    }
    return $script:events.ToArray()
}

# The scratch "HKEY_CURRENT_USER", with one saved session so Edit has something to show.
$scratch = "Software\PuttyModernTests\e2e-settings-$([guid]::NewGuid().ToString('N'))"
$puttyKey = "HKCU:\$scratch\Software\SimonTatham\PuTTY"
$saved = Join-Path "$puttyKey\Sessions" 'e2e%20saved'
New-Item -Path $saved -Force | Out-Null
New-ItemProperty -Path $saved -Name 'HostName' -Value 'saved.example.org' -PropertyType String | Out-Null
New-ItemProperty -Path $saved -Name 'PortNumber' -Value 2200 -PropertyType DWord | Out-Null
New-ItemProperty -Path $saved -Name 'Protocol' -Value 'ssh' -PropertyType String | Out-Null

$app = $null
try {
    $env:PUTTYMODERN_TEST_REGISTRY_ROOT = $scratch
    $app = Start-Process -FilePath $Exe -PassThru
    $env:PUTTYMODERN_TEST_REGISTRY_ROOT = $null

    # --- the window and PuTTY's pages
    Check 'the command bar offers New session' (Wait-Element 'CommandNewSession')
    [void](Ui @('invoke', 'CommandNewSession'))
    Check "the settings window shows PuTTY's pages" (Wait-Element 'SettingsPages')
    # The pages are a tree: the top level and the open branch (Session, with Logging) are in view.
    foreach ($page in 'Session', 'Logging', 'Terminal', 'Window', 'Connection') {
        Check "the page tree has $page" ((Ui @('search', $page, '--type', 'TreeItem')) -match 'Found [1-9]')
    }
    Check 'the search box is there' ((Ui @('search', 'Find a setting')) -match 'Found [1-9]')
    Check 'the host name box is there' (Wait-Element 'Host Name (or IP address)' 10 @('--type', 'Edit'))
    # A picture of the window, to compare with the Figma frame "Session configuration" (artifacts\spike\app-settings-*.png).
    New-Item -ItemType Directory -Force -Path (Join-Path $root 'artifacts\spike') | Out-Null
    & winapp ui screenshot -a PuTTY.App --output (Join-Path $root 'artifacts\spike\app-settings-session.png') 2>&1 | Out-Null
    # The sketch's words for the Session page (Presentation.cs), the lower cards scrolled into view for a second picture.
    Check 'the page has the sketch''s line under its title' ((Ui @('search', 'Connect to a host or manage your saved sessions.')) -match 'Found [1-9]')
    [void](Ui @('scroll-into-view', 'Only on clean exit'))
    Check 'the close-on-exit setting is a card with its explanation' (Wait-Element 'Choose what happens when the connection ends.' 10)
    & winapp ui screenshot -a PuTTY.App --output (Join-Path $root 'artifacts\spike\app-settings-session-bottom.png') 2>&1 | Out-Null
    # Choosing a page in the tree shows that page's controls (check boxes are switches, named after PuTTY's labels).
    [void](Ui @('invoke', 'Terminal', '--type', 'TreeItem'))
    Check 'choosing Terminal in the tree shows its controls' (Wait-Element 'Auto wrap mode initially on' 15)
    & winapp ui screenshot -a PuTTY.App --output (Join-Path $root 'artifacts\spike\app-settings-terminal.png') 2>&1 | Out-Null
    Check 'Answerback is a card of its own with its explanation' (Wait-Element 'Response sent when the remote host requests identification.' 10)
    [void](Ui @('scroll-into-view', 'Printer to send ANSI printer output to:'))
    Check 'remote-controlled printing is a card with the sketch''s line' (Wait-Element 'Printer for ANSI printer output' 10)
    Check 'the printer box says what an empty setting means' ((Ui @('get-value', 'Printer to send ANSI printer output to:')) -like '*None (printing disabled)*')
    Check 'the line discipline card has its short title' ((Ui @('search', 'Line discipline')) -match 'Found [1-9]')
    & winapp ui screenshot -a PuTTY.App --output (Join-Path $root 'artifacts\spike\app-settings-terminal-bottom.png') 2>&1 | Out-Null
    [void](Ui @('invoke', 'Session', '--type', 'TreeItem'))
    Check 'and Session brings the host name box back' (Wait-Element 'Host Name (or IP address)' 15 @('--type', 'Edit'))
    Check 'and the port box, filled with the SSH default by PuTTY''s own handler' ((Ui @('get-value', 'Port', '--type', 'Edit')) -match '22')

    # --- edit: host, port, a name for the session; Save writes PuTTY's storage
    [void](Ui @('set-value', 'Host Name (or IP address)', '127.0.0.1', '--type', 'Edit'))
    [void](Ui @('set-value', 'Port', "$($info.port)", '--type', 'Edit'))
    [void](Ui @('set-value', 'Saved Sessions', 'e2e created', '--type', 'Edit'))
    [void](Ui @('invoke', 'Save', '--type', 'Button'))
    Start-Sleep -Seconds 1
    $createdKey = Join-Path "$puttyKey\Sessions" 'e2e%20created'
    $stored = if (Test-Path $createdKey) { Get-ItemProperty $createdKey } else { $null }
    Check 'Save wrote the session into PuTTY''s storage' ($null -ne $stored) "($createdKey)"
    Check 'with the host that was typed' ($stored.HostName -eq '127.0.0.1') "($($stored.HostName))"
    Check 'and the port' ($stored.PortNumber -eq $info.port) "($($stored.PortNumber))"
    Check 'and the protocol PuTTY chose' ($stored.Protocol -eq 'ssh') "($($stored.Protocol))"

    # --- Open: the session starts with what was typed (the SSH dialogs of slice 3 follow)
    [void](Ui @('invoke', 'Open session', '--type', 'Button'))
    Check 'Open closes the settings window' (Gone 'SettingsPages')
    Check 'the host key of the typed host is asked about' (Wait-Element 'Connect Once' 30)
    [void](Ui @('invoke', 'Connect Once'))
    Check 'then the user name' (Wait-Element 'PromptField0' 30)
    [void](Ui @('set-value', 'PromptField0', 'test'))
    [void](Ui @('invoke', 'OK'))
    Check 'then the password' (Wait-Element 'PromptField0' 30)
    [void](Ui @('set-value', 'PromptField0', 'secret'))
    [void](Ui @('invoke', 'OK'))
    $deadline = (Get-Date).AddSeconds(30)
    while ((Get-Date) -lt $deadline -and -not ((Server-Events) -contains '{"event":"shell"}')) { Start-Sleep -Milliseconds 300 }
    Check 'the server started a shell: the session ran with the edited settings' ((Server-Events) -contains '{"event":"shell"}') "($((Server-Events) -join ' '))"

    # --- the session that was saved shows up in the panel; its context menu shows a saved session's settings
    # (The document tab is also called "e2e created", so look for the list entry, a ListItem.)
    Check 'the session saved from the dialog is in the panel' (Wait-Element 'e2e created' 15 @('--type', 'ListItem'))
    # The saved session's context menu has its settings (the panel has no Edit button).
    [void](Ui @('click', 'Session:e2e saved', '--right'))
    Check 'a right click on a saved session offers its settings' (Wait-Element 'Settings...' 10 @('--type', 'MenuItem'))
    [void](Ui @('invoke', 'Settings...', '--type', 'MenuItem'))
    Check 'Settings... opens the settings window' (Wait-Element 'SettingsPages' 15)
    $deadline = (Get-Date).AddSeconds(10)
    $hostShown = ''
    while ((Get-Date) -lt $deadline -and $hostShown -notmatch 'saved.example.org') {
        $hostShown = Ui @('get-value', 'Host Name (or IP address)', '--type', 'Edit')
        Start-Sleep -Milliseconds 300
    }
    Check 'it shows the saved session''s host' ($hostShown -match 'saved.example.org') "($($hostShown.Trim()))"
    Check 'and its port' ((Ui @('get-value', 'Port', '--type', 'Edit')) -match '2200')
    [void](Ui @('invoke', 'Cancel', '--type', 'Button'))
    Check 'Cancel closes it' (Gone 'SettingsPages')
}
finally {
    $env:PUTTYMODERN_TEST_REGISTRY_ROOT = $null
    $closedCleanly = $false
    if ($app -and -not $app.HasExited) { [void]$app.CloseMainWindow(); [void]$app.WaitForExit(10000) }
    if ($app) {
        if ($app.HasExited) { $closedCleanly = ($app.ExitCode -eq 0) } else { $app.Kill() }
        Check 'the app exits cleanly when its window is closed' $closedCleanly "(exit code $(if ($app.HasExited) { $app.ExitCode } else { 'still running' }))"
    }
    try { $server.StandardInput.Close(); if (-not $server.WaitForExit(3000)) { $server.Kill() } } catch { }
    Remove-Item -Path "HKCU:\Software\PuttyModernTests\$($scratch.Split('\')[-1])" -Recurse -Force -ErrorAction SilentlyContinue
}
exit $failures
