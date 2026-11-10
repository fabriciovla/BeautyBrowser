# Generates the app icons from the logo (assets\BeautyBrowser-1024.png): assets\BeautyBrowser.ico (16 to 256 px, PNG
# frames inside the .ico) and assets\BeautyBrowser.png (256 px).
param([string]$OutDir = (Join-Path $PSScriptRoot "..\assets"))

Add-Type -AssemblyName System.Drawing
$source = Join-Path $OutDir "BeautyBrowser-1024.png"
if (-not (Test-Path $source)) { throw "Missing logo: $source" }
$logo = [System.Drawing.Image]::FromFile($source)

function New-LogoPng([int]$size) {
    $bmp = New-Object System.Drawing.Bitmap $size, $size, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.InterpolationMode = 'HighQualityBicubic'
    $g.PixelOffsetMode = 'HighQuality'
    $g.SmoothingMode = 'HighQuality'
    $g.CompositingQuality = 'HighQuality'
    $g.Clear([System.Drawing.Color]::Transparent)
    # TileFlipXY avoids a dark border when downscaling.
    $attributes = New-Object System.Drawing.Imaging.ImageAttributes
    $attributes.SetWrapMode([System.Drawing.Drawing2D.WrapMode]::TileFlipXY)
    $rect = New-Object System.Drawing.Rectangle 0, 0, $size, $size
    $g.DrawImage($logo, $rect, 0, 0, $logo.Width, $logo.Height, [System.Drawing.GraphicsUnit]::Pixel, $attributes)

    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $g.Dispose(); $bmp.Dispose(); $attributes.Dispose()
    return ,$ms.ToArray()
}

$sizes = 16, 20, 24, 32, 40, 48, 64, 96, 128, 256
$images = foreach ($s in $sizes) { ,(New-LogoPng $s) }
$logo.Dispose()

# .ico layout: a header, one directory entry per image, then the PNGs.
$ico = New-Object System.IO.MemoryStream
$w = New-Object System.IO.BinaryWriter $ico
$w.Write([uint16]0); $w.Write([uint16]1); $w.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $s = $sizes[$i]; $data = $images[$i]
    $w.Write([byte]($(if ($s -ge 256) { 0 } else { $s })))
    $w.Write([byte]($(if ($s -ge 256) { 0 } else { $s })))
    $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([uint16]1); $w.Write([uint16]32)
    $w.Write([uint32]$data.Length); $w.Write([uint32]$offset)
    $offset += $data.Length
}
foreach ($data in $images) { $w.Write($data) }
$w.Flush()
[System.IO.File]::WriteAllBytes((Join-Path $OutDir "BeautyBrowser.ico"), $ico.ToArray())
[System.IO.File]::WriteAllBytes((Join-Path $OutDir "BeautyBrowser.png"), $images[-1])
"icon: $(Join-Path $OutDir 'BeautyBrowser.ico')"
