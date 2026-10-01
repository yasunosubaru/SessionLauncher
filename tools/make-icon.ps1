<#
    make-icon.ps1 - generate the SessionLauncher multi-resolution .ico.

    Renders each size natively (no upscaling) and packs the PNG payloads into a
    Vista-style ICO container, which is what Windows expects for 16..256 px.

    Usage:
        powershell -ExecutionPolicy Bypass -File .\tools\make-icon.ps1
#>
[CmdletBinding()]
param(
    # $PSScriptRoot is not yet bound while param defaults are evaluated in
    # Windows PowerShell 5.1, so the default is resolved in the body below.
    [string] $OutputPath = ''
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

if ([string]::IsNullOrWhiteSpace($OutputPath)) {
    $OutputPath = Join-Path (Split-Path -Parent $MyInvocation.MyCommand.Path) `
                             '..\src\SessionLauncher.App\Assets\SessionLauncher.ico'
}

# Sizes Windows asks a taskbar / Explorer icon for.
$sizes = @(16, 20, 24, 32, 40, 48, 64, 128, 256)

function New-RoundedRectPath([double]$x, [double]$y, [double]$w, [double]$h, [double]$r) {
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = [float]([Math]::Min($r * 2, [Math]::Min($w, $h)))
    $x = [float]$x; $y = [float]$y
    $right = [float]($x + $w - $d)
    $bottom = [float]($y + $h - $d)

    $path.AddArc([System.Drawing.RectangleF]::new($x, $y, $d, $d), 180, 90)
    $path.AddArc([System.Drawing.RectangleF]::new($right, $y, $d, $d), 270, 90)
    $path.AddArc([System.Drawing.RectangleF]::new($right, $bottom, $d, $d), 0, 90)
    $path.AddArc([System.Drawing.RectangleF]::new($x, $bottom, $d, $d), 90, 90)
    $path.CloseFigure()
    return $path
}

function New-RoundedBar([double]$x, [double]$y, [double]$w, [double]$h, [double]$r) {
    return New-RoundedRectPath $x $y $w $h $r
}

function Render-Icon([int]$size) {
    $bmp = New-Object System.Drawing.Bitmap($size, $size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    try {
        $g.SmoothingMode     = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
        $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
        $g.PixelOffsetMode   = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
        $g.Clear([System.Drawing.Color]::Transparent)

        $inset    = [Math]::Max(1, [Math]::Round($size * 0.045))
        $box      = $size - 2 * $inset
        $radius   = $box * 0.235

        # Badge: rounded square, indigo -> violet, with a soft top highlight.
        $badge = New-RoundedRectPath $inset $inset $box $box $radius
        $grad = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
            (New-Object System.Drawing.Point(0, $inset)),
            (New-Object System.Drawing.Point(0, ($inset + $box))),
            [System.Drawing.Color]::FromArgb(255, 96, 132, 255),
            [System.Drawing.Color]::FromArgb(255, 108, 74, 255))
        $g.FillPath($grad, $badge)

        $rimPen = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(70, 255, 255, 255), [Math]::Max(1, $size * 0.028))
        $g.DrawPath($rimPen, $badge)
        $rimPen.Dispose()
        $grad.Dispose()
        $badge.Dispose()

        # Play triangle, nudged right of centre to leave room for transcript bars.
        $unit  = $box / 24.0
        $shift = if ($size -ge 32) { 1.6 * $unit } else { 0.0 }
        $triW  = if ($size -ge 32) { 9.0 * $unit } else { 11.0 * $unit }
        $triH  = if ($size -ge 32) { 10.0 * $unit } else { 12.5 * $unit }
        $cx    = $inset + $box / 2 + $shift
        $cy    = $inset + $box / 2 - ($triH / 6)

        $tri = New-Object System.Drawing.Drawing2D.GraphicsPath
        $tri.AddPolygon(@(
            (New-Object System.Drawing.PointF([float]$cx, [float]($cy - $triH / 2))),
            (New-Object System.Drawing.PointF([float]($cx + $triW), [float]$cy)),
            (New-Object System.Drawing.PointF([float]$cx, [float]($cy + $triH / 2)))
        ))
        $tri.CloseFigure()
        $g.FillPath((New-Object System.Drawing.SolidBrush([System.Drawing.Color]::White)), $tri)
        $tri.Dispose()

        # Transcript bars on the left, mirroring the list this app shows.
        if ($size -ge 32) {
            $barX    = $inset + $box * 0.17
            $barW    = $box * 0.20
            $barH    = [Math]::Max(1.5, $box * 0.062)
            $barGap  = $box * 0.115
            $barTop  = $cy - $barGap
            $barBrush = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(225, 255, 255, 255))
            foreach ($i in 0, 1) {
                $b = New-RoundedBar $barX ($barTop + $i * $barGap) $barW $barH ($barH / 2)
                $g.FillPath($barBrush, $b)
                $b.Dispose()
            }
            $barBrush.Dispose()
        }

        $ms = New-Object System.IO.MemoryStream
        $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
        # The leading comma is load-bearing: PowerShell unrolls a byte[] into
        # individual bytes as it leaves the function, which would silently
        # produce a 1-byte icon entry.
        return ,$ms.ToArray()
    }
    finally {
        $g.Dispose()
        $bmp.Dispose()
    }
}

# ---- pack the PNGs into one ICO -------------------------------------------------
$images = foreach ($s in $sizes) { Render-Icon $s }

$outDir = Split-Path -Parent $OutputPath
New-Item -ItemType Directory -Force -Path $outDir | Out-Null

$ms = New-Object System.IO.MemoryStream
$bw = New-Object System.IO.BinaryWriter($ms)

# ICONDIR
$bw.Write([UInt16]0)                    # reserved
$bw.Write([UInt16]1)                    # type: icon
$bw.Write([UInt16]$sizes.Count)

$offset = 6 + (16 * $sizes.Count)       # header + one ICONDIRENTRY per image
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $size = $sizes[$i]
    $dim  = if ($size -ge 256) { 0 } else { $size }   # 0 means 256
    $bw.Write([Byte]$dim)               # width
    $bw.Write([Byte]$dim)               # height
    $bw.Write([Byte]0)                  # palette size (0 = truecolour)
    $bw.Write([Byte]0)                  # reserved
    $bw.Write([UInt16]1)                # colour planes
    $bw.Write([UInt16]32)               # bits per pixel
    $bw.Write([UInt32]$images[$i].Length)
    $bw.Write([UInt32]$offset)
    $offset += $images[$i].Length
}
foreach ($img in $images) { $bw.Write($img) }

$bw.Flush()
[System.IO.File]::WriteAllBytes($OutputPath, $ms.ToArray())
$bw.Dispose(); $ms.Dispose()

Write-Host ("wrote {0}  ({1} sizes, {2} KB)" -f $OutputPath, $sizes.Count,
    [Math]::Round((Get-Item $OutputPath).Length / 1KB, 1))
