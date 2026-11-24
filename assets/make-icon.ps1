# Generates assets\icon.ico (16-256 px) and assets\icon-256.png / logo.png for OpenM50 Webcam.
# Pure System.Drawing, so the icon can be regenerated without any design tool.
Add-Type -AssemblyName System.Drawing
$ErrorActionPreference = 'Stop'
$out = $PSScriptRoot

function New-RoundedRect([float]$x, [float]$y, [float]$w, [float]$h, [float]$r) {
    $p = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = 2 * $r
    $p.AddArc($x, $y, $d, $d, 180, 90)
    $p.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
    $p.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90)
    $p.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
    $p.CloseFigure()
    return $p
}

function Draw-Icon([int]$size) {
    $bmp = New-Object System.Drawing.Bitmap($size, $size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $g.PixelOffsetMode = 'HighQuality'
    $g.Clear([System.Drawing.Color]::Transparent)
    $s = $size / 256.0

    # Background tile: dark navy gradient
    $tile = New-RoundedRect (8 * $s) (8 * $s) (240 * $s) (240 * $s) (52 * $s)
    $bg = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
        (New-Object System.Drawing.PointF(0, 0)), (New-Object System.Drawing.PointF(0, $size)),
        [System.Drawing.Color]::FromArgb(255, 36, 48, 66), [System.Drawing.Color]::FromArgb(255, 14, 19, 28))
    $g.FillPath($bg, $tile)

    # Camera body (light grey) with viewfinder hump
    $bodyBrush = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
        (New-Object System.Drawing.PointF(0, (70 * $s))), (New-Object System.Drawing.PointF(0, (200 * $s))),
        [System.Drawing.Color]::FromArgb(255, 238, 241, 245), [System.Drawing.Color]::FromArgb(255, 190, 198, 210))
    $hump = New-RoundedRect (92 * $s) (58 * $s) (72 * $s) (40 * $s) (12 * $s)
    $g.FillPath($bodyBrush, $hump)
    $body = New-RoundedRect (36 * $s) (82 * $s) (184 * $s) (118 * $s) (26 * $s)
    $g.FillPath($bodyBrush, $body)

    # Lens: dark ring, blue/teal glass, highlight
    $cx = 128 * $s; $cy = 142 * $s
    $r1 = 50 * $s; $r2 = 40 * $s; $r3 = 26 * $s
    $g.FillEllipse((New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(255, 22, 28, 38))), $cx - $r1, $cy - $r1, 2 * $r1, 2 * $r1)
    $glass = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
        (New-Object System.Drawing.PointF(($cx - $r2), ($cy - $r2))), (New-Object System.Drawing.PointF(($cx + $r2), ($cy + $r2))),
        [System.Drawing.Color]::FromArgb(255, 46, 196, 182), [System.Drawing.Color]::FromArgb(255, 58, 110, 255))
    $g.FillEllipse($glass, $cx - $r2, $cy - $r2, 2 * $r2, 2 * $r2)
    $g.FillEllipse((New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(255, 14, 22, 40))), $cx - $r3, $cy - $r3, 2 * $r3, 2 * $r3)
    if ($size -ge 32) {
        $hl = 9 * $s
        $g.FillEllipse((New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(200, 255, 255, 255))), $cx - 14 * $s, $cy - 16 * $s, $hl, $hl)
    }

    # "Live" dot (teal, not red)
    $d = 16 * $s
    $g.FillEllipse((New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(255, 46, 196, 182))), 186 * $s, 98 * $s, $d, $d)

    $g.Dispose()
    return $bmp
}

$sizes = 16, 20, 24, 32, 40, 48, 64, 128, 256
$pngs = @()
foreach ($sz in $sizes) {
    $bmp = Draw-Icon $sz
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $pngs += , @($sz, $ms.ToArray())
    if ($sz -eq 256) { $bmp.Save((Join-Path $out 'icon-256.png'), [System.Drawing.Imaging.ImageFormat]::Png) }
    $bmp.Dispose()
}

# ICO container with PNG-compressed images
$fs = [System.IO.File]::Create((Join-Path $out 'icon.ico'))
$w = New-Object System.IO.BinaryWriter($fs)
$w.Write([UInt16]0); $w.Write([UInt16]1); $w.Write([UInt16]$pngs.Count)
$offset = 6 + 16 * $pngs.Count
foreach ($p in $pngs) {
    $sz = $p[0]; $data = $p[1]
    $dim = if ($sz -ge 256) { 0 } else { $sz }
    $w.Write([byte]$dim); $w.Write([byte]$dim); $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([UInt16]1); $w.Write([UInt16]32); $w.Write([UInt32]$data.Length); $w.Write([UInt32]$offset)
    $offset += $data.Length
}
foreach ($p in $pngs) { $w.Write([byte[]]$p[1]) }
$w.Close()

# Wide logo for the README / installer
$logoW = 1200; $logoH = 300
$logo = New-Object System.Drawing.Bitmap($logoW, $logoH, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
$g = [System.Drawing.Graphics]::FromImage($logo)
$g.SmoothingMode = 'AntiAlias'; $g.TextRenderingHint = 'AntiAliasGridFit'
$g.Clear([System.Drawing.Color]::Transparent)
$icon = Draw-Icon 240
$g.DrawImage($icon, 20, 30, 240, 240)
$fontBig = New-Object System.Drawing.Font('Segoe UI Semibold', 72, [System.Drawing.FontStyle]::Regular, [System.Drawing.GraphicsUnit]::Pixel)
$fontSmall = New-Object System.Drawing.Font('Segoe UI', 34, [System.Drawing.FontStyle]::Regular, [System.Drawing.GraphicsUnit]::Pixel)
$g.DrawString('OpenM50 Webcam', $fontBig, (New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(255, 58, 110, 255))), 290, 70)
$g.DrawString("Canon EOS M50 as a USB webcam $([char]0x2013) free & open source", $fontSmall, (New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(255, 128, 136, 150))), 294, 165)
$g.Dispose()
$logo.Save((Join-Path $out 'logo.png'), [System.Drawing.Imaging.ImageFormat]::Png)

# Installer wizard images (Inno Setup): small 55x55 and large 164x314 (plus 2x variants)
foreach ($pair in @(@(55, 'wizard-small.bmp'), @(110, 'wizard-small@2x.bmp'))) {
    $b = New-Object System.Drawing.Bitmap($pair[0], $pair[0])
    $gg = [System.Drawing.Graphics]::FromImage($b); $gg.Clear([System.Drawing.Color]::White)
    $i = Draw-Icon $pair[0]; $gg.DrawImage($i, 0, 0, $pair[0], $pair[0]); $gg.Dispose()
    $b.Save((Join-Path $out $pair[1]), [System.Drawing.Imaging.ImageFormat]::Bmp); $b.Dispose()
}
foreach ($pair in @(@(1, 'wizard-large.bmp'), @(2, 'wizard-large@2x.bmp'))) {
    $k = $pair[0]; $W = 164 * $k; $H = 314 * $k
    $b = New-Object System.Drawing.Bitmap($W, $H)
    $gg = [System.Drawing.Graphics]::FromImage($b); $gg.SmoothingMode = 'AntiAlias'
    $grad = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
        (New-Object System.Drawing.PointF(0, 0)), (New-Object System.Drawing.PointF(0, $H)),
        [System.Drawing.Color]::FromArgb(255, 36, 48, 66), [System.Drawing.Color]::FromArgb(255, 14, 19, 28))
    $gg.FillRectangle($grad, 0, 0, $W, $H)
    $i = Draw-Icon (120 * $k); $gg.DrawImage($i, 22 * $k, 60 * $k, 120 * $k, 120 * $k)
    $f = New-Object System.Drawing.Font('Segoe UI Semibold', (17 * $k), [System.Drawing.FontStyle]::Regular, [System.Drawing.GraphicsUnit]::Pixel)
    $sf = New-Object System.Drawing.StringFormat; $sf.Alignment = 'Center'
    $gg.DrawString("OpenM50`nWebcam", $f, [System.Drawing.Brushes]::White, (New-Object System.Drawing.RectangleF(0, (200 * $k), $W, (60 * $k))), $sf)
    $gg.Dispose()
    $b.Save((Join-Path $out $pair[1]), [System.Drawing.Imaging.ImageFormat]::Bmp); $b.Dispose()
}
Write-Host "Icons written to $out"
