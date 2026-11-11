param([string]$Out)
Add-Type -AssemblyName System.Drawing

# Claude orange tile with two white usage bars (session, week), drawn as shapes so it
# stays crisp down to 16px. Same writer as wsl-tray/tools/make-icon.ps1. PNG payloads inside the .ico (supported since Vista).
$sizes = 256, 64, 48, 32, 24, 16
$blobs = @()
foreach ($s in $sizes) {
  $bmp = New-Object Drawing.Bitmap $s, $s, ([Drawing.Imaging.PixelFormat]::Format32bppArgb)
  $g = [Drawing.Graphics]::FromImage($bmp)
  $g.SmoothingMode = [Drawing.Drawing2D.SmoothingMode]::AntiAlias
  $g.Clear([Drawing.Color]::Transparent)

  $r = [Math]::Max(3, [int]($s * 0.22))
  $d = $r * 2
  $w = $s - 1
  $path = New-Object Drawing.Drawing2D.GraphicsPath
  $path.AddArc(0, 0, $d, $d, 180, 90)
  $path.AddArc($w - $d, 0, $d, $d, 270, 90)
  $path.AddArc($w - $d, $w - $d, $d, $d, 0, 90)
  $path.AddArc(0, $w - $d, $d, $d, 90, 90)
  $path.CloseFigure()
  $brush = New-Object Drawing.SolidBrush ([Drawing.Color]::FromArgb(0xD9, 0x77, 0x57))
  $g.FillPath($brush, $path)

  $pw = [Math]::Max(2.5, $s * 0.13)
  $pen = New-Object Drawing.Pen ([Drawing.Color]::White), $pw
  $pen.StartCap = [Drawing.Drawing2D.LineCap]::Round
  $pen.EndCap = [Drawing.Drawing2D.LineCap]::Round
  $g.DrawLine($pen, ($s * 0.25), ($s * 0.37), ($s * 0.50), ($s * 0.37))
  $g.DrawLine($pen, ($s * 0.25), ($s * 0.63), ($s * 0.75), ($s * 0.63))

  $ms = New-Object IO.MemoryStream
  $bmp.Save($ms, [Drawing.Imaging.ImageFormat]::Png)
  $blobs += ,($ms.ToArray())
  $g.Dispose(); $bmp.Dispose(); $pen.Dispose(); $brush.Dispose(); $path.Dispose()
}

$fs = [IO.File]::Create($Out)
$bw = New-Object IO.BinaryWriter $fs
$bw.Write([UInt16]0); $bw.Write([UInt16]1); $bw.Write([UInt16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
  $s = $sizes[$i]
  $dim = if ($s -ge 256) { 0 } else { $s }
  $bw.Write([byte]$dim); $bw.Write([byte]$dim); $bw.Write([byte]0); $bw.Write([byte]0)
  $bw.Write([UInt16]1); $bw.Write([UInt16]32)
  $bw.Write([UInt32]$blobs[$i].Length); $bw.Write([UInt32]$offset)
  $offset += $blobs[$i].Length
}
foreach ($b in $blobs) { $bw.Write($b) }
$bw.Close()
Write-Output "wrote $Out ($((Get-Item $Out).Length) bytes, sizes $($sizes -join ','))"
