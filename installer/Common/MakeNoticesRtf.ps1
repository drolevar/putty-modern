<#
  Makes the licence text the installers show (the MSI's licence page and the setup program's licence box) as RTF, from the same
  files the About dialog shows: PuTTY Modern's own LICENSE, then THIRD-PARTY-NOTICES.txt (PuTTY's licence word for word and the
  other components'). The order is the one AboutText.LicencesText uses (the About dialog adds the .NET runtime's own third-party
  notices after these, which are too long for a licence page).

  The text files are hard-wrapped, so lines are joined into paragraphs: a blank line, a heading (a line with a ==== or ---- line
  under it, or one starting with #), a list item (a line starting with - or * or a number) or a smaller indent starts a new one.
  Everything outside ASCII is written as an RTF unicode escape, so this file and its output are plain ASCII.

  Windows PowerShell 5.1 runs this (the build calls powershell.exe): no PowerShell 7 syntax.
#>
param(
    [Parameter(Mandatory = $true)] [string] $OutputFilePath,
    [string] $LicenseFile,
    [string] $NoticesFile
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
if (-not $LicenseFile) { $LicenseFile = Join-Path $repoRoot 'LICENSE' }
if (-not $NoticesFile) { $NoticesFile = Join-Path $repoRoot 'THIRD-PARTY-NOTICES.txt' }

function ConvertTo-RtfText([string] $text) {
    $sb = New-Object System.Text.StringBuilder
    foreach ($ch in $text.ToCharArray()) {
        $code = [int] $ch
        if ($ch -eq '\') { [void] $sb.Append('\\') }
        elseif ($ch -eq '{') { [void] $sb.Append('\{') }
        elseif ($ch -eq '}') { [void] $sb.Append('\}') }
        elseif ($ch -eq "`t") { [void] $sb.Append('\tab ') }
        elseif ($code -gt 126) {
            if ($code -gt 32767) { $code -= 65536 }
            [void] $sb.Append('\u' + $code + '?')
        }
        elseif ($code -ge 32) { [void] $sb.Append($ch) }
    }
    $sb.ToString()
}

# Turns the text into blocks: @{ Kind = 'h1'|'h2'|'p'; Text; Indent }.
function ConvertTo-Blocks([string] $text) {
    $blocks = New-Object System.Collections.ArrayList
    $lines = $text.Replace("`r`n", "`n").Replace("`r", "`n").Split("`n")
    $current = $null
    $currentIndent = 0
    for ($i = 0; $i -lt $lines.Length; $i++) {
        $line = $lines[$i]
        $trimmed = $line.Trim()
        $next = if ($i + 1 -lt $lines.Length) { $lines[$i + 1].Trim() } else { '' }
        if ($trimmed.Length -eq 0) {
            if ($current) { [void] $blocks.Add($current); $current = $null }
            continue
        }
        if ($trimmed -match '^(=|-){3,}$') {
            # an underline that no heading used: a separator
            if ($current) { [void] $blocks.Add($current); $current = $null }
            continue
        }
        if ($next -match '^(=|-){3,}$') {
            if ($current) { [void] $blocks.Add($current); $current = $null }
            $kind = if ($next[0] -eq '=') { 'h1' } else { 'h2' }
            [void] $blocks.Add(@{ Kind = $kind; Text = $trimmed; Indent = 0 })
            $i++
            continue
        }
        if ($trimmed -match '^#{1,6}\s+(.*)$') {
            if ($current) { [void] $blocks.Add($current); $current = $null }
            [void] $blocks.Add(@{ Kind = 'h2'; Text = $Matches[1]; Indent = 0 })
            continue
        }
        $indent = $line.Length - $line.TrimStart().Length
        $isItem = $trimmed -match '^([-*]|\d+[.)])\s'
        if ($current -and -not $isItem -and $indent -ge $currentIndent) {
            $current.Text = $current.Text + ' ' + $trimmed
        }
        else {
            if ($current) { [void] $blocks.Add($current) }
            $current = @{ Kind = 'p'; Text = $trimmed; Indent = $indent }
            $currentIndent = $indent
        }
    }
    if ($current) { [void] $blocks.Add($current) }
    , $blocks
}

function Add-Blocks([System.Text.StringBuilder] $sb, $blocks) {
    foreach ($b in $blocks) {
        $text = ConvertTo-RtfText $b.Text
        switch ($b.Kind) {
            'h1' { [void] $sb.Append('\pard\sb240\sa120\b\fs26 ' + $text + '\b0\fs20\par' + "`r`n") }
            'h2' { [void] $sb.Append('\pard\sb200\sa100\b\fs22 ' + $text + '\b0\fs20\par' + "`r`n") }
            default {
                $left = [Math]::Min($b.Indent, 8) * 90
                [void] $sb.Append('\pard\sa120\li' + $left + ' ' + $text + '\par' + "`r`n")
            }
        }
    }
}

$license = [System.IO.File]::ReadAllText($LicenseFile, [System.Text.Encoding]::UTF8)
$notices = [System.IO.File]::ReadAllText($NoticesFile, [System.Text.Encoding]::UTF8)

$sb = New-Object System.Text.StringBuilder
[void] $sb.Append('{\rtf1\ansi\ansicpg1252\deff0{\fonttbl{\f0\fswiss\fcharset0 Segoe UI;}}' + "`r`n")
[void] $sb.Append('\viewkind4\uc1\f0\fs20' + "`r`n")
Add-Blocks $sb (ConvertTo-Blocks "PuTTY Modern`n============`n`n$license")
Add-Blocks $sb (ConvertTo-Blocks $notices)
[void] $sb.Append('}')

$dir = Split-Path -Parent $OutputFilePath
if ($dir -and -not (Test-Path -LiteralPath $dir)) { New-Item -ItemType Directory -Path $dir | Out-Null }
[System.IO.File]::WriteAllText($OutputFilePath, $sb.ToString(), [System.Text.Encoding]::ASCII)
Write-Host ('Wrote ' + $OutputFilePath + ' (' + (Get-Item -LiteralPath $OutputFilePath).Length + ' bytes)')
