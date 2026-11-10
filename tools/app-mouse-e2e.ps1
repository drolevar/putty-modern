<#
.SYNOPSIS
  End-to-end check of the terminal's mouse, clipboard and wheel in the real app, with REAL input:
  it moves the cursor and clicks. Run it only where that is harmless: Windows Sandbox, a VM, a spare
  machine. It refuses to run elsewhere unless -IKnowThisDrivesTheMouse is given.

.DESCRIPTION
  A loopback server sends a banner; the script finds the terminal on screen, then
    - drags over the banner with the left button: the selection must reach the system clipboard,
    - double-clicks a word: that word must be the clipboard,
    - puts text on the clipboard and right-clicks: it must reach the server as a paste,
    - floods the scrollback, then turns the wheel: the banner must scroll back into view, and out again.
  Results go to -Report (and the console); the exit code is the number of failures.

  In Windows Sandbox (share the app folder and this script, for example as C:\spike):
    wsb exec --id <id> -r ExistingLogin -d C:\spike -c "powershell -NoProfile -ExecutionPolicy Bypass -File C:\spike\app-mouse-e2e.ps1 -Exe C:\spike\app-term\PuTTY.App.exe -IKnowThisDrivesTheMouse"
  The app must be a build whose C++ runtime is present on the machine (copy vcruntime140*.dll and
  msvcp140.dll next to the exe for a fresh Sandbox).
#>
param(
    [Parameter(Mandatory)] [string] $Exe,
    [string] $Report = '',
    [switch] $IKnowThisDrivesTheMouse
)

$ErrorActionPreference = 'Stop'
if (-not $IKnowThisDrivesTheMouse -and $env:USERNAME -ne 'WDAGUtilityAccount') {
    Write-Host 'This script moves the real mouse. Run it in Windows Sandbox, or pass -IKnowThisDrivesTheMouse.'
    exit 99
}

if (-not $Report) { $Report = Join-Path (Split-Path -Parent $MyInvocation.MyCommand.Path) 'results\mouse-e2e.txt' }
New-Item -ItemType Directory -Force -Path (Split-Path $Report) | Out-Null
$lines = New-Object System.Collections.Generic.List[string]
$script:failures = 0
function Log($text) { $lines.Add($text); Write-Host $text; Set-Content -Path $Report -Value $lines }
function Check($name, $ok, $detail = '') {
    if ($ok) { Log "PASS  $name $detail" } else { Log "FAIL  $name $detail"; $script:failures++ }
}

Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName System.Windows.Forms
Add-Type -ReferencedAssemblies System.Drawing -TypeDefinition @'
using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

public static class MouseNative
{
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
    [DllImport("user32.dll")] public static extern bool MoveWindow(IntPtr hwnd, int x, int y, int w, int h, bool repaint);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hwnd, int cmd);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] public static extern void mouse_event(uint flags, uint dx, uint dy, int data, UIntPtr extra);

    public const uint MOVE = 0x0001, ABSOLUTE = 0x8000, LEFTDOWN = 0x0002, LEFTUP = 0x0004, RIGHTDOWN = 0x0008, RIGHTUP = 0x0010, WHEEL = 0x0800;

    public static Bitmap Capture(int x, int y, int w, int h)
    {
        var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp)) { g.CopyFromScreen(x, y, 0, 0, new Size(w, h)); }
        return bmp;
    }

    public static int Count(Bitmap bmp, int r, int g, int b)
    {
        int count = 0;
        for (int y = 0; y < bmp.Height; y++)
            for (int x = 0; x < bmp.Width; x++)
            {
                Color c = bmp.GetPixel(x, y);
                if (c.R == r && c.G == g && c.B == b) count++;
            }
        return count;
    }

    // Bounding box of the terminal: the rows and columns that are mostly pure black. A few stray black
    // pixels elsewhere (text in a title bar, an icon) must not move the box, so a line counts only
    // when at least 100 of its pixels are black.
    public static int[] BlackBox(Bitmap bmp)
    {
        int[] rows = new int[bmp.Height];
        int[] cols = new int[bmp.Width];
        for (int y = 0; y < bmp.Height; y++)
            for (int x = 0; x < bmp.Width; x++)
            {
                Color c = bmp.GetPixel(x, y);
                if (c.R == 0 && c.G == 0 && c.B == 0) { rows[y]++; cols[x]++; }
            }
        int minX = -1, maxX = -1, minY = -1, maxY = -1;
        for (int x = 0; x < cols.Length; x++) if (cols[x] >= 100) { if (minX < 0) minX = x; maxX = x; }
        for (int y = 0; y < rows.Length; y++) if (rows[y] >= 100) { if (minY < 0) minY = y; maxY = y; }
        return new[] { minX, minY, maxX, maxY };
    }
}
'@

[void][MouseNative]::SetProcessDPIAware()
$esc = [char]27

function Send-Text($stream, $text) {
    $bytes = [Text.Encoding]::UTF8.GetBytes($text)
    $stream.Write($bytes, 0, $bytes.Length)
}

# A real input event, not SetCursorPos: teleporting the cursor does not reach a XAML control as pointer moves,
# so a drag made of SetCursorPos steps selected nothing.
function Move-To($x, $y) {
    $screen = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
    $dx = [uint32][Math]::Ceiling($x * 65535.0 / ($screen.Width - 1))
    $dy = [uint32][Math]::Ceiling($y * 65535.0 / ($screen.Height - 1))
    [MouseNative]::mouse_event([MouseNative]::MOVE -bor [MouseNative]::ABSOLUTE, $dx, $dy, 0, [UIntPtr]::Zero)
    Start-Sleep -Milliseconds 40
}
function Press($down, $up) { [MouseNative]::mouse_event($down, 0, 0, 0, [UIntPtr]::Zero); Start-Sleep -Milliseconds 40; [MouseNative]::mouse_event($up, 0, 0, 0, [UIntPtr]::Zero) }

function Read-Server($stream, $seconds) {
    $received = New-Object System.Collections.Generic.List[byte]
    $buffer = New-Object byte[] 4096
    $deadline = (Get-Date).AddSeconds($seconds)
    while ((Get-Date) -lt $deadline) {
        if ($stream.DataAvailable) {
            $n = $stream.Read($buffer, 0, $buffer.Length)
            for ($i = 0; $i -lt $n; $i++) { $received.Add($buffer[$i]) }
        } else { Start-Sleep -Milliseconds 100 }
    }
    return [Text.Encoding]::GetEncoding(28591).GetString($received.ToArray())
}

$listener = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Loopback, 0)
$listener.Start()
$port = $listener.LocalEndpoint.Port
$app = Start-Process -FilePath $Exe -ArgumentList @('--connect', "raw://127.0.0.1:$port") -PassThru
try {
    $accept = $listener.AcceptTcpClientAsync()
    Check 'the app connects to the server' ($accept.Wait(30000))
    if (-not $accept.IsCompleted) { return }
    $stream = $accept.Result.GetStream()

    # A window the whole of which is on the screen, in front.
    $deadline = (Get-Date).AddSeconds(20)
    while ($app.MainWindowHandle -eq [IntPtr]::Zero -and (Get-Date) -lt $deadline) { Start-Sleep -Milliseconds 200; $app.Refresh() }
    $hwnd = $app.MainWindowHandle
    $screen = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
    $width = [Math]::Min(1440, $screen.Width - 20)
    $height = [Math]::Min(940, $screen.Height - 60)
    [void][MouseNative]::ShowWindow($hwnd, 9)
    [void][MouseNative]::MoveWindow($hwnd, 0, 0, $width, $height, $true)
    [void][MouseNative]::SetForegroundWindow($hwnd)
    Start-Sleep -Seconds 2

    Send-Text $stream "$esc[32mHELLO FROM THE LOOPBACK SERVER$esc[0m`r`nsecond line`r`n"
    Start-Sleep -Seconds 2

    $shot = [MouseNative]::Capture(0, 0, $screen.Width, $screen.Height)
    $shot.Save((Join-Path (Split-Path $Report) 'mouse-e2e-screen.png'))
    $box = [MouseNative]::BlackBox($shot)
    $shot.Dispose()
    Check 'the terminal is on screen' ($box[2] -gt $box[0] + 200 -and $box[3] -gt $box[1] + 100) "(black box $($box -join ','), screen $($screen.Width)x$($screen.Height))"
    if ($box[2] -le $box[0]) { return }
    $left = $box[0]; $top = $box[1]; $right = $box[2]
    $rowY = $top + 4                       # inside the first row at any scale

    # The window may not be the foreground one (Windows can refuse SetForegroundWindow to a script), and
    # then the first click only activates it. One click on empty terminal space gets that out of the way.
    Move-To ($left + 100) ($top + 300)
    Press ([MouseNative]::LEFTDOWN) ([MouseNative]::LEFTUP)
    Start-Sleep -Milliseconds 800
    # --- drag-select the banner: the selection must be on the clipboard when the button is released.
    Set-Clipboard -Value 'BEFORE'
    Move-To ($left + 1) $rowY
    [MouseNative]::mouse_event([MouseNative]::LEFTDOWN, 0, 0, 0, [UIntPtr]::Zero); Start-Sleep -Milliseconds 60
    for ($x = $left + 20; $x -le [Math]::Min($left + 420, $right - 5); $x += 20) { Move-To $x $rowY }
    [MouseNative]::mouse_event([MouseNative]::LEFTUP, 0, 0, 0, [UIntPtr]::Zero)
    Start-Sleep -Seconds 1
    $clip = Get-Clipboard -Raw
    $sel = [MouseNative]::Capture(0, 0, $screen.Width, $screen.Height); $sel.Save((Join-Path (Split-Path $Report) 'mouse-e2e-selected.png')); $sel.Dispose()
    Check 'dragging over the banner copies it to the clipboard' ($clip -like '*HELLO FROM THE LOOPBACK SERVER*') "(clipboard: [$($clip)])"

    # --- double-click the first word.
    Start-Sleep -Milliseconds 700
    Move-To ($left + 3) $rowY
    Press ([MouseNative]::LEFTDOWN) ([MouseNative]::LEFTUP)
    Start-Sleep -Milliseconds 60
    Press ([MouseNative]::LEFTDOWN) ([MouseNative]::LEFTUP)
    Start-Sleep -Seconds 1
    $clip = (Get-Clipboard -Raw)
    Check 'a double-click copies the word under the pointer' ($clip -eq 'HELLO') "(clipboard: [$($clip)])"

    # --- right button pastes: the line discipline holds the text until Enter, so the text ends in CR.
    Set-Clipboard -Value "PASTED-OK`r"
    Move-To ($left + 100) ($top + 200)
    Press ([MouseNative]::RIGHTDOWN) ([MouseNative]::RIGHTUP)
    $heard = Read-Server $stream 5
    Check 'the right button pastes the clipboard into the session' ($heard -like '*PASTED-OK*') "(server heard: [$($heard.Replace("`r", '<CR>').Replace("`n", '<LF>'))])"

    # --- the wheel: flood so the banner scrolls out of view, then wheel up and down.
    $flood = New-Object System.Text.StringBuilder
    1..150 | ForEach-Object { [void]$flood.Append("scrollback line $_`r`n") }
    Send-Text $stream $flood.ToString()
    Start-Sleep -Seconds 3
    Move-To ($left + 100) ($top + 100)
    $w = $right - $left + 1
    $h = $box[3] - $top + 1
    $bmp = [MouseNative]::Capture($left, $top, $w, $h); $greenBottom = [MouseNative]::Count($bmp, 0, 187, 0); $bmp.Dispose()
    Check 'the banner has scrolled out of view' ($greenBottom -lt 5) "($greenBottom green pixels)"

    for ($i = 0; $i -lt 25; $i++) { [MouseNative]::mouse_event([MouseNative]::WHEEL, 0, 0, 120, [UIntPtr]::Zero); Start-Sleep -Milliseconds 40 }
    Start-Sleep -Seconds 1
    $bmp = [MouseNative]::Capture($left, $top, $w, $h); $greenTop = [MouseNative]::Count($bmp, 0, 187, 0); $bmp.Dispose()
    Check 'the wheel scrolls back to the banner' ($greenTop -gt 30) "($greenTop green pixels)"

    for ($i = 0; $i -lt 25; $i++) { [MouseNative]::mouse_event([MouseNative]::WHEEL, 0, 0, -120, [UIntPtr]::Zero); Start-Sleep -Milliseconds 40 }
    Start-Sleep -Seconds 1
    $bmp = [MouseNative]::Capture($left, $top, $w, $h); $greenBack = [MouseNative]::Count($bmp, 0, 187, 0); $bmp.Dispose()
    Check 'the wheel scrolls forward again, out of the banner' ($greenBack -lt 5) "($greenBack green pixels)"
}
finally {
    $closedCleanly = $false
    if (-not $app.HasExited) { [void]$app.CloseMainWindow(); [void]$app.WaitForExit(10000) }
    if ($app.HasExited) { $closedCleanly = ($app.ExitCode -eq 0) } else { $app.Kill() }
    Check 'the app exits cleanly when its window is closed' $closedCleanly "(exit code $(if ($app.HasExited) { $app.ExitCode } else { 'still running' }))"
    $listener.Stop()
    Log "failures: $script:failures"
}
exit $script:failures
