<#
.SYNOPSIS
  Inside Windows Sandbox, as System: makes cdb (WinDbg's console debugger, installed with
  `winget install Microsoft.WinDbg`) usable by a normal user as C:\debugger\cdb.exe, and optionally turns full
  page heap on for the app. tools\run-in-sandbox.ps1 -AttachDebugger then attaches cdb to the app in a check.

.DESCRIPTION
  WinDbg is a packaged app: its folder under C:\Program Files\WindowsApps cannot be run by a normal user, so the
  debugger's own files (the package's amd64 folder) are copied to C:\debugger.

  -PageHeap  full page heap for PuTTY.App.exe (what `gflags /p /enable PuTTY.App.exe /full` sets): every heap block
             gets pages of its own with a guard page after it, so a write past the end or into a freed block faults
             at once, in the code that did it, instead of at some later free. Memory-hungry and slow: only while
             hunting a heap corruption.
  -Remove    turns the page heap off again (the copy in C:\debugger stays).

  From the host:
    tools\sandbox-exec.ps1 'powershell -NoProfile -ExecutionPolicy Bypass -File C:\spike\work\tools\sandbox\install-debugger.ps1 -PageHeap' -RunAs System
#>
param([switch] $PageHeap, [switch] $Remove)

$ErrorActionPreference = 'Stop'
$ifeo = 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options\PuTTY.App.exe'

if ($Remove) {
    Remove-Item -Path $ifeo -ErrorAction SilentlyContinue
    Write-Host 'The page heap setting for PuTTY.App.exe is removed.'
    exit 0
}

$package = Get-AppxPackage -AllUsers Microsoft.WinDbg | Select-Object -First 1
if (-not $package) { Write-Host 'WinDbg is not installed: winget install --id Microsoft.WinDbg --exact --silent'; exit 90 }
if (-not (Test-Path 'C:\debugger\cdb.exe')) {
    Copy-Item -Path (Join-Path $package.InstallLocation 'amd64') -Destination 'C:\debugger' -Recurse -Force
}
Write-Host 'cdb is in C:\debugger.'

if ($PageHeap) {
    New-Item -Path $ifeo -Force | Out-Null
    Set-ItemProperty -Path $ifeo -Name GlobalFlag -Value 0x02000000 -Type DWord
    Set-ItemProperty -Path $ifeo -Name PageHeapFlags -Value 0x3 -Type DWord
    Write-Host 'Full page heap is on for PuTTY.App.exe.'
}
