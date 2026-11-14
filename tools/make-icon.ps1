# Generates app.ico (glassy blue squircle with a white music note) in several sizes.
# Run from apps/spotify-widget:  powershell -ExecutionPolicy Bypass -File tools\make-icon.ps1
Add-Type -AssemblyName System.Drawing
$out = Join-Path $PSScriptRoot "..\app.ico"
$sizes = 16, 20, 24, 32, 40, 48, 64, 256

function Draw-Icon([int]$s) {
    $bmp = New-Object System.Drawing.Bitmap $s, $s, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'; $g.PixelOffsetMode = 'HighQuality'; $g.TextRenderingHint = 'AntiAliasGridFit'
    $inset = [Math]::Max(0.5, $s * 0.04)
    $r = $s * 0.27; $x = $inset; $y = $inset; $w = $s - 2 * $inset; $h = $w
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $path.AddArc($x, $y, 2*$r, 2*$r, 180, 90); $path.AddArc($x+$w-2*$r, $y, 2*$r, 2*$r, 270, 90)
    $path.AddArc($x+$w-2*$r, $y+$h-2*$r, 2*$r, 2*$r, 0, 90); $path.AddArc($x, $y+$h-2*$r, 2*$r, 2*$r, 90, 90); $path.CloseFigure()

    $body = New-Object System.Drawing.Drawing2D.LinearGradientBrush (New-Object System.Drawing.PointF 0,0), (New-Object System.Drawing.PointF $s,$s), ([System.Drawing.Color]::Black), ([System.Drawing.Color]::Black)
    $blend = New-Object System.Drawing.Drawing2D.ColorBlend 3
    $blend.Colors = @([System.Drawing.Color]::FromArgb(255,120,196,255), [System.Drawing.Color]::FromArgb(255,52,104,246), [System.Drawing.Color]::FromArgb(255,104,58,236))
    $blend.Positions = @(0.0, 0.55, 1.0); $body.InterpolationColors = $blend
    $g.FillPath($body, $path)

    # glossy cap
    $g.SetClip($path)
    $gloss = New-Object System.Drawing.Drawing2D.LinearGradientBrush (New-Object System.Drawing.PointF 0,0), (New-Object System.Drawing.PointF 0,($s*0.55)), ([System.Drawing.Color]::FromArgb(110,255,255,255)), ([System.Drawing.Color]::FromArgb(0,255,255,255))
    $g.FillEllipse($gloss, -$s*0.25, -$s*0.55, $s*1.5, $s*1.05)
    $g.ResetClip()

    # glass rim
    if ($s -ge 24) { $pen = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(130,255,255,255)), ([Math]::Max(1, $s/48)); $g.DrawPath($pen, $path) }

    # music note
    $font = New-Object System.Drawing.Font "Segoe Fluent Icons", ($s * 0.5), ([System.Drawing.FontStyle]::Regular), ([System.Drawing.GraphicsUnit]::Pixel)
    $fmt = New-Object System.Drawing.StringFormat; $fmt.Alignment = 'Center'; $fmt.LineAlignment = 'Center'
    $rect = New-Object System.Drawing.RectangleF 0, ($s*0.02), $s, $s
    if ($s -ge 32) { $g.DrawString([string][char]0xE8D6, $font, (New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(70,0,0,60))), (New-Object System.Drawing.RectangleF ($s*0.015), ($s*0.05), $s, $s), $fmt) }
    $g.DrawString([string][char]0xE8D6, $font, [System.Drawing.Brushes]::White, $rect, $fmt)
    $g.Dispose()
    return $bmp
}

$pngs = foreach ($s in $sizes) { $b = Draw-Icon $s; $ms = New-Object System.IO.MemoryStream; $b.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png); if ($s -eq 256) { $b.Save((Join-Path $PSScriptRoot "icon-preview.png")) }; ,$ms.ToArray() }

$fs = [System.IO.File]::Create($out); $bw = New-Object System.IO.BinaryWriter $fs
$bw.Write([UInt16]0); $bw.Write([UInt16]1); $bw.Write([UInt16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $s = $sizes[$i]; $d = $pngs[$i]
    $bw.Write([byte]($(if ($s -ge 256) { 0 } else { $s }))); $bw.Write([byte]($(if ($s -ge 256) { 0 } else { $s })))
    $bw.Write([byte]0); $bw.Write([byte]0); $bw.Write([UInt16]1); $bw.Write([UInt16]32)
    $bw.Write([UInt32]$d.Length); $bw.Write([UInt32]$offset); $offset += $d.Length
}
foreach ($d in $pngs) { $bw.Write($d) }
$bw.Close(); "wrote $out"
