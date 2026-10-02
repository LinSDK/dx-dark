# Generates src\DxDark.App\Assets\DxDark.ico: a dark screen with a glowing ambient-light ring.
# Run with Windows PowerShell:  powershell -ExecutionPolicy Bypass -File tools\make-icon.ps1
param([string]$OutFile = (Join-Path $PSScriptRoot '..\src\DxDark.App\Assets\DxDark.ico'))

Add-Type -AssemblyName System.Drawing

function New-RoundedPath([double]$x, [double]$y, [double]$w, [double]$h, [double]$r) {
    $p = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = [Math]::Min(2 * $r, [Math]::Min($w, $h))
    $p.AddArc([single]$x, [single]$y, [single]$d, [single]$d, 180, 90)
    $p.AddArc([single]($x + $w - $d), [single]$y, [single]$d, [single]$d, 270, 90)
    $p.AddArc([single]($x + $w - $d), [single]($y + $h - $d), [single]$d, [single]$d, 0, 90)
    $p.AddArc([single]$x, [single]($y + $h - $d), [single]$d, [single]$d, 90, 90)
    $p.CloseFigure()
    return $p
}

function New-RingBrush([double]$s, [int]$alpha) {
    $rect = New-Object System.Drawing.RectangleF 0, 0, ([single]$s), ([single]$s)
    $brush = New-Object System.Drawing.Drawing2D.LinearGradientBrush $rect, ([System.Drawing.Color]::Black), ([System.Drawing.Color]::Black), ([single]45)
    $blend = New-Object System.Drawing.Drawing2D.ColorBlend 3
    $blend.Colors = @(
        [System.Drawing.Color]::FromArgb($alpha, 0x34, 0xD2, 0xF2),
        [System.Drawing.Color]::FromArgb($alpha, 0x8B, 0x6C, 0xFF),
        [System.Drawing.Color]::FromArgb($alpha, 0xFF, 0x4F, 0xA3))
    $blend.Positions = @([single]0, [single]0.5, [single]1)
    $brush.InterpolationColors = $blend
    return $brush
}

function New-IconFrame([int]$size) {
    $s = [double]$size
    $bmp = New-Object System.Drawing.Bitmap $size, $size, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.Clear([System.Drawing.Color]::Transparent)

    # Rounded dark tile.
    $tile = New-RoundedPath (0.03 * $s) (0.03 * $s) (0.94 * $s) (0.94 * $s) (0.22 * $s)
    $tileBrush = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 0x14, 0x16, 0x1C))
    $g.FillPath($tileBrush, $tile)

    # Glowing ring (soft halo layers on larger sizes, then the core stroke).
    $inset = 0.2 * $s
    $ring = New-RoundedPath $inset $inset ($s - 2 * $inset) ($s - 2 * $inset) (0.1 * $s)
    $core = [Math]::Max(1.5, 0.075 * $s)
    if ($size -ge 32) {
        $layers = @(
            @{ W = 3.8; A = 10 }, @{ W = 3.3; A = 14 }, @{ W = 2.9; A = 20 }, @{ W = 2.5; A = 28 },
            @{ W = 2.1; A = 40 }, @{ W = 1.75; A = 60 }, @{ W = 1.45; A = 95 }, @{ W = 1.2; A = 140 })
        foreach ($layer in $layers) {
            $pen = New-Object System.Drawing.Pen ((New-RingBrush $s $layer.A)), ([single]($core * $layer.W))
            $pen.LineJoin = [System.Drawing.Drawing2D.LineJoin]::Round
            $g.DrawPath($pen, $ring)
        }
    }
    $corePen = New-Object System.Drawing.Pen ((New-RingBrush $s 255)), ([single]$core)
    $corePen.LineJoin = [System.Drawing.Drawing2D.LineJoin]::Round
    $g.DrawPath($corePen, $ring)

    # The "screen" inside the light.
    $si = $inset + $core * 0.9
    $screen = New-RoundedPath $si $si ($s - 2 * $si) ($s - 2 * $si) (0.06 * $s)
    $screenBrush = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 0x07, 0x08, 0x0B))
    $g.FillPath($screenBrush, $screen)

    $g.Dispose()
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    return , $ms.ToArray()
}

$sizes = 16, 20, 24, 32, 40, 48, 64, 128, 256
$frames = @($sizes | ForEach-Object { , (New-IconFrame $_) })

# ICO container with PNG-compressed entries.
$out = New-Object System.IO.MemoryStream
$w = New-Object System.IO.BinaryWriter $out
$w.Write([uint16]0); $w.Write([uint16]1); $w.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $dim = if ($sizes[$i] -ge 256) { 0 } else { $sizes[$i] }
    $w.Write([byte]$dim); $w.Write([byte]$dim); $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([uint16]1); $w.Write([uint16]32)
    $w.Write([uint32]$frames[$i].Length); $w.Write([uint32]$offset)
    $offset += $frames[$i].Length
}
foreach ($f in $frames) { $w.Write($f) }
$w.Flush()

$dir = Split-Path -Parent $OutFile
if (-not (Test-Path $dir)) { New-Item -ItemType Directory -Path $dir | Out-Null }
[System.IO.File]::WriteAllBytes($OutFile, $out.ToArray())
"Wrote $OutFile ($($out.Length) bytes)"

# Also write a 256 px PNG for documentation.
[System.IO.File]::WriteAllBytes((Join-Path $dir 'DxDark-256.png'), $frames[-1])
