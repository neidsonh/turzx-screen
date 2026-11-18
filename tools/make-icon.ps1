# Gera app.ico (16..256 px, entradas PNG) desenhando o icone com System.Drawing
param([string]$Out = "$PSScriptRoot\..\app.ico")
Add-Type -AssemblyName System.Drawing

function RoundPath([single]$x, [single]$y, [single]$w, [single]$h, [single]$r) {
  $p = New-Object Drawing.Drawing2D.GraphicsPath; $d = $r * 2
  $p.AddArc($x, $y, $d, $d, 180, 90); $p.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
  $p.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90); $p.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
  $p.CloseFigure(); $p
}

function Draw([int]$s) {
  $b = New-Object Drawing.Bitmap $s, $s, ([Drawing.Imaging.PixelFormat]::Format32bppArgb)
  $g = [Drawing.Graphics]::FromImage($b)
  $g.SmoothingMode = 'AntiAlias'; $g.PixelOffsetMode = 'HighQuality'
  $cyan = [Drawing.Color]::FromArgb(0, 200, 255)
  $f = $s / 256.0

  # corpo da telinha (moldura escura com degrade)
  $m = [Math]::Max(1, 10 * $f)
  $body = RoundPath $m ($m + 18 * $f) ($s - 2 * $m) ($s - 2 * $m - 36 * $f) (34 * $f)
  $rect = New-Object Drawing.RectangleF 0, 0, $s, $s
  $gb = New-Object Drawing.Drawing2D.LinearGradientBrush $rect, ([Drawing.Color]::FromArgb(46, 54, 78)), ([Drawing.Color]::FromArgb(14, 16, 26)), 90
  $g.FillPath($gb, $body)

  # area da tela
  $sx = $m + 18 * $f; $sy = $m + 36 * $f; $sw = $s - 2 * $sx; $sh = $s - 2 * $m - 36 * $f - 36 * $f
  $scr = RoundPath $sx $sy $sw $sh (18 * $f)
  $sb = New-Object Drawing.Drawing2D.LinearGradientBrush $rect, ([Drawing.Color]::FromArgb(10, 40, 70)), ([Drawing.Color]::FromArgb(4, 10, 20)), 90
  $g.FillPath($sb, $scr)

  # barras de grafico subindo
  $n = if ($s -le 24) { 3 } else { 4 }
  $heights = if ($n -eq 3) { 0.35, 0.6, 0.9 } else { 0.3, 0.55, 0.42, 0.85 }
  $pad = 16 * $f; $gap = [Math]::Max(1, 10 * $f)
  $bw = ($sw - 2 * $pad - ($n - 1) * $gap) / $n
  $base = $sy + $sh - $pad
  $cb = New-Object Drawing.SolidBrush $cyan
  for ($i = 0; $i -lt $n; $i++) {
    $bh = ($sh - 2 * $pad) * $heights[$i]
    $bx = $sx + $pad + $i * ($bw + $gap)
    if ($s -ge 48) { $p = RoundPath $bx ($base - $bh) $bw $bh ([Math]::Min($bw / 2, 6 * $f)); $g.FillPath($cb, $p) }
    else { $g.FillRectangle($cb, [single]$bx, [single]($base - $bh), [single]$bw, [single]$bh) }
  }
  # pontinho "USB" embaixo
  if ($s -ge 32) {
    $dr = 5 * $f; $dc = New-Object Drawing.SolidBrush ([Drawing.Color]::FromArgb(0, 200, 255))
    $g.FillEllipse($dc, [single]($s / 2 - $dr), [single]($s - $m - 27 * $f - $dr), [single](2 * $dr), [single](2 * $dr))
  }
  $g.Dispose(); $b
}

$sizes = 16, 20, 24, 32, 40, 48, 64, 128, 256
$pngs = foreach ($s in $sizes) { $bmp = Draw $s; $ms = New-Object IO.MemoryStream; $bmp.Save($ms, [Drawing.Imaging.ImageFormat]::Png); $bmp.Dispose(); , $ms.ToArray() }

$fs = [IO.File]::Create((Resolve-Path -LiteralPath (Split-Path $Out) ).Path + '\' + (Split-Path $Out -Leaf))
$w = New-Object IO.BinaryWriter $fs
$w.Write([UInt16]0); $w.Write([UInt16]1); $w.Write([UInt16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
  $s = $sizes[$i]; $len = $pngs[$i].Length
  $w.Write([byte]($s % 256)); $w.Write([byte]($s % 256)); $w.Write([byte]0); $w.Write([byte]0)
  $w.Write([UInt16]1); $w.Write([UInt16]32); $w.Write([UInt32]$len); $w.Write([UInt32]$offset)
  $offset += $len
}
foreach ($p in $pngs) { $w.Write($p) }
$w.Close()
# preview em 256 para conferencia
$prev = Draw 256; $prev.Save("$PSScriptRoot\..\icon-preview.png"); $prev.Dispose()
"ok: $Out"
