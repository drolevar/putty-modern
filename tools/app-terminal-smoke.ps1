<#
.SYNOPSIS
  End-to-end check of the terminal in the real app: a loopback server sends text, the app is
  started against it with --connect, a screenshot must show the text in PuTTY's colours, and
  typed keys must reach the server.

.DESCRIPTION
  Safe on a developer desktop: no key is injected into the desktop. Typing is posted as thread
  messages to the app's own UI thread (the same messages Windows would send), which is what the
  engine's keyboard hook reads. Needs the app built (see CLAUDE.md), the winapp CLI on PATH, and
  prints PASS or FAIL per check; the exit code is the number of failures.

  tools\app-terminal-smoke.ps1 -Exe artifacts\publish\PuTTY.App\release_win-x64\PuTTY.App.exe
#>
param(
    [Parameter(Mandatory)] [string] $Exe,
    [string] $Screenshot = ''
)

$ErrorActionPreference = 'Stop'
if (-not $Screenshot) { $Screenshot = Join-Path (Split-Path -Parent $MyInvocation.MyCommand.Path) '..\artifacts\spike\app-terminal.png' }
$esc = [char]27
$failures = 0
function Check($name, $ok, $detail = '') {
    if ($ok) { Write-Host "PASS  $name $detail" } else { Write-Host "FAIL  $name $detail"; $script:failures++ }
}

Add-Type -TypeDefinition @'
using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

public static class SmokeNative
{
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);
    [DllImport("user32.dll")] public static extern bool MoveWindow(IntPtr hwnd, int x, int y, int width, int height, bool repaint);
    [DllImport("user32.dll", SetLastError = true)] public static extern bool PostThreadMessageW(uint threadId, uint msg, UIntPtr wParam, IntPtr lParam);

    // Counts pixels of exactly the given colour (the interior of thick glyph strokes is solid).
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
                    {
                        if (row[x * 4 + 2] == r && row[x * 4 + 1] == g && row[x * 4] == b) count++;
                    }
                }
                return count;
            }
            finally { bmp.UnlockBits(data); }
        }
    }
}
'@ -ReferencedAssemblies System.Drawing

New-Item -ItemType Directory -Force -Path (Split-Path $Screenshot) | Out-Null
Remove-Item $Screenshot -ErrorAction SilentlyContinue

$listener = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Loopback, 0)
$listener.Start()
$port = $listener.LocalEndpoint.Port
$app = Start-Process -FilePath $Exe -ArgumentList @('--connect', "raw://127.0.0.1:$port") -PassThru
try {
    $accept = $listener.AcceptTcpClientAsync()
    Check 'the app connects to the server' ($accept.Wait(20000))
    if (-not $accept.IsCompleted) { return }
    $client = $accept.Result
    $stream = $client.GetStream()

    # PuTTY's default colours: green is 0,187,0 and red 187,0,0. Two lines, so the text is not on the first row only.
    $banner = "$esc[32mHELLO FROM THE LOOPBACK SERVER$esc[0m`r`n$esc[31mWARNING$esc[0m`r`nlogin: "
    $bytes = [Text.Encoding]::UTF8.GetBytes($banner)
    $stream.Write($bytes, 0, $bytes.Length)
    Start-Sleep -Seconds 3

    & winapp ui screenshot -a PuTTY.App --output $Screenshot 2>&1 | Out-Null
    Check 'a screenshot was taken' (Test-Path $Screenshot)
    if (Test-Path $Screenshot) {
        $green = [SmokeNative]::CountColour($Screenshot, 0, 187, 0)
        $red = [SmokeNative]::CountColour($Screenshot, 187, 0, 0)
        $grey = [SmokeNative]::CountColour($Screenshot, 187, 187, 187)
        Check 'green text is on screen' ($green -gt 30) "($green pixels of 0,187,0)"
        Check 'red text is on screen' ($red -gt 20) "($red pixels of 187,0,0)"
        Check 'default-colour text (the prompt) is on screen' ($grey -gt 20) "($grey pixels of 187,187,187)"
    }

    # Typing: thread messages to the app's UI thread, as Windows would post them. A Raw session edits
    # the line locally, so the server hears "xy" and the carriage return together, on Enter.
    $app.Refresh()
    $pid2 = 0
    $uiThread = [SmokeNative]::GetWindowThreadProcessId($app.MainWindowHandle, [ref]$pid2)
    Check 'the UI thread was found' ($uiThread -ne 0)
    foreach ($ch in 'x', 'y') {
        [void][SmokeNative]::PostThreadMessageW($uiThread, 0x0102, [UIntPtr]::new([uint64][int][char]$ch), [IntPtr]::new(1))   # WM_CHAR
    }
    $lparamDown = [IntPtr]::new(1 -bor (0x1C -shl 16))
    $lparamUp = [IntPtr]::new([int64]([uint32](1 -bor (0x1C -shl 16) -bor (1 -shl 30)) -bor 0x80000000))
    [void][SmokeNative]::PostThreadMessageW($uiThread, 0x0100, [UIntPtr]::new(13), $lparamDown)               # WM_KEYDOWN VK_RETURN
    [void][SmokeNative]::PostThreadMessageW($uiThread, 0x0101, [UIntPtr]::new(13), $lparamUp)                 # WM_KEYUP

    $received = New-Object System.Collections.Generic.List[byte]
    $buffer = New-Object byte[] 256
    $deadline = (Get-Date).AddSeconds(10)
    while ((Get-Date) -lt $deadline -and $received.Count -lt 3) {
        if ($stream.DataAvailable) {
            $n = $stream.Read($buffer, 0, $buffer.Length)
            for ($i = 0; $i -lt $n; $i++) { $received.Add($buffer[$i]) }
        } else { Start-Sleep -Milliseconds 100 }
    }
    $text = [Text.Encoding]::GetEncoding(28591).GetString($received.ToArray()).Replace("`r", '<CR>').Replace("`n", '<LF>')
    Check 'typed text reaches the server' ($text.StartsWith('xy<CR>')) "(server received: $text)"

    # Growing the window must grow the terminal: more of the view is terminal black, and the text is redrawn.
    $before = [SmokeNative]::CountColour($Screenshot, 0, 0, 0)
    [void][SmokeNative]::MoveWindow($app.MainWindowHandle, 40, 40, 1700, 1000, $true)
    Start-Sleep -Seconds 3
    $resized = $Screenshot -replace '\.png$', '-resized.png'
    & winapp ui screenshot -a PuTTY.App --output $resized 2>&1 | Out-Null
    $after = [SmokeNative]::CountColour($resized, 0, 0, 0)
    $greenAfter = [SmokeNative]::CountColour($resized, 0, 187, 0)
    Check 'the terminal grows with the window' ($after -gt $before * 1.1) "(black pixels $before -> $after)"
    Check 'the text is redrawn after the resize' ($greenAfter -gt 30) "($greenAfter green pixels)"

    # Scrollback and the scrollbar. 150 more lines push the banner out of view. The scrollbar is moved
    # through UI Automation (its RangeValue pattern), not with the mouse, so nothing is injected into the
    # desktop: that exercises engine -> ScrollbarChanged -> XAML ScrollBar -> ValueChanged -> engine -> repaint.
    $esc2 = [string][char]27
    $flood = New-Object System.Text.StringBuilder
    1..150 | ForEach-Object { [void]$flood.Append("scrollback line $_`r`n") }
    $floodBytes = [Text.Encoding]::UTF8.GetBytes($flood.ToString())
    $stream.Write($floodBytes, 0, $floodBytes.Length)
    Start-Sleep -Seconds 3
    $bottom = $Screenshot -replace '\.png$', '-bottom.png'
    & winapp ui screenshot -a PuTTY.App --output $bottom 2>&1 | Out-Null
    $props = (& winapp ui get-property TerminalScrollBar -a PuTTY.App 2>&1 | Out-String) -replace ($esc2 + '\[[0-9;]*m'), ''
    Check 'the scrollbar exists and is enabled once there is scrollback' ($props -match 'IsEnabled:\s*True') "($(($props -split "`n" | Where-Object { $_ -match 'AutomationId|IsEnabled' }) -join ' ' -replace '\s+', ' '))"
    $greenBottom = [SmokeNative]::CountColour($bottom, 0, 187, 0)
    Check 'the banner has scrolled out of view' ($greenBottom -lt 5) "($greenBottom green pixels at the bottom)"
    $setResult = (& winapp ui set-value TerminalScrollBar '0' -a PuTTY.App 2>&1 | Out-String)
    Start-Sleep -Seconds 2
    $top = $Screenshot -replace '\.png$', '-top.png'
    & winapp ui screenshot -a PuTTY.App --output $top 2>&1 | Out-Null
    $greenTop = [SmokeNative]::CountColour($top, 0, 187, 0)
    Check 'moving the scrollbar to the top scrolls the terminal back to the banner' ($greenTop -gt 30) "($greenTop green pixels at the top; $($setResult.Trim()))"
}
finally {
    $closedCleanly = $false
    if (-not $app.HasExited) { [void]$app.CloseMainWindow(); [void]$app.WaitForExit(10000) }
    if ($app.HasExited) { $closedCleanly = ($app.ExitCode -eq 0) } else { $app.Kill() }
    Check 'the app exits cleanly when its window is closed' $closedCleanly "(exit code $(if ($app.HasExited) { $app.ExitCode } else { 'still running' }))"
    $listener.Stop()
}
exit $failures
