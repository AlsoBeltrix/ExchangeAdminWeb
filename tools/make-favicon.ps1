Add-Type -AssemblyName System.Drawing

# Intermediates go to a scratch directory, never into the repo. Only make-ico.ps1 installs the
# three shipped files into wwwroot.
$work = Join-Path ([System.IO.Path]::GetTempPath()) "eaw-favicon"
New-Item -ItemType Directory -Force -Path $work | Out-Null

$S = 512
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

# One stroke weight throughout, scaled from the canvas: the reference reads as a single
# hand-drawn line width everywhere.
$w = [int]($S * 0.036)
$pen = New-Object System.Drawing.Pen($ink, $w)
$pen.LineJoin = 'Round'
$pen.StartCap = 'Round'
$pen.EndCap   = 'Round'

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

# --- card -------------------------------------------------------------------
$m = $S * 0.075
$card = Get-RoundedPath $m ($m * 1.35) ($S - 2 * $m) ($S - 2.7 * $m) ($S * 0.085)
$g.FillPath($bTeal, $card)
$g.DrawPath($pen, $card)

# --- pie: blue disc, orange wedge in the upper right ------------------------
$pr = $S * 0.135
$pcx = $S * 0.335
$pcy = $S * 0.415
$px = $pcx - $pr
$py = $pcy - $pr
$pd = $pr * 2

$g.FillEllipse($bBlue, $px, $py, $pd, $pd)
$g.FillPie($bOrange, $px, $py, $pd, $pd, -90, 100)
$g.DrawEllipse($pen, $px, $py, $pd, $pd)
# The wedge's own outline, so it reads as a slice rather than a colour patch.
$g.DrawPie($pen, $px, $py, $pd, $pd, -90, 100)

# --- three stacked pills on the right ---------------------------------------
$plx = $S * 0.575
$plw = $S * 0.245
$plh = $S * 0.082
$gap = $S * 0.052
$ply = $S * 0.275
$fills = @($bOrange, $bBlue, $bBlue)

for ($i = 0; $i -lt 3; $i++) {
    $y = $ply + $i * ($plh + $gap)
    $pill = Get-RoundedPath $plx $y $plw $plh ($plh / 2)
    $g.FillPath($fills[$i], $pill)
    $g.DrawPath($pen, $pill)
    $pill.Dispose()
}

# --- slider across the bottom: orange left, blue right, knob between --------
$sy = $S * 0.665
$sh = $S * 0.078
$sx1 = $S * 0.135
$sx2 = $S * 0.845
$knobX = $S * 0.475

$left = Get-RoundedPath $sx1 $sy ($knobX - $sx1) $sh ($sh / 2)
$g.FillPath($bOrange, $left)
$g.DrawPath($pen, $left)

$right = Get-RoundedPath $knobX $sy ($sx2 - $knobX) $sh ($sh / 2)
$g.FillPath($bBlue, $right)
$g.DrawPath($pen, $right)

$kr = $sh * 0.78
$g.FillEllipse($bBlue, ($knobX - $kr), ($sy + $sh / 2 - $kr), ($kr * 2), ($kr * 2))
$g.DrawEllipse($pen, ($knobX - $kr), ($sy + $sh / 2 - $kr), ($kr * 2), ($kr * 2))

$left.Dispose(); $right.Dispose(); $card.Dispose()

$master = "$work\favicon-master.png"
$bmp.Save($master, [System.Drawing.Imaging.ImageFormat]::Png)

# Downscale from the 512 master rather than redrawing: strokes stay proportional.
foreach ($size in 256, 180, 64, 32, 16) {
    $out = New-Object System.Drawing.Bitmap($size, $size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $og = [System.Drawing.Graphics]::FromImage($out)
    $og.InterpolationMode = 'HighQualityBicubic'
    $og.PixelOffsetMode = 'HighQuality'
    $og.SmoothingMode = 'AntiAlias'
    $og.Clear([System.Drawing.Color]::Transparent)
    $og.DrawImage($bmp, 0, 0, $size, $size)
    $og.Dispose()
    $out.Save("$work\favicon-$size.png", [System.Drawing.Imaging.ImageFormat]::Png)
    $out.Dispose()
}

$g.Dispose(); $bmp.Dispose()
Write-Output "wrote master + 256/180/64/32/16"
