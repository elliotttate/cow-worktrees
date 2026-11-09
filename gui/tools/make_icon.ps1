#Requires -Version 5.1
<#
make_icon.ps1 - draws the app icon (a Git branch on a rounded square, with an orange "dirty" dot) and writes
CowWorktrees\Assets\AppIcon.ico (16-256 px, PNG-compressed entries) and AppIcon.png (64 px, title bar).
#>
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$assets = Join-Path $PSScriptRoot '..\CowWorktrees\Assets'
New-Item -ItemType Directory -Force $assets | Out-Null

function Draw([int]$size) {
    $bmp = New-Object Drawing.Bitmap($size, $size, [Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'; $g.InterpolationMode = 'HighQualityBicubic'; $g.PixelOffsetMode = 'HighQuality'
    $s = $size / 256.0
    $g.ScaleTransform($s, $s)

    # Rounded square with a violet-to-blue gradient.
    $path = New-Object Drawing.Drawing2D.GraphicsPath
    $r = 56; $x = 8; $y = 8; $w = 240; $h = 240
    $path.AddArc($x, $y, $r, $r, 180, 90); $path.AddArc($x + $w - $r, $y, $r, $r, 270, 90)
    $path.AddArc($x + $w - $r, $y + $h - $r, $r, $r, 0, 90); $path.AddArc($x, $y + $h - $r, $r, $r, 90, 90)
    $path.CloseFigure()
    $bg = New-Object Drawing.Drawing2D.LinearGradientBrush((New-Object Drawing.Point(0, 0)), (New-Object Drawing.Point(256, 256)),
        [Drawing.Color]::FromArgb(255, 108, 76, 230), [Drawing.Color]::FromArgb(255, 38, 112, 232))
    $g.FillPath($bg, $path)

    # Branch: a trunk with two commits and a side branch curving back in.
    $pen = New-Object Drawing.Pen([Drawing.Color]::White, 18)
    $pen.StartCap = 'Round'; $pen.EndCap = 'Round'
    $g.DrawLine($pen, 92, 78, 92, 178)
    $g.DrawBezier($pen, 164, 100, 164, 150, 92, 132, 92, 172)
    $white = [Drawing.Brushes]::White
    foreach ($c in @(@(92, 66), @(92, 190), @(164, 92))) { $g.FillEllipse($white, $c[0] - 24, $c[1] - 24, 48, 48) }
    $hole = New-Object Drawing.SolidBrush([Drawing.Color]::FromArgb(255, 80, 92, 230))
    foreach ($c in @(@(92, 66), @(92, 190), @(164, 92))) { $g.FillEllipse($hole, $c[0] - 11, $c[1] - 11, 22, 22) }

    # The "dirty" dot.
    $g.FillEllipse((New-Object Drawing.SolidBrush([Drawing.Color]::FromArgb(255, 250, 176, 40))), 160, 160, 64, 64)
    $g.DrawEllipse((New-Object Drawing.Pen([Drawing.Color]::White, 8)), 160, 160, 64, 64)
    $g.Dispose()
    return $bmp
}

function PngBytes($bmp) { $ms = New-Object IO.MemoryStream; $bmp.Save($ms, [Drawing.Imaging.ImageFormat]::Png); return , ([byte[]]$ms.ToArray()) }

$sizes = 16, 20, 24, 32, 40, 48, 64, 256
$images = New-Object 'System.Collections.Generic.List[byte[]]'
foreach ($sz in $sizes) { $b = Draw $sz; $images.Add((PngBytes $b)); $b.Dispose() }

$out = New-Object IO.MemoryStream
$bw = New-Object IO.BinaryWriter($out)
$bw.Write([uint16]0); $bw.Write([uint16]1); $bw.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $sz = $sizes[$i]; $len = $images[$i].Length
    $bw.Write([byte]($(if ($sz -ge 256) { 0 } else { $sz }))); $bw.Write([byte]($(if ($sz -ge 256) { 0 } else { $sz })))
    $bw.Write([byte]0); $bw.Write([byte]0); $bw.Write([uint16]1); $bw.Write([uint16]32)
    $bw.Write([uint32]$len); $bw.Write([uint32]$offset)
    $offset += $len
}
foreach ($img in $images) { $bw.Write($img) }
$bw.Flush()
[IO.File]::WriteAllBytes((Join-Path $assets 'AppIcon.ico'), $out.ToArray())
$b = Draw 64; $b.Save((Join-Path $assets 'AppIcon.png'), [Drawing.Imaging.ImageFormat]::Png); $b.Dispose()
Get-ChildItem $assets | Select-Object Name, Length
