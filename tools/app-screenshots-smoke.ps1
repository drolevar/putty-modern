<#
.SYNOPSIS
  Makes the pictures of the README (the main window with two terminals, the settings window) from the real app in Windows
  Sandbox, in the light theme, and checks on the way that what they show is on screen.

.DESCRIPTION
  Run it in Windows Sandbox with tools\run-in-sandbox.ps1 -Smoke screenshots; the pictures come back in artifacts\spike\readme-*.png
  (copy the ones you want to docs\images). It refuses to run anywhere else, because it adds two names to the machine's hosts file
  (build-01.example.com and web-01.example.com, both pointing at the loopback address, so that the session list and the details
  panel show host names and not 127.0.0.1) and takes them out again at the end.

  The content is made up: two sessions of the raw kind talk to small servers in this script, which send what a shell and a log
  tail would (colours, box drawing characters); the other saved sessions are only listed (SSH, Telnet, a serial line) and are
  never opened. The app's registry and workspace file are scratch copies (PUTTYMODERN_TEST_REGISTRY_ROOT), so nothing of a real
  user's is read or written.

  Prints PASS or FAIL per check; the exit code is the number of failures.
#>
param(
    [Parameter(Mandatory)] [string] $Exe
)

$ErrorActionPreference = 'Stop'
if ($env:USERNAME -ne 'WDAGUtilityAccount') {
    Write-Host 'This changes the hosts file: it runs only in Windows Sandbox (tools\run-in-sandbox.ps1 -Smoke screenshots).'
    exit 97
}
$esc = [string][char]27
$failures = 0
$root = Split-Path -Parent $PSScriptRoot
function Check($name, $ok, $detail = '') {
    if ($ok) { Write-Host "PASS  $name $detail" } else { Write-Host "FAIL  $name $detail"; $script:failures++ }
}
function Clean($lines) { ($lines | ForEach-Object { "$_" -replace ($esc + '\[[0-9;]*m'), '' }) -join "`n" }
function Ui { param([string[]] $UiArgs) Clean (& winapp ui @UiArgs -a PuTTY.App 2>&1) }
function Try-Ui { param([string[]] $UiArgs) try { Ui $UiArgs } catch { '' } }
function Wait-Element($selector, $seconds = 30, [string[]] $Extra = @()) {
    $deadline = (Get-Date).AddSeconds($seconds)
    while ((Get-Date) -lt $deadline) {
        if ((Try-Ui (@('search', $selector) + $Extra)) -match 'Found [1-9]') { return $true }
        Start-Sleep -Milliseconds 400
    }
    return $false
}
. (Join-Path $PSScriptRoot 'smoke-common.ps1')       # Bring-AppToFront
function Open-Session($name) {
    Bring-AppToFront
    [void](Try-Ui @('click', "Session:$name", '--right'))
    for ($i = 0; $i -lt 20; $i++) {
        $menu = Try-Ui @('invoke', 'Open', '--type', 'MenuItem')
        if ($menu -notmatch 'No element|not found|error') { return }
        Start-Sleep -Milliseconds 500
    }
}
$pictures = Join-Path $root 'artifacts\spike'
New-Item -ItemType Directory -Force -Path $pictures | Out-Null

# ---- what the two terminals show (each pane is about 45 columns wide: keep the lines short)
$prompt = "$esc[01;32mdeploy@build-01$esc[00m:$esc[01;34m~$esc[00m$ "
$bar = [string][char]0x2502; $tee = [string][char]0x251C; $ell = [string][char]0x2514; $dash = [string][char]0x2500
$buildText = (@(
        "${prompt}uptime",
        " 09:40:55 up 41 days,  3:12,  2 users",
        "${prompt}ls",
        "$esc[01;36mcurrent$esc[0m  $esc[01;32mdeploy.sh$esc[0m  notes.txt  $esc[01;34mreleases$esc[0m",
        "${prompt}git status -sb",
        "## $esc[32mmain$esc[0m...$esc[31morigin/main$esc[0m [ahead 1]",
        " $esc[31mM$esc[0m src/app.c",
        "$esc[31m??$esc[0m notes.txt",
        "${prompt}tree releases",
        "releases",
        "$tee$dash$dash $esc[01;34m2.4.0$esc[0m",
        "$bar   $tee$dash$dash app",
        "$bar   $ell$dash$dash config.yml",
        "$ell$dash$dash $esc[01;34m2.4.1$esc[0m",
        "    $tee$dash$dash app",
        "    $ell$dash$dash config.yml",
        "$prompt") -join "`r`n")
$logText = (@(
        "deploy@web-01:~`$ tail -f service.log",
        "$esc[90m09:41:02$esc[0m $esc[32mINFO $esc[0m listening on :8443",
        "$esc[90m09:41:07$esc[0m $esc[32mINFO $esc[0m GET /api/orders 200 14ms",
        "$esc[90m09:41:07$esc[0m $esc[32mINFO $esc[0m GET /api/orders/812 9ms",
        "$esc[90m09:41:12$esc[0m $esc[33mWARN $esc[0m slow query (1.8 s)",
        "$esc[90m09:41:13$esc[0m $esc[32mINFO $esc[0m POST /api/orders 201",
        "$esc[90m09:41:19$esc[0m $esc[01;31mERROR$esc[0m upstream timeout",
        "$esc[90m09:41:19$esc[0m $esc[32mINFO $esc[0m retry 1 of 3 in 2 s",
        "$esc[90m09:41:21$esc[0m $esc[32mINFO $esc[0m POST /api/payments 200",
        "$esc[90m09:41:26$esc[0m $esc[32mINFO $esc[0m GET /api/orders/813 11ms",
        "$esc[90m09:41:31$esc[0m $esc[32mINFO $esc[0m GET /api/orders 200 12ms",
        "$esc[90m09:41:33$esc[0m $esc[33mWARN $esc[0m cache miss ratio 0.41",
        "$esc[90m09:41:40$esc[0m $esc[32mINFO $esc[0m GET /health 200 1ms",
        '') -join "`r`n")

# A server that sends its text to the first client and then keeps the connection open.
$keep = New-Object System.Collections.Generic.List[object]
function Start-Demo($text) {
    $listener = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Loopback, 0)
    $listener.Start()
    $task = $listener.AcceptTcpClientAsync()
    $script:keep.Add($listener)
    [pscustomobject]@{ Listener = $listener; Task = $task; Text = $text; Port = $listener.LocalEndpoint.Port }
}
function Send-Demo($demo) {
    if ($demo.Task.Wait(20000)) {
        $client = $demo.Task.Result
        $script:keep.Add($client)
        $bytes = [System.Text.Encoding]::UTF8.GetBytes($demo.Text)
        $client.GetStream().Write($bytes, 0, $bytes.Length)
        return $true
    }
    return $false
}
$build = Start-Demo $buildText
$logs = Start-Demo $logText

# ---- the machine's hosts file, for the two names (taken out again at the end)
$hosts = Join-Path $env:SystemRoot 'System32\drivers\etc\hosts'
$hostsBefore = Get-Content $hosts -Raw
Add-Content -Path $hosts -Value "`r`n127.0.0.1 build-01.example.com`r`n127.0.0.1 web-01.example.com # putty-modern screenshots"

# ---- the scratch registry: saved sessions in PuTTY's own format
$leaf = "e2e-shots-$([guid]::NewGuid().ToString('N'))"
$scratch = "Software\PuttyModernTests\$leaf"
$sessionsKey = "HKCU:\$scratch\Software\SimonTatham\PuTTY\Sessions"
function Save-Session($name, $hostName, $port, $protocol, $user = '') {
    $key = Join-Path $sessionsKey ($name -replace ' ', '%20')
    New-Item -Path $key -Force | Out-Null
    New-ItemProperty -Path $key -Name 'HostName' -Value $hostName -PropertyType String | Out-Null
    New-ItemProperty -Path $key -Name 'PortNumber' -Value $port -PropertyType DWord | Out-Null
    New-ItemProperty -Path $key -Name 'Protocol' -Value $protocol -PropertyType String | Out-Null
    if ($user) { New-ItemProperty -Path $key -Name 'UserName' -Value $user -PropertyType String | Out-Null }
}
Save-Session 'Default Settings' '' 22 'ssh'
Save-Session 'web-01' 'web-01.example.com' $logs.Port 'raw'          # these two are opened, against the servers above
Save-Session 'build-01' 'build-01.example.com' $build.Port 'raw'
Save-Session 'web-02' 'web-02.example.com' 22 'ssh' 'deploy'
Save-Session 'db-01' 'db-01.example.com' 22 'ssh' 'admin'
Save-Session 'staging-web' 'staging.example.com' 22 'ssh' 'deploy'
Save-Session 'home-router' '192.168.1.1' 23 'telnet'
$serial = Join-Path $sessionsKey 'bench-serial'
New-Item -Path $serial -Force | Out-Null
New-ItemProperty -Path $serial -Name 'Protocol' -Value 'serial' -PropertyType String | Out-Null
New-ItemProperty -Path $serial -Name 'SerialLine' -Value 'COM3' -PropertyType String | Out-Null
New-ItemProperty -Path $serial -Name 'SerialSpeed' -Value 115200 -PropertyType DWord | Out-Null
New-ItemProperty -Path $serial -Name 'HostName' -Value '' -PropertyType String | Out-Null

# The folders and the recents are the app's own file (moved under %TEMP% by the test hook).
$workspaceDir = Join-Path $env:TEMP "PuttyModernTests\$leaf"
New-Item -ItemType Directory -Force -Path $workspaceDir | Out-Null
$yesterday = (Get-Date).AddDays(-1).ToString('o')
$lastWeek = (Get-Date).AddDays(-4).ToString('o')
@"
{ "version": 1,
  "folders": [ { "id": "f1", "name": "Production" }, { "id": "f2", "name": "Staging" } ],
  "sessions": {
    "web-02": { "folderId": "f1", "lastOpened": "$yesterday", "openCount": 5 },
    "db-01": { "folderId": "f1", "lastOpened": "$lastWeek", "openCount": 2 },
    "staging-web": { "folderId": "f2", "lastOpened": "$lastWeek", "openCount": 1 },
    "home-router": { "lastOpened": "$lastWeek", "openCount": 3 }
  } }
"@ | Set-Content -Path (Join-Path $workspaceDir 'workspace.json') -Encoding UTF8

$app = $null
try {
    $env:PUTTYMODERN_TEST_REGISTRY_ROOT = $scratch
    $app = Start-Process -FilePath $Exe -PassThru
    $env:PUTTYMODERN_TEST_REGISTRY_ROOT = $null

    Check 'the sessions panel lists the saved sessions' (Wait-Element 'Session:web-01' 40)
    Check 'the folders are there' (((Try-Ui @('search', 'Folder:Production')) -match 'Found [1-9]') -and ((Try-Ui @('search', 'Folder:Staging')) -match 'Found [1-9]'))
    Check 'the serial session shows its line' ((Try-Ui @('search', 'COM3')) -match 'Found [1-9]')

    Open-Session 'build-01'
    Check 'the first server is reached' (Send-Demo $build)
    Check 'the shell text is on screen' (Wait-Element 'TerminalScreenText' 20)
    Open-Session 'web-01'
    Check 'the second server is reached' (Send-Demo $logs)
    Start-Sleep -Seconds 2

    # side by side is the button's own default; with two tabs open it arranges them
    Bring-AppToFront
    [void](Try-Ui @('invoke', 'CommandSplitPane'))
    Start-Sleep -Seconds 3
    $text = Try-Ui @('get-property', 'TerminalScreenText')
    Check 'a terminal shows its text to UI Automation' ($text -match 'releases|service\.log|INFO')
    & winapp ui screenshot -a PuTTY.App --output (Join-Path $pictures 'readme-main.png') 2>&1 | Out-Null
    Check 'the main window picture is made' (Test-Path (Join-Path $pictures 'readme-main.png'))

    # the settings window, on the Session page, with a host typed in
    [void](Try-Ui @('invoke', 'CommandNewSession'))
    Check 'the settings window opens' (Wait-Element 'SettingsPages' 30)
    [void](Try-Ui @('set-value', 'Host Name (or IP address)', 'web-02.example.com', '--type', 'Edit'))
    Start-Sleep -Seconds 1
    $listing = (& winapp ui list-windows 2>&1) -join ' '
    $m = [regex]::Match($listing, 'HWND (\d+): "New session"')
    if ($m.Success) {
        & winapp ui screenshot --window ([int64]$m.Groups[1].Value) --output (Join-Path $pictures 'readme-settings.png') 2>&1 | Out-Null
    }
    Check 'the settings window picture is made' (Test-Path (Join-Path $pictures 'readme-settings.png'))
    [void](Try-Ui @('invoke', 'Cancel', '--type', 'Button'))
}
finally {
    if ($app -and -not $app.HasExited) { $app.Kill() }
    foreach ($item in $keep) { try { $item.Dispose() } catch { try { $item.Stop() } catch { } } }
    Set-Content -Path $hosts -Value $hostsBefore -NoNewline
    Remove-Item -Recurse -Force "HKCU:\$scratch" -ErrorAction SilentlyContinue
    Remove-Item -Recurse -Force $workspaceDir -ErrorAction SilentlyContinue
}

Write-Host ''
Write-Host "$failures failure(s)"
exit $failures
