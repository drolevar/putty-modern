<#
.SYNOPSIS
  Finds the Visual C++ runtime installer (vc_redist.x64.exe) that comes with Visual Studio's C++ tools and writes an include
  file for the bundle: where it is, which version it is, and which Visual C++ year that is.

.DESCRIPTION
  The engine DLL (puttyengine.dll) links the C++ runtime dynamically (/MD, always: PuTTY's own libraries are built that way),
  so a machine without the runtime cannot start the app, and the bundle installs it first. The runtime that goes with the
  toolset the engine was built with is the one in the Visual Studio that built it: vswhere finds that Visual Studio, and
  VC\Auxiliary\Build\Microsoft.VCRedistVersion.default.txt names the folder under VC\Redist\MSVC.

  The version is read from the file itself, and written as the version the bundle requires: a machine whose msvcp140.dll is
  older than that gets the runtime installed (see VcRedist.wxs).
#>
param(
    [Parameter(Mandatory)] [string] $OutputFilePath
)

$ErrorActionPreference = 'Stop'

$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
if (-not (Test-Path $vswhere)) { throw "vswhere.exe is not at ${vswhere}: Visual Studio with the C++ tools is needed to find the Visual C++ runtime installer." }

$vs = & $vswhere -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if (-not $vs) { throw 'No Visual Studio with the C++ x64 tools was found (vswhere).' }

$versionFile = Join-Path $vs 'VC\Auxiliary\Build\Microsoft.VCRedistVersion.default.txt'
$redistFolderName = (Get-Content -Path $versionFile -TotalCount 1).Trim()
$redist = Join-Path $vs "VC\Redist\MSVC\$redistFolderName\vc_redist.x64.exe"
if (-not (Test-Path $redist)) { throw "$redist does not exist." }

$version = [System.Diagnostics.FileVersionInfo]::GetVersionInfo($redist)
$versionText = '{0}.{1}.{2}.{3}' -f $version.FileMajorPart, $version.FileMinorPart, $version.FileBuildPart, $version.FilePrivatePart

# The Visual C++ year from the toolset's minor version (14.51 is the 2026 toolset).
$year = switch ($version.FileMinorPart) {
    { $_ -lt 10 } { '2015' }
    { $_ -lt 20 } { '2017' }
    { $_ -lt 30 } { '2019' }
    { $_ -lt 50 } { '2022' }
    { $_ -lt 60 } { '2026' }
    default { 'vNext' }
}

Write-Host "Visual C++ $year runtime ${versionText}: ${redist}"

$directory = Split-Path -Parent $OutputFilePath
if ($directory -and -not (Test-Path $directory)) { New-Item -ItemType Directory -Force -Path $directory | Out-Null }

@"
<Include xmlns="http://wixtoolset.org/schemas/v4/wxs">
  <!-- Written by scripts\FindVcRedist.ps1 at build time; not kept in source control. -->
  <?define VCRedistExe="$redist" ?>
  <?define VCRedistRequired="$versionText" ?>
  <?define VCRedistYear="$year" ?>
</Include>
"@ | Out-File -FilePath $OutputFilePath -Encoding UTF8
