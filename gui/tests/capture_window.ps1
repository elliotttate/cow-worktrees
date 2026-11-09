#Requires -Version 5.1
<#
capture_window.ps1 - saves a PNG of a window with PrintWindow(PW_RENDERFULLCONTENT), which captures the
window's own content even when other windows cover it, without activating it.
  capture_window.ps1 -ProcessId <pid> -Out <png>
#>
param([Parameter(Mandatory)][int]$ProcessId, [Parameter(Mandatory)][string]$Out)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class WinCap {
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h, IntPtr hdc, uint flags);
    [DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(IntPtr h, int attr, out RECT r, int size);
    [DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(IntPtr v);
}
'@
[void][WinCap]::SetProcessDpiAwarenessContext([IntPtr]-4)   # per-monitor v2, so sizes are physical pixels
$p = Get-Process -Id $ProcessId
$h = $p.MainWindowHandle
if ($h -eq [IntPtr]::Zero) { throw "process $ProcessId has no main window" }
$r = New-Object WinCap+RECT
[void][WinCap]::GetWindowRect($h, [ref]$r)
$w = $r.Right - $r.Left; $hgt = $r.Bottom - $r.Top
$bmp = New-Object Drawing.Bitmap($w, $hgt)
$g = [Drawing.Graphics]::FromImage($bmp)
$hdc = $g.GetHdc()
$ok = [WinCap]::PrintWindow($h, $hdc, 2)
$g.ReleaseHdc($hdc)
# Trim the invisible resize borders DWM adds around the visible frame.
$f = New-Object WinCap+RECT
if ([WinCap]::DwmGetWindowAttribute($h, 9, [ref]$f, 16) -eq 0) {
    # Both rectangles are physical pixels only when this process is per-monitor aware; otherwise skip the trim.
    $crop = New-Object Drawing.Rectangle(($f.Left - $r.Left), ($f.Top - $r.Top), ($f.Right - $f.Left), ($f.Bottom - $f.Top))
    $crop.Intersect((New-Object Drawing.Rectangle(0, 0, $w, $hgt)))
    if ($crop.Width -gt $w / 2 -and $crop.Height -gt $hgt / 2 -and ($crop.Width -ne $w -or $crop.Height -ne $hgt)) {
        $bmp2 = $bmp.Clone($crop, $bmp.PixelFormat); $bmp.Dispose(); $bmp = $bmp2
    }
}
# When this process is not per-monitor aware, the bitmap is larger than what PrintWindow drew; cut the
# undrawn (fully transparent) margin off the right and bottom, probing along the middle row and column.
$right = $bmp.Width; $bottom = $bmp.Height
$midY = [int]($bmp.Height / 2); $midX = [int]($bmp.Width / 4)
while ($right -gt 1 -and $bmp.GetPixel($right - 1, $midY).A -eq 0) { $right-- }
while ($bottom -gt 1 -and $bmp.GetPixel($midX, $bottom - 1).A -eq 0) { $bottom-- }
if ($right -lt $bmp.Width -or $bottom -lt $bmp.Height) {
    $bmp2 = $bmp.Clone((New-Object Drawing.Rectangle(0, 0, $right, $bottom)), $bmp.PixelFormat); $bmp.Dispose(); $bmp = $bmp2
}
$bmp.Save($Out, [Drawing.Imaging.ImageFormat]::Png)
$g.Dispose(); $bmp.Dispose()
if (-not $ok) { throw 'PrintWindow failed' }
Write-Output $Out
