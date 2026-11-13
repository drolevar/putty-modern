<#
.SYNOPSIS
  End-to-end check of what the real app does when the engine hits a fatal error: PuTTY's own modalfatalbox (the path an
  out-of-memory takes) and then a failed assertion in the engine (the way PuTTY's live assertions fail, an abort). With two
  sessions open the workspace is saved first and the reason goes to the crash log; after modalfatalbox the process ends with
  exit code 3 instead of hanging or leaving a crash report dialog, after an abort Windows's own reporting takes over.

.DESCRIPTION
  Run it in Windows Sandbox with tools\run-in-sandbox.ps1 -Smoke crash. The fatal error is made by the test hook
  PUTTYMODERN_TEST_FATAL_AFTER (TestRegistry.cs), which raises it a number of seconds after the window is up; the same
  hook points the app's registry and workspace file at a scratch copy, so the user's real PuTTY settings and workspace
  are neither read nor written. The box that would tell the user (FatalBox) is not shown under the hook.

  Prints PASS or FAIL per check; the exit code is the number of failures.
#>
param(
    [Parameter(Mandatory)] [string] $Exe
)

$ErrorActionPreference = 'Stop'
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

$leaf = "e2e-crash-$([guid]::NewGuid().ToString('N'))"
$scratch = "Software\PuttyModernTests\$leaf"
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

$workspaceDir = Join-Path $env:TEMP "PuttyModernTests\$leaf"
$layoutFile = Join-Path $workspaceDir 'layout.json'
$crashLog = Join-Path $env:LOCALAPPDATA 'PuTTY Modern\crash.log'
Remove-Item $crashLog -ErrorAction SilentlyContinue

$app = $null
try {
    $env:PUTTYMODERN_TEST_REGISTRY_ROOT = $scratch
    $env:PUTTYMODERN_TEST_FATAL_AFTER = '60'
    $started = Get-Date
    $app = Start-Process -FilePath $Exe -PassThru
    $env:PUTTYMODERN_TEST_REGISTRY_ROOT = $null
    $env:PUTTYMODERN_TEST_FATAL_AFTER = $null

    Check 'the sessions panel lists the saved sessions' (Wait-Element 'Session:e2e alpha' 40)
    $acceptAlpha = $alpha.AcceptTcpClientAsync()
    Open-Session 'e2e alpha'
    Check 'the first session connects' ($acceptAlpha.Wait(20000))
    $acceptBeta = $beta.AcceptTcpClientAsync()
    Open-Session 'e2e beta'
    Check 'the second session connects' ($acceptBeta.Wait(20000))
    Check 'there is no workspace file yet (it is saved on the way out)' (-not (Test-Path $layoutFile))

    # --- the fatal error comes (60 s after the window was up); the process must end by itself
    $exited = $app.WaitForExit(120000)
    Check 'the app ended by itself, and did not hang' $exited
    if ($exited) {
        Check 'with exit code 3 (the engine''s own way out, no crash report)' ($app.ExitCode -eq 3) "(exit code $($app.ExitCode), after $([int]((Get-Date) - $started).TotalSeconds) s)"
    }
    Check 'the workspace was saved first' (Test-Path $layoutFile) "($layoutFile)"
    $state = if (Test-Path $layoutFile) { Get-Content $layoutFile -Raw | ConvertFrom-Json } else { $null }
    Check 'it holds both sessions, by their saved names' ($state -and @($state.sessions).Count -eq 2 -and (@($state.sessions | ForEach-Object savedName) -contains 'e2e alpha') -and (@($state.sessions | ForEach-Object savedName) -contains 'e2e beta'))
    $logText = if (Test-Path $crashLog) { Get-Content $crashLog -Raw } else { '' }
    Check 'the reason is in the crash log' ($logText -match 'Engine fatal error' -and $logText -match 'test: a fatal error of the engine') "($crashLog)"

    # --- the second time: the sessions come back from the saved workspace, and a failed assertion (an abort) hits
    Remove-Item $crashLog -ErrorAction SilentlyContinue
    $layoutBefore = (Get-Item $layoutFile).LastWriteTimeUtc.Ticks
    $acceptAlpha = $alpha.AcceptTcpClientAsync()
    $acceptBeta = $beta.AcceptTcpClientAsync()
    $env:PUTTYMODERN_TEST_REGISTRY_ROOT = $scratch
    $env:PUTTYMODERN_TEST_FATAL_AFTER = '25'
    $env:PUTTYMODERN_TEST_FATAL_KIND = 'assert'
    $app = Start-Process -FilePath $Exe -PassThru
    $env:PUTTYMODERN_TEST_REGISTRY_ROOT = $null
    $env:PUTTYMODERN_TEST_FATAL_AFTER = $null
    $env:PUTTYMODERN_TEST_FATAL_KIND = $null
    Check 'the two sessions come back from the saved workspace' ($acceptAlpha.Wait(40000) -and $acceptBeta.Wait(40000))
    $deadline = (Get-Date).AddSeconds(90)
    while ((Get-Date) -lt $deadline -and -not (Test-Path $crashLog)) { Start-Sleep -Milliseconds 500 }
    $logText = if (Test-Path $crashLog) { Get-Content $crashLog -Raw } else { '' }
    Check 'a failed assertion is reported to the host as well' ($logText -match 'Engine fatal error' -and $logText -match 'internal error')
    Start-Sleep -Seconds 3
    Check 'and the workspace was saved again' ((Get-Item $layoutFile).LastWriteTimeUtc.Ticks -gt $layoutBefore)
    # An abort goes on to Windows's own crash reporting, which may show a dialog and keep the process: the checks are done.
    $ended = $app.WaitForExit(15000)
    Write-Host "      the process $(if ($ended) { "ended with exit code $($app.ExitCode)" } else { 'is still there (Windows''s crash reporting); it is stopped' })"
}
finally {
    $env:PUTTYMODERN_TEST_REGISTRY_ROOT = $null
    $env:PUTTYMODERN_TEST_FATAL_AFTER = $null
    $env:PUTTYMODERN_TEST_FATAL_KIND = $null
    if ($app -and -not $app.HasExited) { $app.Kill() }
    Get-Process -Name WerFault -ErrorAction SilentlyContinue | Stop-Process -Force
    $alpha.Stop(); $beta.Stop()
    Remove-Item -Path $workspaceDir -Recurse -Force -ErrorAction SilentlyContinue
    Remove-Item -Path "HKCU:\Software\PuttyModernTests\$leaf" -Recurse -Force -ErrorAction SilentlyContinue
}
exit $failures
