<#
    shot.ps1 - capture a top-level window's own content to a PNG.

    Uses PrintWindow with PW_RENDERFULLCONTENT rather than a screen grab, so the
    capture is unaffected by whatever is stacked on top of the window or by
    focus. Handy when another app (or the Start menu) keeps stealing the
    foreground.

    Usage:
        powershell -ExecutionPolicy Bypass -File .\tools\shot.ps1 `
            -Process SessionLauncher -Out C:\temp\app.png
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string] $Process,
    [Parameter(Mandatory = $true)][string] $Out
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

if (-not ('WinCapture' -as [type])) {
    Add-Type @"
using System;
using System.Runtime.InteropServices;
public class WinCapture {
    [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h, IntPtr hdc, uint flags);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT rect);
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
}
"@
}

$proc = Get-Process $Process -ErrorAction Stop | Select-Object -First 1
if ($proc.MainWindowHandle -eq [IntPtr]::Zero) { throw "$Process has no main window" }

$rect = New-Object WinCapture+RECT
[WinCapture]::GetWindowRect($proc.MainWindowHandle, [ref]$rect) | Out-Null

$width  = $rect.Right - $rect.Left
$height = $rect.Bottom - $rect.Top
if ($width -le 0 -or $height -le 0) { throw "window has no area: ${width}x${height}" }

$bitmap = New-Object System.Drawing.Bitmap($width, $height, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
$graphics = [System.Drawing.Graphics]::FromImage($bitmap)
try {
    $hdc = $graphics.GetHdc()
    try { [WinCapture]::PrintWindow($proc.MainWindowHandle, $hdc, 2) | Out-Null }
    finally { $graphics.ReleaseHdc($hdc) }

    $dir = Split-Path -Parent $Out
    if ($dir -and -not (Test-Path $dir)) { New-Item -ItemType Directory -Force -Path $dir | Out-Null }
    $bitmap.Save($Out, [System.Drawing.Imaging.ImageFormat]::Png)
}
finally {
    $graphics.Dispose()
    $bitmap.Dispose()
}

Write-Host "captured ${width}x${height} -> $Out"
