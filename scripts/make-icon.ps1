# Draws the app icon (src/HouseControl/Assets/HouseControl.ico) from assets/app-icon-1024.png:
# the artwork with Windows 11's rounded corners, at every size the shell asks for.
# Run it again after changing the artwork:  powershell -File scripts/make-icon.ps1
param(
  [string]$Source = "$PSScriptRoot\..\assets\app-icon-1024.png",
  [string]$Output = "$PSScriptRoot\..\src\HouseControl\Assets\HouseControl.ico"
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$art = [System.Drawing.Image]::FromFile((Resolve-Path $Source))
$sizes = 16, 20, 24, 32, 40, 48, 64, 96, 128, 256
$pngs = foreach ($size in $sizes) {
  $bmp = New-Object System.Drawing.Bitmap $size, $size, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
  $g = [System.Drawing.Graphics]::FromImage($bmp)
  $g.SmoothingMode = 'AntiAlias'
  $g.InterpolationMode = 'HighQualityBicubic'
  $g.PixelOffsetMode = 'HighQuality'
  $g.CompositingQuality = 'HighQuality'
  # A rounded square, inset a little at large sizes like other Windows app icons.
  $inset = if ($size -ge 48) { [math]::Round($size * 0.04) } else { 0 }
  $s = $size - 2 * $inset
  $r = [math]::Max(2, $s * 0.2)
  $path = New-Object System.Drawing.Drawing2D.GraphicsPath
  $path.AddArc($inset, $inset, 2 * $r, 2 * $r, 180, 90)
  $path.AddArc($inset + $s - 2 * $r, $inset, 2 * $r, 2 * $r, 270, 90)
  $path.AddArc($inset + $s - 2 * $r, $inset + $s - 2 * $r, 2 * $r, 2 * $r, 0, 90)
  $path.AddArc($inset, $inset + $s - 2 * $r, 2 * $r, 2 * $r, 90, 90)
  $path.CloseFigure()
  $g.SetClip($path)
  $g.DrawImage($art, $inset, $inset, $s, $s)
  $g.ResetClip()
  # A hairline edge keeps the dark tile visible on a dark taskbar.
  $pen = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(48, 255, 255, 255)), ([math]::Max(1, $size / 128))
  $g.DrawPath($pen, $path)
  $g.Dispose()
  $ms = New-Object System.IO.MemoryStream
  $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
  $bmp.Dispose()
  , $ms.ToArray()
}
$art.Dispose()

# ICONDIR + ICONDIRENTRY[] + PNG images (Windows Vista and newer read PNG entries at every size).
$out = New-Object System.IO.MemoryStream
$w = New-Object System.IO.BinaryWriter $out
$w.Write([uint16]0); $w.Write([uint16]1); $w.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
  $dim = if ($sizes[$i] -ge 256) { 0 } else { $sizes[$i] }
  $w.Write([byte]$dim); $w.Write([byte]$dim); $w.Write([byte]0); $w.Write([byte]0)
  $w.Write([uint16]1); $w.Write([uint16]32)
  $w.Write([uint32]$pngs[$i].Length); $w.Write([uint32]$offset)
  $offset += $pngs[$i].Length
}
foreach ($png in $pngs) { $w.Write($png) }
$w.Flush()
[System.IO.File]::WriteAllBytes([System.IO.Path]::GetFullPath($Output), $out.ToArray())
"Wrote $Output ($($out.Length) bytes)"
