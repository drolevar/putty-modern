<#
.SYNOPSIS
  End-to-end check of the Sessions panel in the real app: sessions saved in PuTTY's registry storage are
  listed, one opens and connects, and opening another replaces it after a question.

.DESCRIPTION
  Safe on a developer desktop: no key or mouse is injected (UI Automation patterns only), and the user's
  real PuTTY settings are neither read nor written. The app is started with PUTTYMODERN_TEST_REGISTRY_ROOT
  (see TestRegistry.cs), which points its HKEY_CURRENT_USER at a scratch key this script fills with
  sessions in PuTTY's own format and deletes at the end.

    tools\app-sessions-smoke.ps1 -Exe artifacts\publish\PuTTY.App\release_win-x64\PuTTY.App.exe

  Prints PASS or FAIL per check; the exit code is the number of failures.
#>
param(
    [Parameter(Mandatory)] [string] $Exe,
    [string] $Screenshot = '',
    [switch] $AllowHostDesktop
)

if ($env:USERNAME -ne 'WDAGUtilityAccount' -and -not $AllowHostDesktop) {
    Write-Host 'This starts the app and drives its windows. Run it in Windows Sandbox with tools\run-in-sandbox.ps1, or pass -AllowHostDesktop to use this desktop.'
    exit 99
}
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
if (-not $Screenshot) { $Screenshot = Join-Path $root 'artifacts\spike\app-sessions.png' }
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

# Opens a saved session from its context menu (a right click on its row, then Open): the Sessions panel has no Open button.
# The menu appears a moment after the click, so try again for a few seconds.
function Open-Session($name) {
    [void](Ui @('click', "Session:$name", '--right'))
    for ($i = 0; $i -lt 20; $i++) {
        try { [void](Ui @('invoke', 'Open', '--type', 'MenuItem')); return } catch { Start-Sleep -Milliseconds 500 }
    }
    [void](Ui @('invoke', 'Open', '--type', 'MenuItem'))
}

Add-Type -TypeDefinition @'
using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

public static class SessionsSmokeNative
{
    public static int CountColour(string path, int r, int g, int b)
    {
        using (var bmp = new Bitmap(path))
        {
            var data = bmp.LockBits(new Rectangle(0, 0, bmp.Width, bmp.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            try
            {
                int count = 0;
                byte[] row = new byte[data.Stride];
                for (int y = 0; y < bmp.Height; y++)
                {
                    Marshal.Copy(IntPtr.Add(data.Scan0, y * data.Stride), row, 0, data.Stride);
                    for (int x = 0; x < bmp.Width; x++)
                        if (row[x * 4 + 2] == r && row[x * 4 + 1] == g && row[x * 4] == b) count++;
                }
                return count;
            }
            finally { bmp.UnlockBits(data); }
        }
    }
}
'@ -ReferencedAssemblies System.Drawing

# --- the scratch "HKEY_CURRENT_USER" with sessions in PuTTY's format
$scratch = "Software\PuttyModernTests\e2e-sessions-$([guid]::NewGuid().ToString('N'))"
$sessionsKey = "HKCU:\$scratch\Software\SimonTatham\PuTTY\Sessions"
function Save-Session($name, $port) {
    $key = Join-Path $sessionsKey ($name -replace ' ', '%20')
    New-Item -Path $key -Force | Out-Null
    New-ItemProperty -Path $key -Name 'HostName' -Value '127.0.0.1' -PropertyType String | Out-Null
    New-ItemProperty -Path $key -Name 'PortNumber' -Value $port -PropertyType DWord | Out-Null
    New-ItemProperty -Path $key -Name 'Protocol' -Value 'raw' -PropertyType String | Out-Null
}

$alpha = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Loopback, 0); $alpha.Start()
$beta = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Loopback, 0); $beta.Start()
Save-Session 'e2e alpha' $alpha.LocalEndpoint.Port
Save-Session 'e2e beta' $beta.LocalEndpoint.Port
Save-Session 'Default Settings' 22

$realSessions = 'HKCU:\Software\SimonTatham\PuTTY\Sessions'
$realBefore = if (Test-Path $realSessions) { (Get-ChildItem $realSessions | Measure-Object).Count } else { 0 }

# The panel's folders and recents are the application's own file; the test hook moves it under %TEMP%\PuttyModernTests
# (TestRegistry.cs). It starts with one folder ("Team", holding alpha) and beta opened yesterday.
$workspaceDir = Join-Path $env:TEMP "PuttyModernTests\$($scratch.Split('\')[-1])"
$workspaceFile = Join-Path $workspaceDir 'workspace.json'
New-Item -ItemType Directory -Force -Path $workspaceDir | Out-Null
$yesterday = (Get-Date).AddDays(-1).ToString('o')
@"
{ "version": 1,
  "folders": [ { "id": "f1", "name": "Team" } ],
  "sessions": { "e2e alpha": { "folderId": "f1" }, "e2e beta": { "lastOpened": "$yesterday", "openCount": 1 } } }
"@ | Set-Content -Path $workspaceFile -Encoding UTF8
function Read-Workspace { Get-Content $workspaceFile -Raw | ConvertFrom-Json }
$realWorkspace = Join-Path $env:APPDATA 'PuTTY Modern\workspace.json'
$realWorkspaceBefore = if (Test-Path $realWorkspace) { (Get-Item $realWorkspace).LastWriteTimeUtc.Ticks } else { 0 }

New-Item -ItemType Directory -Force -Path (Split-Path $Screenshot) | Out-Null
$app = $null
try {
    $env:PUTTYMODERN_TEST_REGISTRY_ROOT = $scratch
    $app = Start-Process -FilePath $Exe -PassThru
    $env:PUTTYMODERN_TEST_REGISTRY_ROOT = $null

    Check 'the sessions panel lists a saved session' (Wait-Element 'e2e alpha')
    Check 'it lists the other one too' ((Ui @('search', 'e2e beta')) -match 'Found [1-9]')
    Check '"Default Settings" is not offered as a session' ((Ui @('search', 'Default Settings')) -match 'Found 0')
    Check 'the panel says how many there are' ((Ui @('search', '2 saved sessions')) -match 'Found [1-9]')

    # --- the tree: folders and sessions from the workspace file, the host under each name, RECENT
    $listing = Ui @('inspect', '--depth', '14')
    Check 'a folder from the workspace file holds its session, and the other session is at the top' ($listing -match 'Folder:Team[\s\S]*Session:e2e alpha[\s\S]*Session:e2e beta')
    Check 'the host is shown under a session name' ((Ui @('search', '127.0.0.1')) -match 'Found [2-9]')
    Check 'RECENT lists the session opened lately' ((Ui @('search', 'Recent:e2e beta')) -match 'Found [1-9]')
    Check 'and says when' ((Ui @('search', 'Yesterday')) -match 'Found [1-9]')

    # --- a folder closes and opens (its chevron is a button)
    [void](Ui @('invoke', 'Toggle:Team'))
    Start-Sleep -Milliseconds 800
    Check 'closing a folder hides its sessions' ((Ui @('search', 'Session:e2e alpha')) -match 'Found 0')
    [void](Ui @('invoke', 'Toggle:Team'))
    Start-Sleep -Milliseconds 800
    Check 'opening it shows them again' ((Ui @('search', 'Session:e2e alpha')) -match 'Found [1-9]')

    # --- a new folder from the plus button; a name that exists is refused inside the dialog
    function New-Folder($name) {
        [void](Ui @('invoke', 'AddToSessionsButton'))
        if (-not (Wait-Element 'New folder...' 10 @('--type', 'MenuItem'))) { return $false }
        [void](Ui @('invoke', 'New folder...', '--type', 'MenuItem'))
        if (-not (Wait-Element 'FolderNameBox' 10)) { return $false }
        [void](Ui @('set-value', 'FolderNameBox', $name))
        [void](Ui @('invoke', 'Create', '--type', 'Button'))
        return $true
    }
    Check 'the plus offers a new folder and a dialog asks for its name' (New-Folder 'Lab')
    Check 'the new folder appears in the tree' (Wait-Element 'Folder:Lab' 10)
    Check 'and is saved in the workspace file' (@((Read-Workspace).folders | ForEach-Object name) -contains 'Lab')
    [void](New-Folder 'lab')
    Check 'the same name in another case is refused in the dialog, which stays open' (Wait-Element 'There is already a folder' 10)
    [void](Ui @('invoke', 'Cancel', '--type', 'Button'))
    Start-Sleep -Milliseconds 800
    Check 'the refused name changed nothing' (@((Read-Workspace).folders).Count -eq 2)

    # --- a session moves into a folder through its right-click menu
    [void](Ui @('click', 'Session:e2e beta', '--right'))
    Check 'a right click on a session opens its menu' (Wait-Element 'Move to folder' 10 @('--type', 'MenuItem'))
    [void](Ui @('invoke', 'Move to folder', '--type', 'MenuItem'))
    Check 'the menu lists the folders' (Wait-Element 'Lab' 10 @('--type', 'MenuItem'))
    [void](Ui @('invoke', 'Lab', '--type', 'MenuItem'))
    Start-Sleep -Milliseconds 1000
    $labId = (@((Read-Workspace).folders) | Where-Object name -eq 'Lab').id
    Check 'the session is in the folder, in the workspace file' ((Read-Workspace).sessions.'e2e beta'.folderId -eq $labId)
    Check 'and the tree shows it under the folder' ((Ui @('inspect', '--depth', '14')) -match 'Folder:Lab[\s\S]*Session:e2e beta')

    # --- rename the folder, then search flattens the tree
    [void](Ui @('click', 'Folder:Lab', '--right'))
    Check 'a right click on a folder opens its menu' (Wait-Element 'Rename...' 10 @('--type', 'MenuItem'))
    [void](Ui @('invoke', 'Rename...', '--type', 'MenuItem'))
    [void](Wait-Element 'FolderNameBox' 10)
    [void](Ui @('set-value', 'FolderNameBox', 'Lab2'))
    [void](Ui @('invoke', 'Rename', '--type', 'Button'))
    Check 'the folder has its new name' (Wait-Element 'Folder:Lab2' 10)
    Check 'in the workspace file too' (@((Read-Workspace).folders | ForEach-Object name) -contains 'Lab2')
    [void](Ui @('set-value', 'SessionSearch', 'beta'))
    Start-Sleep -Milliseconds 1000
    Check 'searching shows the matching session' ((Ui @('search', 'Session:e2e beta')) -match 'Found [1-9]')
    Check 'and no folders' ((Ui @('search', 'Folder:Team')) -match 'Found 0')
    [void](Ui @('set-value', 'SessionSearch', ' '))      # winapp refuses an empty value; the panel trims the text
    Start-Sleep -Milliseconds 1000
    Check 'clearing the search brings the tree back' ((Ui @('search', 'Folder:Team')) -match 'Found [1-9]')

    # --- deleting a folder moves what is in it up; no session is lost
    [void](Ui @('click', 'Folder:Lab2', '--right'))
    [void](Wait-Element 'Delete folder' 10 @('--type', 'MenuItem'))
    [void](Ui @('invoke', 'Delete folder', '--type', 'MenuItem'))
    Check 'deleting a folder asks first' (Wait-Element 'No saved session is deleted' 10)
    [void](Ui @('invoke', 'Delete folder', '--type', 'Button'))
    Start-Sleep -Milliseconds 1000
    Check 'the folder is gone' ((Ui @('search', 'Folder:Lab2')) -match 'Found 0')
    Check 'its session is at the top again, in the file and on screen' ($null -eq (Read-Workspace).sessions.'e2e beta'.folderId -and (Ui @('search', 'Session:e2e beta')) -match 'Found [1-9]')

    # --- open alpha from its context menu
    Open-Session 'e2e alpha'
    $accept = $alpha.AcceptTcpClientAsync()
    Check 'opening a saved session connects to its saved host and port' ($accept.Wait(20000))
    if ($accept.IsCompleted) {
        $stream = $accept.Result.GetStream()
        $bytes = [Text.Encoding]::UTF8.GetBytes("$esc[32mALPHA IS CONNECTED$esc[0m`r`n")
        $stream.Write($bytes, 0, $bytes.Length)
        Start-Sleep -Seconds 3
        & winapp ui screenshot -a PuTTY.App --output $Screenshot 2>&1 | Out-Null
        $green = if (Test-Path $Screenshot) { [SessionsSmokeNative]::CountColour($Screenshot, 0, 187, 0) } else { 0 }
        Check 'the terminal shows what the first session sends' ($green -gt 30) "($green green pixels)"
        Check 'the document tab is named after the session' ((Ui @('search', 'e2e alpha', '--type', 'TabItem')) -match 'Found [1-9]')

        # --- the details panel, and PuTTY's Change Settings on the running session
        Check 'the details panel names the open session' ((Ui @('get-property', 'SessionDetailsName')) -match 'e2e alpha')
        # --- the connection log and the status bar
        Check 'the connection log has PuTTY''s own line for the session, with its name' (Wait-Element 'e2e alpha Connecting to' 15)
        Check 'the status bar counts the connected session' (Wait-Element '1 active session' 10)
        Check 'and says it is connected' ((Ui @('get-property', 'SessionDetailsStatus')) -match 'Connected')
        [void](Ui @('invoke', 'ChangeSettingsButton'))
        Check 'Session settings opens PuTTY''s Change Settings, with Apply instead of Open' (Wait-Element 'Apply' 20 @('--type', 'Button'))
        Check 'the host name is not offered while connected' ((Ui @('search', 'Host Name (or IP address)', '--type', 'Edit')) -match 'Found 0')
        [void](Ui @('invoke', 'Window', '--type', 'TreeItem'))
        [void](Wait-Element 'Behaviour' 15 @('--type', 'TreeItem'))
        [void](Ui @('invoke', 'Behaviour', '--type', 'TreeItem'))
        Check 'the Behaviour page has the window title' (Wait-Element 'Window title:' 15 @('--type', 'Edit'))
        [void](Ui @('set-value', 'Window title:', 'Changed by Apply', '--type', 'Edit'))
        [void](Ui @('invoke', 'Apply', '--type', 'Button'))
        $closed = $false
        $deadline = (Get-Date).AddSeconds(15)
        while ((Get-Date) -lt $deadline -and -not $closed) {
            $closed = (Ui @('search', 'Apply', '--type', 'Button')) -match 'Found 0'
            if (-not $closed) { Start-Sleep -Milliseconds 400 }
        }
        Check 'Apply closes the settings window' $closed
        $titled = $false
        $deadline = (Get-Date).AddSeconds(15)
        while ((Get-Date) -lt $deadline -and -not $titled) {
            $titled = (Ui @('list-windows')) -match 'Changed by Apply - PuTTY'
            if (-not $titled) { Start-Sleep -Milliseconds 400 }
        }
        Check 'the running session took the new window title, which PuTTY sets through its own reconfiguration' $titled
        Check 'and is still connected' ((Ui @('get-property', 'SessionDetailsStatus')) -match 'Connected')

        # --- open beta while alpha is connected: a second tab, and nothing is asked or closed
        Check 'the session just opened is at the top of RECENT' ((Ui @('search', 'Recent:e2e alpha')) -match 'Found [1-9]')
        Check 'and the workspace file counts the opening' ((Read-Workspace).sessions.'e2e alpha'.openCount -ge 1 -and $null -ne (Read-Workspace).sessions.'e2e alpha'.lastOpened)

        $alphaClient = $accept.Result
        function Is-Closed($client) { $client.Client.Poll(300000, [System.Net.Sockets.SelectMode]::SelectRead) -and $client.Client.Available -eq 0 }
        Open-Session 'e2e beta'
        $acceptBeta = $beta.AcceptTcpClientAsync()
        Check 'the second session connects' ($acceptBeta.Wait(20000))
        Check 'opening a second session while one is connected does not ask anything' ((Ui @('search', 'Close and open')) -match 'Found 0')
        Check 'there is a tab for each session' ((Wait-Element 'e2e beta' 15 @('--type', 'TabItem')) -and (Ui @('search', 'e2e alpha', '--type', 'TabItem')) -match 'Found [1-9]')
        Check 'the status bar counts both' (Wait-Element '2 active sessions' 10)
        Check 'the log has the lines of both sessions' ((Wait-Element 'e2e beta Connecting to' 10) -and (Ui @('search', 'e2e alpha Connecting to')) -match 'Found [1-9]')
        [void](Ui @('invoke', 'ConnectionLogClear'))
        Start-Sleep -Milliseconds 800
        Check 'Clear empties the log' ((Ui @('search', 'Connecting to')) -match 'Found 0')
        if ($acceptBeta.IsCompleted) {
            $betaClient = $acceptBeta.Result
            $streamBeta = $betaClient.GetStream()
            $bytes = [Text.Encoding]::UTF8.GetBytes("$esc[31mBETA IS CONNECTED$esc[0m`r`n")
            $streamBeta.Write($bytes, 0, $bytes.Length)
            Start-Sleep -Seconds 3
            $second = $Screenshot -replace '\.png$', '-second.png'
            & winapp ui screenshot -a PuTTY.App --output $second 2>&1 | Out-Null
            $red = [SessionsSmokeNative]::CountColour($second, 187, 0, 0)
            $greenAfter = [SessionsSmokeNative]::CountColour($second, 0, 187, 0)
            Check 'the terminal now shows the second session' ($red -gt 30) "($red red pixels)"
            Check 'and not the first, which is in its own tab' ($greenAfter -lt 5) "($greenAfter green pixels)"
            Check 'the first connection stays open' (-not (Is-Closed $alphaClient))
            Check 'the details panel names the second session' ((Ui @('get-property', 'SessionDetailsName')) -match 'e2e beta')

            # --- back to the first tab: its terminal is there as it was
            [void](Ui @('invoke', 'e2e alpha', '--type', 'TabItem'))
            Start-Sleep -Seconds 3
            $back = $Screenshot -replace '\.png$', '-back.png'
            & winapp ui screenshot -a PuTTY.App --output $back 2>&1 | Out-Null
            $greenBack = [SessionsSmokeNative]::CountColour($back, 0, 187, 0)
            $redBack = [SessionsSmokeNative]::CountColour($back, 187, 0, 0)
            Check 'choosing the first tab shows its terminal again' ($greenBack -gt 30) "($greenBack green pixels)"
            Check 'and not the second' ($redBack -lt 5) "($redBack red pixels)"
            Check 'the details panel follows the tab' ((Ui @('get-property', 'SessionDetailsName')) -match 'e2e alpha')

            # --- closing a tab ends its session and only that one
            $tree = Ui @('inspect', '--depth', '16')
            $closeSlug = if ($tree -match 'TabItem "e2e alpha"[\s\S]*?(btn-partclosebutton-[0-9a-f]+)') { $Matches[1] } else { $null }
            Check 'the first tab has a close button' ($null -ne $closeSlug)
            if ($closeSlug) {
                [void](Ui @('invoke', $closeSlug))
                $closed = $false
                $deadline = (Get-Date).AddSeconds(10)
                while ((Get-Date) -lt $deadline -and -not $closed) {
                    $closed = Is-Closed $alphaClient
                    if (-not $closed) { Start-Sleep -Milliseconds 300 }
                }
                Check 'closing the first tab closed its connection' $closed
                Check 'and its tab is gone' ((Ui @('search', 'e2e alpha', '--type', 'TabItem')) -match 'Found 0')
                Check 'the status bar counts one session again' (Wait-Element '1 active session' 10)
                Check 'the second session was not touched' ((-not (Is-Closed $betaClient)) -and (Ui @('search', 'e2e beta', '--type', 'TabItem')) -match 'Found [1-9]')

                # --- Duplicate session: another tab with a copy of this session's settings
                $acceptDuplicate = $beta.AcceptTcpClientAsync()
                [void](Ui @('invoke', 'DuplicateButton'))
                Check 'Duplicate session opens a second connection to the same server' ($acceptDuplicate.Wait(20000))
                Check 'in a tab of its own, beside the first' ((Ui @('inspect', '--depth', '16')) -match 'TabItem "e2e beta"[\s\S]*TabItem "e2e beta"')

                # --- the server hangs up on the duplicate: the panel says so, and Reconnect brings it back in the same tab
                if ($acceptDuplicate.IsCompleted) {
                    $duplicateClient = $acceptDuplicate.Result
                    $acceptAgain = $beta.AcceptTcpClientAsync()
                    $duplicateClient.Close()
                    $notConnected = $false
                    $deadline = (Get-Date).AddSeconds(15)
                    while ((Get-Date) -lt $deadline -and -not $notConnected) {
                        $notConnected = (Ui @('get-property', 'SessionDetailsStatus')) -match 'Not connected'
                        if (-not $notConnected) { Start-Sleep -Milliseconds 400 }
                    }
                    Check 'when the server hangs up the details panel says Not connected' $notConnected
                    Check 'and Reconnect, which was disabled while connected, can now be used' ((Ui @('get-property', 'ReconnectButton')) -match 'IsEnabled:\s*True')
                    [void](Ui @('invoke', 'ReconnectButton'))
                    Check 'Reconnect connects again to the same server' ($acceptAgain.Wait(20000))
                    $again = if ($acceptAgain.IsCompleted) { $acceptAgain.Result } else { $null }
                    if ($again) {
                        $bytes = [Text.Encoding]::UTF8.GetBytes("$esc[32mBACK AGAIN$esc[0m`r`n")
                        $again.GetStream().Write($bytes, 0, $bytes.Length)
                        Start-Sleep -Seconds 3
                        $reconnected = $Screenshot -replace '\.png$', '-reconnected.png'
                        & winapp ui screenshot -a PuTTY.App --output $reconnected 2>&1 | Out-Null
                        $greenAgain = [SessionsSmokeNative]::CountColour($reconnected, 0, 187, 0)
                        Check 'the reconnected session draws in the same tab' ($greenAgain -gt 30) "($greenAgain green pixels)"
                    }

                    Check 'and the panel says Connected again' ((Ui @('get-property', 'SessionDetailsStatus')) -match 'Name:\s*Connected')
                    Check 'the first of the two beta tabs was not disturbed' (-not (Is-Closed $betaClient))
                }
            }
        }
    }
}
finally {
    $env:PUTTYMODERN_TEST_REGISTRY_ROOT = $null
    $closedCleanly = $false
    if ($app -and -not $app.HasExited) { [void]$app.CloseMainWindow(); [void]$app.WaitForExit(10000) }
    if ($app) {
        if ($app.HasExited) { $closedCleanly = ($app.ExitCode -eq 0) } else { $app.Kill() }
        Check 'the app exits cleanly when its window is closed' $closedCleanly "(exit code $(if ($app.HasExited) { $app.ExitCode } else { 'still running' }))"
    }
    $alpha.Stop(); $beta.Stop()
    $realAfter = if (Test-Path $realSessions) { (Get-ChildItem $realSessions | Measure-Object).Count } else { 0 }
    Check "the user's real PuTTY sessions were not touched" ($realBefore -eq $realAfter) "($realBefore before, $realAfter after)"
    $realWorkspaceAfter = if (Test-Path $realWorkspace) { (Get-Item $realWorkspace).LastWriteTimeUtc.Ticks } else { 0 }
    Check "the user's real workspace file was not touched" ($realWorkspaceBefore -eq $realWorkspaceAfter)
    Remove-Item -Path $workspaceDir -Recurse -Force -ErrorAction SilentlyContinue
    Remove-Item -Path "HKCU:\Software\PuttyModernTests\$($scratch.Split('\')[-1])" -Recurse -Force -ErrorAction SilentlyContinue
}
exit $failures
