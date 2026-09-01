param([string]$OutPath)
Add-Type -AssemblyName System.Drawing
$size = 256
$bmp = New-Object System.Drawing.Bitmap($size, $size)
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.SmoothingMode = 'AntiAlias'
$g.Clear([System.Drawing.Color]::Transparent)

function New-RoundRect([int]$x, [int]$y, [int]$w, [int]$h, [int]$r) {
  $p = New-Object System.Drawing.Drawing2D.GraphicsPath
  $d = $r * 2
  $p.AddArc($x, $y, $d, $d, 180, 90)
  $p.AddArc(($x + $w - $d), $y, $d, $d, 270, 90)
  $p.AddArc(($x + $w - $d), ($y + $h - $d), $d, $d, 0, 90)
  $p.AddArc($x, ($y + $h - $d), $d, $d, 90, 90)
  $p.CloseFigure()
  return $p
}

# plate
$plate = New-RoundRect 8 8 240 240 46
$bg = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
  (New-Object System.Drawing.Point(8, 8)),
  (New-Object System.Drawing.Point(248, 248)),
  [System.Drawing.Color]::FromArgb(255, 34, 38, 46),
  [System.Drawing.Color]::FromArgb(255, 20, 22, 28))
$g.FillPath($bg, $plate)
$edge = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(90, 255, 255, 255), 3)
$g.DrawPath($edge, $plate)

# vault dial ring
$accent = [System.Drawing.Color]::FromArgb(255, 229, 72, 77)
$ring = New-Object System.Drawing.Pen($accent, 14)
$ring.StartCap = 'Round'
$ring.EndCap = 'Round'
$g.DrawArc($ring, 62, 62, 132, 132, 128, 284)

# spokes
$spoke = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(255, 236, 238, 244), 12)
$spoke.StartCap = 'Round'
$spoke.EndCap = 'Round'
$cx = 128.0
$cy = 128.0
foreach ($a in 0, 60, 120) {
  $rad = $a * [Math]::PI / 180.0
  $dx = [Math]::Cos($rad) * 44.0
  $dy = [Math]::Sin($rad) * 44.0
  $g.DrawLine($spoke, ($cx - $dx), ($cy - $dy), ($cx + $dx), ($cy + $dy))
}

# hub
$hub = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(255, 20, 22, 28))
$g.FillEllipse($hub, 100, 100, 56, 56)
$hub2 = New-Object System.Drawing.SolidBrush($accent)
$g.FillEllipse($hub2, 112, 112, 32, 32)

$g.Dispose()
$bmp.Save($OutPath, [System.Drawing.Imaging.ImageFormat]::Png)
$bmp.Dispose()
Write-Output ("saved " + $OutPath)
