<#
.SYNOPSIS
  End-to-end check of PuTTY features beyond a plain connection, in the real app: session logging (the question about a
  log file that exists already: append, overwrite, or no logging).

.DESCRIPTION
  Run it in Windows Sandbox with tools\run-in-sandbox.ps1 -Smoke features. The user's real PuTTY settings are neither
  read nor written: the app is started with PUTTYMODERN_TEST_REGISTRY_ROOT (see TestRegistry.cs), which points its
  registry at a scratch key with saved sessions made for the checks. Each feature adds a section below.

  Prints PASS or FAIL per check; the exit code is the number of failures.
#>
param(
    [Parameter(Mandatory)] [string] $Exe,
    [string] $Screenshot = ''
)

if ($env:USERNAME -ne 'WDAGUtilityAccount') {
    Write-Host 'This starts the real app and opens windows: run it in Windows Sandbox with tools\run-in-sandbox.ps1 -Smoke features.'
    exit 99
}
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
if (-not $Screenshot) { $Screenshot = Join-Path $root 'artifacts\spike\app-features.png' }
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
function Open-Session($name) {
    [void](Ui @('invoke', "Session:$name"))
    for ($i = 0; $i -lt 20; $i++) {
        try { [void](Ui @('invoke', 'OpenSessionButton')); return } catch { Start-Sleep -Milliseconds 500 }
    }
    [void](Ui @('invoke', 'OpenSessionButton'))
}
# A file the app has open for writing, read without asking for exclusive access.
function Read-Shared($path) {
    if (-not (Test-Path $path)) { return '' }
    $stream = [System.IO.File]::Open($path, 'Open', 'Read', 'ReadWrite, Delete')
    try { return (New-Object System.IO.StreamReader($stream)).ReadToEnd() } finally { $stream.Dispose() }
}
function Wait-Log($path, $text, $seconds = 15) {
    $deadline = (Get-Date).AddSeconds($seconds)
    while ((Get-Date) -lt $deadline) {
        if ((Read-Shared $path).Contains($text)) { return $true }
        Start-Sleep -Milliseconds 400
    }
    return $false
}

# --- the scratch "HKEY_CURRENT_USER" with the saved sessions
$leaf = "e2e-features-$([guid]::NewGuid().ToString('N'))"
$scratch = "Software\PuttyModernTests\$leaf"
$sessionsKey = "HKCU:\$scratch\Software\SimonTatham\PuTTY\Sessions"
function Save-Session($name, $port, [hashtable] $more = @{}) {
    $key = Join-Path $sessionsKey ($name -replace ' ', '%20')
    New-Item -Path $key -Force | Out-Null
    New-ItemProperty -Path $key -Name 'HostName' -Value '127.0.0.1' -PropertyType String | Out-Null
    New-ItemProperty -Path $key -Name 'PortNumber' -Value $port -PropertyType DWord | Out-Null
    New-ItemProperty -Path $key -Name 'Protocol' -Value 'raw' -PropertyType String | Out-Null
    foreach ($name in $more.Keys) {
        $value = $more[$name]
        if ($value -is [int]) { New-ItemProperty -Path $key -Name $name -Value $value -PropertyType DWord | Out-Null }
        else { New-ItemProperty -Path $key -Name $name -Value $value -PropertyType String | Out-Null }
    }
}
function New-Listener { $l = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Loopback, 0); $l.Start(); return $l }
function Send-Text($task, $text) {
    $bytes = [Text.Encoding]::UTF8.GetBytes($text)
    $task.Result.GetStream().Write($bytes, 0, $bytes.Length)
}

$workDir = Join-Path $env:TEMP "PuttyModernTests\$leaf"
New-Item -ItemType Directory -Force -Path $workDir | Out-Null
New-Item -ItemType Directory -Force -Path (Split-Path $Screenshot) | Out-Null

# Session logging: LogType 2 (all session output), LogFileClash -1 (ask), LogHeader 0 (no header line)
$logAppend = Join-Path $workDir 'append.log';       [System.IO.File]::WriteAllText($logAppend, "OLD APPEND CONTENT`r`n")
$logOverwrite = Join-Path $workDir 'overwrite.log'; [System.IO.File]::WriteAllText($logOverwrite, "OLD OVERWRITE CONTENT`r`n")
$logOff = Join-Path $workDir 'off.log';             [System.IO.File]::WriteAllText($logOff, "OLD OFF CONTENT`r`n")
$append = New-Listener; $overwrite = New-Listener; $off = New-Listener
Save-Session 'e2e log append' $append.LocalEndpoint.Port @{ LogType = 2; LogFileName = $logAppend; LogFileClash = -1; LogHeader = 0 }
Save-Session 'e2e log overwrite' $overwrite.LocalEndpoint.Port @{ LogType = 2; LogFileName = $logOverwrite; LogFileClash = -1; LogHeader = 0 }
Save-Session 'e2e log off' $off.LocalEndpoint.Port @{ LogType = 2; LogFileName = $logOff; LogFileClash = -1; LogHeader = 0 }

$app = $null
try {
    $env:PUTTYMODERN_TEST_REGISTRY_ROOT = $scratch
    $app = Start-Process -FilePath $Exe -PassThru
    $env:PUTTYMODERN_TEST_REGISTRY_ROOT = $null
    Check 'the sessions panel lists the saved sessions' (Wait-Element 'Session:e2e log append' 40)

    # --- session logging
    # PuTTY opens the log file when the first output is to be written, so the server speaks first.
    $accepted = $append.AcceptTcpClientAsync()
    Open-Session 'e2e log append'
    Check 'the session connects' ($accepted.Wait(20000))
    Send-Text $accepted 'APPENDED TEXT'
    Check 'a log file that exists is asked about' (Wait-Element 'Disable logging' 30 @('--type', 'Button'))
    [void](Ui @('invoke', 'Append', '--type', 'Button'))
    Check 'Append: the session output is added to the file' (Wait-Log $logAppend 'APPENDED TEXT')
    Check 'and what was there stays' ((Read-Shared $logAppend).StartsWith('OLD APPEND CONTENT'))

    $accepted = $overwrite.AcceptTcpClientAsync()
    Open-Session 'e2e log overwrite'
    [void]$accepted.Wait(20000)
    Send-Text $accepted 'OVERWRITTEN TEXT'
    Check 'the second file is asked about too' (Wait-Element 'Overwrite' 30 @('--type', 'Button'))
    [void](Ui @('invoke', 'Overwrite', '--type', 'Button'))
    Check 'Overwrite: the session output is in the file' (Wait-Log $logOverwrite 'OVERWRITTEN TEXT')
    Check 'and the old content is gone' (-not (Read-Shared $logOverwrite).Contains('OLD OVERWRITE'))

    $accepted = $off.AcceptTcpClientAsync()
    Open-Session 'e2e log off'
    Check 'the third session connects' ($accepted.Wait(20000))
    Send-Text $accepted 'NOT LOGGED'
    Check 'the third file is asked about' (Wait-Element 'Disable logging' 30 @('--type', 'Button'))
    [void](Ui @('invoke', 'Disable logging', '--type', 'Button'))
    Send-Text $accepted 'MORE TEXT'
    Check 'Disable logging: the session carries on' ((Ui @('get-property', 'SessionDetailsStatus')) -match 'Connected')
    Start-Sleep -Seconds 1
    Check 'and the file is left as it was' ((Read-Shared $logOff) -eq "OLD OFF CONTENT`r`n")
    & winapp ui screenshot -a PuTTY.App --output $Screenshot 2>&1 | Out-Null
}
finally {
    $env:PUTTYMODERN_TEST_REGISTRY_ROOT = $null
    if ($app -and -not $app.HasExited) { $app.Kill() }
    $append.Stop(); $overwrite.Stop(); $off.Stop()
    Remove-Item -Path $workDir -Recurse -Force -ErrorAction SilentlyContinue
    Remove-Item -Path "HKCU:\Software\PuttyModernTests\$leaf" -Recurse -Force -ErrorAction SilentlyContinue
}
exit $failures
