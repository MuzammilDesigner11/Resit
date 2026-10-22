param(
    [string]$OutDir = (Join-Path $PSScriptRoot 'png'),
    [string]$Ico = (Join-Path $PSScriptRoot '..\src\Resit\Resit.ico')
)
# Renders the Resit logo (same shapes as assets/logo.svg) with WPF at every icon size, saves
# PNGs, and packs the icon sizes into one .ico (PNG entries, supported since Windows Vista).
# Run with: powershell -STA -File assets\make-icon.ps1
Add-Type -AssemblyName PresentationCore, PresentationFramework, WindowsBase
New-Item -ItemType Directory -Force $OutDir | Out-Null

$cream = New-Object System.Windows.Media.SolidColorBrush ([System.Windows.Media.Color]::FromRgb(0xF4, 0xEF, 0xE6))
$ink = New-Object System.Windows.Media.SolidColorBrush ([System.Windows.Media.Color]::FromRgb(0x1E, 0x1C, 0x19))
function Pen($brush, $width) { $p = New-Object System.Windows.Media.Pen $brush, $width; $p.LineJoin = 'Round'; $p }
$teal = New-Object System.Windows.Media.SolidColorBrush ([System.Windows.Media.Color]::FromRgb(0x1F, 0x6F, 0x78))
$legPen = New-Object System.Windows.Media.Pen $ink, 72; $legPen.StartLineCap = 'Round'; $legPen.EndLineCap = 'Round'

function Render([int]$size) {
    $dv = New-Object System.Windows.Media.DrawingVisual
    $dc = $dv.RenderOpen()
    $s = $size / 512.0
    $dc.PushTransform((New-Object System.Windows.Media.ScaleTransform $s, $s))
    $dc.DrawRoundedRectangle($cream, $null, (New-Object System.Windows.Rect 0, 0, 512, 512), 116, 116)
    # Geometric R: pill stem, lake-teal circle bowl, rounded leg (same as assets/logo.svg).
    $dc.DrawRoundedRectangle($ink, $null, (New-Object System.Windows.Rect 130, 114, 72, 284), 36, 36)
    $dc.DrawEllipse($teal, $null, (New-Object System.Windows.Point 302, 194), 80, 80)
    $dc.DrawLine($legPen, (New-Object System.Windows.Point 278, 322), (New-Object System.Windows.Point 318, 362))
    $dc.Pop(); $dc.Close()
    $bmp = New-Object System.Windows.Media.Imaging.RenderTargetBitmap $size, $size, 96, 96, ([System.Windows.Media.PixelFormats]::Pbgra32)
    $bmp.Render($dv)
    $enc = New-Object System.Windows.Media.Imaging.PngBitmapEncoder
    $enc.Frames.Add([System.Windows.Media.Imaging.BitmapFrame]::Create($bmp))
    $ms = New-Object IO.MemoryStream; $enc.Save($ms); return ,$ms.ToArray()
}

$sizes = 16, 20, 24, 32, 40, 48, 64, 96, 128, 256
$images = @{}
foreach ($sz in $sizes + 512 + 1024) {
    $png = Render $sz
    [IO.File]::WriteAllBytes((Join-Path $OutDir "logo-$sz.png"), $png)
    if ($sz -in $sizes) { $images[$sz] = $png }
}

# ICONDIR + ICONDIRENTRY[] + PNG blobs.
$fs = [IO.File]::Create($Ico); $w = New-Object IO.BinaryWriter $fs
$w.Write([uint16]0); $w.Write([uint16]1); $w.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
foreach ($sz in $sizes) {
    $len = $images[$sz].Length
    $dim = if ($sz -ge 256) { 0 } else { $sz }
    $w.Write([byte]$dim); $w.Write([byte]$dim); $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([uint16]1); $w.Write([uint16]32); $w.Write([uint32]$len); $w.Write([uint32]$offset)
    $offset += $len
}
foreach ($sz in $sizes) { $w.Write($images[$sz]) }
$w.Close()
"icon: $Ico ($((Get-Item $Ico).Length) bytes, $($sizes.Count) sizes)"
