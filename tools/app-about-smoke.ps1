<#
.SYNOPSIS
  End-to-end check of the About dialog: it opens from the command bar's overflow menu and from "About PuTTY" at the foot of
  the settings window, says PuTTY Modern by Andrij Abyzov and credits Simon Tatham and the PuTTY contributors by name, links to
  the official PuTTY, and shows the full licence texts (PuTTY's word for word, the other components', the .NET runtime's notices)
  under Licences, read from the resources inside the exe. The window titles keep saying "PuTTY".

.DESCRIPTION
  Run it in Windows Sandbox with tools\run-in-sandbox.ps1 -Smoke about (or on a desktop with tools\run-checks.ps1 -Smoke about).
  The app's registry is pointed at a scratch key by the test hook PUTTYMODERN_TEST_REGISTRY_ROOT, so the user's own PuTTY
  settings are neither read nor written. Pictures go to artifacts\spike\app-about-*.png.

  Prints PASS or FAIL per check; the exit code is the number of failures.
#>
param(
    [Parameter(Mandatory)] [string] $Exe
)

$ErrorActionPreference = 'Stop'
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
function Wait-Gone($selector, $seconds = 15) {
    $deadline = (Get-Date).AddSeconds($seconds)
    while ((Get-Date) -lt $deadline) {
        if (-not ((Try-Ui @('search', $selector)) -match 'Found [1-9]')) { return $true }
        Start-Sleep -Milliseconds 400
    }
    return $false
}
. (Join-Path $PSScriptRoot 'smoke-common.ps1')       # Bring-AppToFront
$pictures = Join-Path $root 'artifacts\spike'
New-Item -ItemType Directory -Force -Path $pictures | Out-Null

# What the dialog's labels say, found by name; the long ones are searched by a distinctive stretch of their text.
function Check-AboutDialog($where) {
    Check "$where`: the dialog opens" (Wait-Element 'AboutPuttyLink' 20)
    Check "$where`: it names the product" ((Try-Ui @('search', 'PuTTY Modern')) -match 'Found [1-9]')
    Check "$where`: it names the author" ((Try-Ui @('search', 'Andrij Abyzov')) -match 'Found [1-9]')
    Check "$where`: it credits Simon Tatham and the PuTTY contributors" ((Try-Ui @('search', 'Simon Tatham and the PuTTY contributors')) -match 'Found [1-9]')
    Check "$where`: it says PuTTY Modern is not made or endorsed by them" ((Try-Ui @('search', 'not made or endorsed by them')) -match 'Found [1-9]')
    Check "$where`: it says what of PuTTY it includes" ((Try-Ui @('search', "includes PuTTY's core")) -match 'Found [1-9]')
    Check "$where`: the dialog's own title is About PuTTY (a window title stays PuTTY)" ((Try-Ui @('search', 'About PuTTY')) -match 'Found [1-9]')
}

$scratch = "Software\PuttyModernTests\e2e-about-$([guid]::NewGuid().ToString('N'))"
$app = $null
try {
    $env:PUTTYMODERN_TEST_REGISTRY_ROOT = $scratch
    $app = Start-Process -FilePath $Exe -PassThru
    $env:PUTTYMODERN_TEST_REGISTRY_ROOT = $null

    Check 'the command bar is there' (Wait-Element 'CommandNewSession' 40)
    Bring-AppToFront
    $app.Refresh()
    Check 'the main window is still titled PuTTY' ($app.MainWindowTitle -like '*PuTTY*' -and $app.MainWindowTitle -notlike '*Modern*') "(title: '$($app.MainWindowTitle)')"

    # --- from the command bar's overflow menu
    $opened = $false
    foreach ($more in 'MoreButton', 'See more') {
        [void](Try-Ui @('invoke', $more))
        if (Wait-Element 'CommandAbout' 5) { $opened = $true; break }
    }
    Check 'the overflow menu offers About PuTTY' $opened
    [void](Try-Ui @('invoke', 'CommandAbout'))
    Check-AboutDialog 'command bar'
    & winapp ui screenshot -a PuTTY.App --output (Join-Path $pictures 'app-about.png') 2>&1 | Out-Null

    Check 'the link to the official PuTTY is there' ((Try-Ui @('search', 'The official PuTTY')) -match 'Found [1-9]')

    # --- the licences
    [void](Try-Ui @('click', 'AboutLicences'))
    Check 'opening Licences shows the text' (Wait-Element 'AboutLicenceText' 15)
    $text = ''
    for ($i = 0; $i -lt 20 -and -not $text; $i++) {
        $text = Try-Ui @('get-value', 'AboutLicenceText')
        if (-not $text) { Start-Sleep -Milliseconds 500 }
    }
    # The text box's value comes back without its line breaks (the words at a break run together), so compare with every space and
    # line break taken out of both sides.
    $flat = $text -replace '\s', ''
    Set-Content -Path (Join-Path $pictures 'app-about-licence-text.txt') -Value $text -Encoding UTF8     # to read when a check below fails
    Check 'the text has PuTTY Modern''s own licence first' ($flat -match 'PuTTYModern=+MITLicenseCopyright\(c\)2026AndrijAbyzov')
    Check 'it has PuTTY''s licence word for word, with the contributors' ($flat -match 'PuTTYiscopyright1997-2026SimonTatham\.PortionscopyrightRobertdeBath,JorisvanRantwijk,DelianDelchev')
    Check 'it has the docking library''s licence' ($flat -match 'Copyright\(c\)2026Digi21')
    Check 'it has the .NET runtime''s licence and its third-party notices' (($flat -match 'Copyright\(c\)\.NETFoundationandContributors') -and ($flat -match 'third-partynotices'))
    Check 'it says the Microsoft components are under Microsoft''s own terms' ($flat -match 'MicrosoftSoftwareLicenseTerms,MicrosoftWindowsAppSDK')
    Check 'it ends with the .NET runtime''s own notices' ($flat -match 'Licensenoticefor')
    & winapp ui screenshot -a PuTTY.App --output (Join-Path $pictures 'app-about-licences.png') 2>&1 | Out-Null

    [void](Try-Ui @('invoke', 'CloseButton'))
    Check 'Close closes the dialog' (Wait-Gone 'AboutPuttyLink')

    # --- from the settings window
    [void](Try-Ui @('invoke', 'CommandNewSession'))
    Check 'the settings window opens' (Wait-Element 'SettingsAbout' 30)
    [void](Try-Ui @('invoke', 'SettingsAbout'))
    Check-AboutDialog 'settings window'
    [void](Try-Ui @('invoke', 'CloseButton'))
    Check 'Close closes it again' (Wait-Gone 'AboutPuttyLink')
    [void](Try-Ui @('invoke', 'Cancel', '--type', 'Button'))

    # --- the app ends cleanly
    [void]$app.CloseMainWindow()
    Check 'the app exits' ($app.WaitForExit(20000))
    Check 'with exit code 0' ($app.HasExited -and $app.ExitCode -eq 0) "(exit code $($app.ExitCode))"
}
finally {
    if ($app -and -not $app.HasExited) { $app.Kill() }
    Remove-Item -Recurse -Force "HKCU:\$scratch" -ErrorAction SilentlyContinue
}

Write-Host ''
Write-Host "$failures failure(s)"
exit $failures
