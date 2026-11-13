<#
.SYNOPSIS
    Draws the app icon and the installer's pictures from code, so that they can be changed and drawn again.

.DESCRIPTION
    The icon is two monitors with a prompt on each screen and a lightning bolt between them (the old PuTTY icon, in
    the glossy style of Windows 11), on a blue Fluent tile. Everything is drawn with System.Drawing at four times the
    size and scaled down, once per size of the .ico (16 to 256 px; at 32 px and below the prompt and the reflections
    are left out and the bezel is thicker, which keeps the small sizes readable).

    Writes:
      src\PuTTY.App\Assets\AppIcon.ico       the app, its window, the Start menu shortcut, Add or remove programs
      installer\Assets\dialog.bmp            493 x 312, the welcome and finish dialogs (the picture on the left 164 px)
      installer\Assets\banner.bmp            493 x 58, the top of the inner dialogs
      installer\Assets\logo.png              64 x 64, the setup program's window

    The tile is placed by its visible edges, not by the picture's border: on the dialog picture it is as far from the
    top as from the left and the right, on the banner as far from the top as from the grey line at the bottom.
#>
param(
    [string] $IconPath = (Join-Path $PSScriptRoot '..\src\PuTTY.App\Assets\AppIcon.ico'),
    [string] $InstallerAssetsDir = (Join-Path $PSScriptRoot '..\installer\Assets')
)
Add-Type -AssemblyName System.Drawing
$ErrorActionPreference = 'Stop'
$IconPath = [System.IO.Path]::GetFullPath($IconPath)
$InstallerAssetsDir = [System.IO.Path]::GetFullPath($InstallerAssetsDir)
New-Item -ItemType Directory -Force -Path (Split-Path $IconPath), $InstallerAssetsDir | Out-Null

# ---- helpers ---------------------------------------------------------------------------------------------------------
function C([string] $hex, [int] $a = 255) { [System.Drawing.Color]::FromArgb($a, [System.Drawing.ColorTranslator]::FromHtml($hex)) }
function Pt([double] $x, [double] $y) { New-Object System.Drawing.PointF ([single]$x), ([single]$y) }
function Grad([double] $x1, [double] $y1, [double] $x2, [double] $y2, $c1, $c2) {
    New-Object System.Drawing.Drawing2D.LinearGradientBrush ((Pt $x1 $y1), (Pt $x2 $y2), $c1, $c2)
}
function RoundRect([double] $x, [double] $y, [double] $w, [double] $h, [double] $r) {
    $r = [Math]::Min($r, [Math]::Min($w, $h) / 2)
    $p = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = [single]($r * 2)
    $p.AddArc([single]$x, [single]$y, $d, $d, 180, 90)
    $p.AddArc([single]($x + $w - $d), [single]$y, $d, $d, 270, 90)
    $p.AddArc([single]($x + $w - $d), [single]($y + $h - $d), $d, $d, 0, 90)
    $p.AddArc([single]$x, [single]($y + $h - $d), $d, $d, 90, 90)
    $p.CloseFigure()
    return $p
}
function Squircle([double] $x, [double] $y, [double] $s, [double] $n = 5) {
    # a superellipse |x|^n + |y|^n = 1: rounder than a rounded square, like Windows 11 tiles
    $pts = New-Object 'System.Collections.Generic.List[System.Drawing.PointF]'
    for ($deg = 0; $deg -lt 360; $deg += 2) {
        $t = $deg * [Math]::PI / 180
        $c = [Math]::Cos($t); $sn = [Math]::Sin($t)
        $px = [Math]::Sign($c) * [Math]::Pow([Math]::Abs($c), 2 / $n)
        $py = [Math]::Sign($sn) * [Math]::Pow([Math]::Abs($sn), 2 / $n)
        $pts.Add((Pt ($x + $s / 2 + $px * $s / 2) ($y + $s / 2 + $py * $s / 2)))
    }
    $p = New-Object System.Drawing.Drawing2D.GraphicsPath
    $p.AddPolygon($pts.ToArray())
    return $p
}
function Soft-Shadow($g, $paths, [double] $dx, [double] $dy, [double] $k, [int] $alpha) {
    # the shape drawn small and scaled up again is a cheap blur; the box is the 1024 x 1024 of the drawing
    $n = [int](1024 / $k)
    $small = New-Object System.Drawing.Bitmap $n, $n, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $sg = [System.Drawing.Graphics]::FromImage($small)
    $sg.SmoothingMode = 'AntiAlias'
    $m = New-Object System.Drawing.Drawing2D.Matrix
    $m.Scale([single](1 / $k), [single](1 / $k))
    $m.Translate([single]$dx, [single]$dy)
    $sg.Transform = $m
    $brush = New-Object System.Drawing.SolidBrush (C '#001A40' $alpha)
    foreach ($p in $paths) { $sg.FillPath($brush, $p) }
    $sg.Dispose()
    $old = $g.InterpolationMode
    $g.InterpolationMode = 'HighQualityBicubic'
    $g.DrawImage($small, 0, 0, 1024, 1024)
    $g.InterpolationMode = $old
    $small.Dispose()
}

# ---- one monitor: stand, body with a gloss, a screen with a reflection and the prompt -----------------------------------
function Draw-Monitor($g, [double] $x, [double] $y, [double] $w, $col, [bool] $detail, [int] $shadow) {
    $h = $w * 0.72
    $r = $w * 0.09
    $neckW = $w * 0.17; $neckH = $w * 0.10
    $baseW = $w * 0.44; $baseH = $w * 0.065
    $body = RoundRect $x $y $w $h $r
    $neck = RoundRect ($x + $w / 2 - $neckW / 2) ($y + $h - $r) $neckW ($neckH + $r) ($neckW * 0.15)
    $base = RoundRect ($x + $w / 2 - $baseW / 2) ($y + $h + $neckH - $baseH * 0.5) $baseW $baseH ($baseH / 2)
    if ($shadow -gt 0) { Soft-Shadow $g @($body, $neck, $base) 0 ($w * 0.05) 10 $shadow }

    $stand = Grad $x ($y + $h) $x ($y + $h + $neckH + $baseH) $col.StandA $col.StandB
    $g.FillPath($stand, $neck)
    $g.FillPath($stand, $base)

    $g.FillPath((Grad $x $y ($x + $w) ($y + $h) $col.BodyA $col.BodyB), $body)
    $g.SetClip($body)
    $g.FillRectangle((Grad $x $y $x ($y + $h * 0.5) (C '#FFFFFF' 80) (C '#FFFFFF' 0)), $x, $y, $w, $h * 0.5)
    $g.ResetClip()
    if ($detail) {
        $rim = New-Object System.Drawing.Pen ((Grad $x $y $x ($y + $h) (C '#FFFFFF' 150) (C '#FFFFFF' 10)), [single]($w * 0.012))
        $g.DrawPath($rim, $body)
    }

    $m = $w * $(if ($detail) { 0.062 } else { 0.10 })
    $sx = $x + $m; $sy = $y + $m; $sw = $w - 2 * $m; $sh = $h - 2 * $m
    $screen = RoundRect $sx $sy $sw $sh ($r * 0.55)
    $g.FillPath((Grad $sx $sy ($sx + $sw) ($sy + $sh) $col.ScreenA $col.ScreenB), $screen)
    if ($detail) {
        $edge = New-Object System.Drawing.Pen ((C '#000F2E' 120), [single]($w * 0.008))
        $g.DrawPath($edge, $screen)
        $g.SetClip($screen)
        $g.FillPolygon((New-Object System.Drawing.SolidBrush (C '#FFFFFF' 28)), [System.Drawing.PointF[]]@((Pt $sx $sy), (Pt ($sx + $sw * 0.62) $sy), (Pt $sx ($sy + $sh * 0.82))))
        $g.FillRectangle((Grad $sx ($sy + $sh * 0.45) $sx ($sy + $sh) (C '#34B0FF' 0) (C '#34B0FF' 95)), $sx, ($sy + $sh * 0.45), $sw, ($sh * 0.55))
        $g.ResetClip()
        # the prompt: a chevron and an underscore
        $cx = $sx + $sw / 2; $cy = $sy + $sh / 2; $a = $sh * 0.15; $t = $sh * 0.09
        $pen = New-Object System.Drawing.Pen ($col.Prompt, [single]$t)
        $pen.StartCap = 'Round'; $pen.EndCap = 'Round'; $pen.LineJoin = 'Round'
        $g.DrawLines($pen, [System.Drawing.PointF[]]@((Pt ($cx - $a * 1.75) ($cy - $a)), (Pt ($cx - $a * 0.75) $cy), (Pt ($cx - $a * 1.75) ($cy + $a))))
        $g.DrawLine($pen, (Pt ($cx - $a * 0.05) ($cy + $a)), (Pt ($cx + $a * 1.55) ($cy + $a)))
    }
}

# ---- the lightning bolt ----------------------------------------------------------------------------------------------
function Draw-Bolt($g, [double] $x, [double] $y, [double] $w, [double] $h, $pal, [bool] $detail) {
    $rel = @(@(0.55, 0.0), @(0.86, 0.0), @(0.66, 0.36), @(0.94, 0.36), @(0.30, 1.0), @(0.42, 0.60), @(0.12, 0.60))
    $pts = New-Object 'System.Collections.Generic.List[System.Drawing.PointF]'
    foreach ($q in $rel) { $pts.Add((Pt ($x + $q[0] * $w) ($y + $q[1] * $h))) }
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $path.AddPolygon($pts.ToArray())
    Soft-Shadow $g @($path) 0 ($h * 0.035) 8 110
    $brush = Grad $x $y ($x + $w * 0.3) ($y + $h) $pal.BoltA $pal.BoltC
    $blend = New-Object System.Drawing.Drawing2D.ColorBlend 3
    $blend.Colors = [System.Drawing.Color[]]@($pal.BoltA, $pal.BoltB, $pal.BoltC)
    $blend.Positions = [single[]]@(0, 0.5, 1)
    $brush.InterpolationColors = $blend
    $round = New-Object System.Drawing.Pen ($brush, [single]($w * 0.07))
    $round.LineJoin = 'Round'
    $g.FillPath($brush, $path)
    $g.DrawPath($round, $path)
    if ($detail) {
        # a highlight down the left edge
        $g.SetClip($path)
        $g.FillPolygon((New-Object System.Drawing.SolidBrush (C '#FFFFFF' 70)), [System.Drawing.PointF[]]@(
            (Pt ($x + 0.50 * $w) $y), (Pt ($x + 0.66 * $w) $y), (Pt ($x + 0.27 * $w) ($y + 0.60 * $h)), (Pt ($x + 0.05 * $w) ($y + 0.64 * $h))))
        $g.ResetClip()
    }
}

# ---- the icon: white glass monitors and the bolt on a blue tile, in a 1024 x 1024 box ---------------------------------
$palette = @{
    Back  = @{ BodyA = (C '#FFFFFF'); BodyB = (C '#A9CDF3'); StandA = (C '#DCEBFA'); StandB = (C '#8FB9E6'); ScreenA = (C '#1456B4'); ScreenB = (C '#0B3478'); Prompt = (C '#9FD6FF') }
    Front = @{ BodyA = (C '#FFFFFF'); BodyB = (C '#C3DFF8'); StandA = (C '#E8F2FC'); StandB = (C '#A5CBEE'); ScreenA = (C '#0F4C9E'); ScreenB = (C '#082C66'); Prompt = (C '#C4F4FF') }
    BoltA = (C '#FFF59A'); BoltB = (C '#FFC21A'); BoltC = (C '#FF8A00')
}

function Render-Icon([int] $size) {
    $ss = if ($size -ge 64) { 4 } else { 8 }
    $S = $size * $ss
    $canvas = New-Object System.Drawing.Bitmap $S, $S, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($canvas)
    $g.SmoothingMode = 'AntiAlias'; $g.PixelOffsetMode = 'HighQuality'; $g.InterpolationMode = 'HighQualityBicubic'
    $g.ScaleTransform([single]($S / 1024), [single]($S / 1024))
    $detail = $size -ge 40

    $tile = Squircle 40 40 944 5
    Soft-Shadow $g @($tile) 0 24 12 90
    $g.FillPath((Grad 40 40 984 984 (C '#45B0FF') (C '#0B5CC0')), $tile)
    $g.SetClip($tile)
    $g.FillRectangle((Grad 40 40 40 560 (C '#FFFFFF' 70) (C '#FFFFFF' 0)), 40, 40, 944, 520)
    $g.ResetClip()

    # the monitors one top right (behind) and one bottom left (in front), the bolt between them along the line that joins them
    $g.TranslateTransform(512, 512); $g.ScaleTransform(0.72, 0.72); $g.TranslateTransform(-512, -512)
    Draw-Monitor $g 474.5 36 517 $palette.Back $detail 70
    Draw-Monitor $g 32.5 547.6 517 $palette.Front $detail 70
    Draw-Bolt $g 350 323 324 378 $palette $detail
    $g.Dispose()

    $result = New-Object System.Drawing.Bitmap $size, $size, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $rg = [System.Drawing.Graphics]::FromImage($result)
    $rg.InterpolationMode = 'HighQualityBicubic'; $rg.PixelOffsetMode = 'HighQuality'; $rg.SmoothingMode = 'AntiAlias'
    $rg.DrawImage($canvas, 0, 0, $size, $size)
    $rg.Dispose(); $canvas.Dispose()
    return $result
}

function Save-Ico([string] $path) {
    # PNG frames (Windows Vista and later read them at any size)
    $sizes = 16, 20, 24, 32, 40, 48, 64, 128, 256
    $frames = New-Object 'System.Collections.Generic.List[byte[]]'
    foreach ($s in $sizes) {
        $bmp = Render-Icon $s
        $ms = New-Object System.IO.MemoryStream
        $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
        $frames.Add($ms.ToArray())
        $bmp.Dispose()
    }
    $fs = [System.IO.File]::Create($path)
    $bw = New-Object System.IO.BinaryWriter $fs
    $bw.Write([uint16]0); $bw.Write([uint16]1); $bw.Write([uint16]$sizes.Count)
    $offset = 6 + 16 * $sizes.Count
    for ($i = 0; $i -lt $sizes.Count; $i++) {
        $dim = if ($sizes[$i] -ge 256) { 0 } else { $sizes[$i] }
        $bw.Write([byte]$dim); $bw.Write([byte]$dim); $bw.Write([byte]0); $bw.Write([byte]0)
        $bw.Write([uint16]1); $bw.Write([uint16]32)
        $bw.Write([uint32]$frames[$i].Length); $bw.Write([uint32]$offset)
        $offset += $frames[$i].Length
    }
    foreach ($f in $frames) { $bw.Write($f) }
    $bw.Dispose()
}

# ---- the installer's pictures ----------------------------------------------------------------------------------------
function Visible-Bounds($bmp) {
    # where the tile itself is inside the picture: the transparent border and the soft shadow do not count
    $minX = $bmp.Width; $minY = $bmp.Height; $maxX = -1; $maxY = -1
    for ($y = 0; $y -lt $bmp.Height; $y++) {
        for ($x = 0; $x -lt $bmp.Width; $x++) {
            if ($bmp.GetPixel($x, $y).A -ge 128) {
                if ($x -lt $minX) { $minX = $x }; if ($x -gt $maxX) { $maxX = $x }
                if ($y -lt $minY) { $minY = $y }; if ($y -gt $maxY) { $maxY = $y }
            }
        }
    }
    return @{ MinX = $minX; MinY = $minY; MaxX = $maxX + 1; MaxY = $maxY + 1 }
}
function Canvas([int] $w, [int] $h) {
    $bmp = New-Object System.Drawing.Bitmap $w, $h, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'; $g.InterpolationMode = 'HighQualityBicubic'; $g.PixelOffsetMode = 'HighQuality'
    return @{ Bitmap = $bmp; G = $g }
}
function Save-Bmp24($bitmap, [string] $path) {
    # the installer wants 24-bit bitmaps
    $flat = New-Object System.Drawing.Bitmap $bitmap.Width, $bitmap.Height, ([System.Drawing.Imaging.PixelFormat]::Format24bppRgb)
    $fg = [System.Drawing.Graphics]::FromImage($flat); $fg.DrawImage($bitmap, 0, 0, $bitmap.Width, $bitmap.Height); $fg.Dispose()
    $flat.Save($path, [System.Drawing.Imaging.ImageFormat]::Bmp)
    $flat.Dispose()
}

Save-Ico $IconPath
"wrote $IconPath"

$icon = Render-Icon 256
$vis = Visible-Bounds $icon

# dialog.bmp: a soft blue panel on the left 164 px, white to the right where the dialog's text goes; the tile as far from the
# top as from the left and the right
$d = Canvas 493 312
$d.G.Clear([System.Drawing.Color]::White)
$d.G.FillRectangle((New-Object System.Drawing.Drawing2D.LinearGradientBrush ((New-Object System.Drawing.Rectangle 0, 0, 164, 312), (C '#F2F8FE'), (C '#BFDDF7'), 70)), 0, 0, 164, 312)
$d.G.SetClip((New-Object System.Drawing.Rectangle 0, 0, 164, 312))
$soft = New-Object System.Drawing.SolidBrush (C '#FFFFFF' 40)
$d.G.FillEllipse($soft, -60, 190, 220, 220)
$d.G.FillEllipse($soft, 40, -70, 200, 200)
$d.G.ResetClip()
$k = 136 / 256
$margin = (164 - ($vis.MaxX - $vis.MinX) * $k) / 2
$d.G.DrawImage($icon, [single]($margin - $vis.MinX * $k), [single]($margin - $vis.MinY * $k), [single]136, [single]136)
Save-Bmp24 $d.Bitmap (Join-Path $InstallerAssetsDir 'dialog.bmp')
'dialog.bmp: the tile is {0:N1} px from the left, the right and the top' -f $margin

# banner.bmp: white, the tile at the right centred between the top and the grey line (57 px of white above it)
$b = Canvas 493 58
$b.G.Clear([System.Drawing.Color]::White)
$bm = 5
$bk = (57 - 2 * $bm) / ($vis.MaxY - $vis.MinY)
$b.G.DrawImage($icon, [single](483 - $vis.MaxX * $bk), [single]($bm - $vis.MinY * $bk), [single](256 * $bk), [single](256 * $bk))
$b.G.FillRectangle((New-Object System.Drawing.SolidBrush (C '#D9D9D9')), 0, 57, 493, 1)
Save-Bmp24 $b.Bitmap (Join-Path $InstallerAssetsDir 'banner.bmp')
'banner.bmp: the tile is {0} px from the top and {0} px above the grey line' -f $bm

# logo.png: the setup program's window
$l = Canvas 64 64
$l.G.DrawImage($icon, 0, 0, 64, 64)
$l.Bitmap.Save((Join-Path $InstallerAssetsDir 'logo.png'), [System.Drawing.Imaging.ImageFormat]::Png)
'logo.png written'
