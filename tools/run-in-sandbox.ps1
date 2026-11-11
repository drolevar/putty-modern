<#
.SYNOPSIS
  Runs the end-to-end checks of the real app inside Windows Sandbox, so that no window of the app ever opens
  on this desktop.

.DESCRIPTION
  The checks (tools\app-terminal-smoke.ps1, app-ssh-smoke.ps1, app-sessions-smoke.ps1, app-settings-smoke.ps1)
  start the app, drive its windows through UI Automation (the winapp CLI, which is on the guest's PATH) and read
  screenshots. Here they run on the Sandbox's own desktop. The Sandbox must be running and logged in (start it
  from the Start menu: winapp's host-side guest agent does not work on this machine, see
  docs\spike-keyboard-ime.md, so this uses plain `wsb exec`); the rest is automatic:

    tools\run-in-sandbox.ps1                                  # all four, on the NativeAOT publish
    tools\run-in-sandbox.ps1 -Smoke settings,ssh
    tools\run-in-sandbox.ps1 -AppDir artifacts\bin\PuTTY.App\release_win-x64   # the non-AOT build

  It stages the app folder and the scripts under artifacts\sandbox-share\work (shared into the guest as
  C:\spike\work). Tools come from the Sandbox itself: winget, Node and winapp are installed there, and
  tools\sandbox\guest-run.ps1 installs what else a check needs (the C++ runtime, the test server's npm
  package) the first time. Each check's output comes back through a file and is printed here; the exit code
  is the number of FAIL lines.
#>
param(
    [string[]] $Smoke = @('terminal', 'ssh', 'sessions', 'settings'),
    [string] $AppDir = 'artifacts\publish\PuTTY.App\release_win-x64',
    # Attach cdb to the app in the check (for the checks that call Attach-Debugger): a crash then leaves the stacks
    # of every thread in results\cdb.log and a full dump in results\crash.dmp. Needs tools\sandbox\install-debugger.ps1.
    [switch] $AttachDebugger
)

$ErrorActionPreference = 'Stop'
$Smoke = @($Smoke | ForEach-Object { $_ -split ',' })       # `powershell -File x.ps1 -Smoke a,b` arrives as the one string "a,b"
$root =Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$share = Join-Path $root 'artifacts\sandbox-share'
$work = Join-Path $share 'work'
$appSource = if ([IO.Path]::IsPathRooted($AppDir)) { $AppDir } else { Join-Path $root $AppDir }

if (-not (Test-Path (Join-Path $appSource 'PuTTY.App.exe'))) {
    Write-Host "No PuTTY.App.exe in $appSource. Publish first: dotnet publish src\PuTTY.App -c Release -r win-x64 (or, for the non-AOT build, dotnet build src\PuTTY.App -c Release -p:PublishAot=false)."
    exit 97
}

# --- the Sandbox
$ids = @(& wsb list 2>&1 | Where-Object { $_ -match '^[0-9a-f-]{36}$' })
if ($ids.Count -eq 0) {
    Write-Host 'Windows Sandbox is not running. Start it from the Start menu (and let it log in), then run this again.'
    exit 96
}
$id = $ids[0]

# --- stage
function Mirror($from, $to) {
    New-Item -ItemType Directory -Force -Path $to | Out-Null
    & robocopy $from $to /MIR /NFL /NDL /NJH /NJS /NP | Out-Null
    if ($LASTEXITCODE -ge 8) { throw "robocopy $from -> $to failed ($LASTEXITCODE)" }
    $global:LASTEXITCODE = 0
}
Write-Host 'Staging the app and the scripts ...'
Mirror $appSource (Join-Path $work 'app')
Mirror (Join-Path $root 'tools') (Join-Path $work 'tools')
if ($Smoke -contains 'agent') {
    # The key Pageant is given: made by a test (the Windows puttygen has no command line), kept under artifacts\agent-keys.
    $keys = Join-Path $root 'artifacts\agent-keys'
    if (-not (Test-Path (Join-Path $keys 'agent.ppk'))) {
        Write-Host 'Making the key for the agent check (dotnet test) ...'
        & dotnet test --project (Join-Path $root 'tests\PuTTY.Interop.Tests') -c Release --filter 'Name=Writes_the_key_the_agent_smoke_check_uses' | Out-Null
        if (-not (Test-Path (Join-Path $keys 'agent.ppk'))) { Write-Host 'The key was not made.'; exit 95 }
    }
    Mirror $keys (Join-Path $work 'agent-keys')
}
$results = Join-Path $work 'results'
New-Item -ItemType Directory -Force -Path $results | Out-Null

# --- make sure the guest sees the share
$probe = & wsb exec --id $id -r ExistingLogin -c 'cmd.exe /c if exist C:\spike\work\app\PuTTY.App.exe (exit 0) else (exit 1)' 2>&1
if ($probe -notmatch 'exited with code: 0') {
    & wsb share --id $id -f $share -s 'C:\spike' -w 2>&1 | Out-Null
    $probe = & wsb exec --id $id -r ExistingLogin -c 'cmd.exe /c if exist C:\spike\work\app\PuTTY.App.exe (exit 0) else (exit 1)' 2>&1
    if ($probe -notmatch 'exited with code: 0') {
        Write-Host "The Sandbox cannot see the staged files ($probe). Is it logged in, and is $share shared as C:\spike?"
        exit 94
    }
}

# --- run
$failures = 0
foreach ($name in $Smoke) {
    $out = Join-Path $results "$name.txt"
    Remove-Item $out -ErrorAction SilentlyContinue
    Write-Host "== $name (in the Sandbox)"
    # A small .cmd keeps the quoting out of wsb's command line, which does not survive nested quotes.
    $launcher = Join-Path $results "run-$name.cmd"
    $debuggerFlag = if ($AttachDebugger) { '1' } else { '0' }
    Set-Content -Path $launcher -Encoding ASCII -Value @(
        "set PUTTYMODERN_DEBUG_CDB=$debuggerFlag",
        "powershell -NoProfile -ExecutionPolicy Bypass -File C:\spike\work\tools\sandbox\guest-run.ps1 -Smoke $name -Exe C:\spike\work\app\PuTTY.App.exe > C:\spike\work\results\$name.txt 2>&1")
    $run = & wsb exec --id $id -r ExistingLogin -d 'C:\spike\work' -c "cmd.exe /c C:\spike\work\results\run-$name.cmd" 2>&1
    $code = if (($run | Out-String) -match 'exited with code: (\d+)') { [int]$Matches[1] } else { -1 }
    if (Test-Path $out) {
        $text = Get-Content $out
        $text | ForEach-Object { Write-Host $_ }
        $fails = @($text | Where-Object { $_ -match '^FAIL' }).Count
        $passes = @($text | Where-Object { $_ -match '^PASS' }).Count
        Write-Host "-- ${name}: $passes passed, $fails failed (exit code $code)"
        $failures += $fails
        if ($fails -eq 0 -and $code -ne 0) { $failures++ }      # a script that died without a FAIL line
    } else {
        Write-Host "-- ${name}: no output came back (exit code $code): $($run | Out-String)"
        $failures++
    }
}
exit $failures
