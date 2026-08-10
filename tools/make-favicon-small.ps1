Add-Type -AssemblyName System.Drawing

# Intermediates go to a scratch directory, never into the repo. Only make-ico.ps1 installs the
# three shipped files into wwwroot.
$work = Join-Path ([System.IO.Path]::GetTempPath()) "eaw-favicon"
New-Item -ItemType Directory -Force -Path $work | Out-Null

# A SIMPLIFIED glyph for 16/32px. The full card - pie plus three pills plus a slider - turns to mud
# below about 48px: at 16px the pills merge into a single grey bar and the pie loses its wedge.
# Standard practice for icon sets, and the reason .ico is a multi-image format.
#
# What survives the reduction: the teal rounded card, and the pie with its orange wedge. Those two
# carry the identity; the pills and slider are detail that only exists to be seen large.

$S = 256
$bmp = New-Object System.Drawing.Bitmap($S, $S, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.SmoothingMode = 'AntiAlias'
$g.Clear([System.Drawing.Color]::Transparent)

$teal   = [System.Drawing.Color]::FromArgb(255, 125, 207, 201)
$blue   = [System.Drawing.Color]::FromArgb(255,  74, 124, 184)
$orange = [System.Drawing.Color]::FromArgb(255, 251, 176,  64)
$ink    = [System.Drawing.Color]::FromArgb(255,   0,   0,   0)

$bTeal   = New-Object System.Drawing.SolidBrush($teal)
$bBlue   = New-Object System.Drawing.SolidBrush($blue)
$bOrange = New-Object System.Drawing.SolidBrush($orange)

# Proportionally heavier than the large glyph: a stroke that reads at 512px vanishes at 16px.
$w = [int]($S * 0.062)
$pen = New-Object System.Drawing.Pen($ink, $w)
$pen.LineJoin = 'Round'

function Get-RoundedPath([single]$x, [single]$y, [single]$wd, [single]$ht, [single]$r) {
    $p = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = $r * 2
    $p.AddArc($x, $y, $d, $d, 180, 90)
    $p.AddArc($x + $wd - $d, $y, $d, $d, 270, 90)
    $p.AddArc($x + $wd - $d, $y + $ht - $d, $d, $d, 0, 90)
    $p.AddArc($x, $y + $ht - $d, $d, $d, 90, 90)
    $p.CloseFigure()
    return $p
}

# Card fills more of the canvas than the large glyph: at 16px, margin is wasted pixels.
$m = $S * 0.055
$card = Get-RoundedPath $m $m ($S - 2 * $m) ($S - 2 * $m) ($S * 0.14)
$g.FillPath($bTeal, $card)
$g.DrawPath($pen, $card)

# One large pie, centred. A 100-degree wedge from 12 o'clock, as in the reference.
$pr = $S * 0.285
$pcx = $S * 0.5
$pcy = $S * 0.5
$px = $pcx - $pr
$py = $pcy - $pr
$pd = $pr * 2

$g.FillEllipse($bBlue, $px, $py, $pd, $pd)
$g.FillPie($bOrange, $px, $py, $pd, $pd, -90, 100)
$g.DrawEllipse($pen, $px, $py, $pd, $pd)
$g.DrawPie($pen, $px, $py, $pd, $pd, -90, 100)

$card.Dispose()
$bmp.Save("$work\favicon-small-master.png", [System.Drawing.Imaging.ImageFormat]::Png)

foreach ($size in 48, 32, 16) {
    $out = New-Object System.Drawing.Bitmap($size, $size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $og = [System.Drawing.Graphics]::FromImage($out)
    $og.InterpolationMode = 'HighQualityBicubic'
    $og.PixelOffsetMode = 'HighQuality'
    $og.SmoothingMode = 'AntiAlias'
    $og.Clear([System.Drawing.Color]::Transparent)
    $og.DrawImage($bmp, 0, 0, $size, $size)
    $og.Dispose()
    $out.Save("$work\favicon-small-$size.png", [System.Drawing.Imaging.ImageFormat]::Png)
    $out.Dispose()
}

$g.Dispose(); $bmp.Dispose()
Write-Output "wrote small glyph 48/32/16"
