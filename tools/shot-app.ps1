# tools/shot-app.ps1 — screenshot the LAUNCHER's own window, read-only.
#
# Renders the window through PrintWindow rather than copying screen pixels.
# That matters: a screen copy takes whatever happens to be on top at that
# rectangle, so it silently produces a picture of the desktop when another window
# overlaps — and the obvious fix, activating the window, is exactly the kind of
# window manipulation this project has been burned by. PrintWindow asks the window
# to draw itself into a bitmap and needs neither activation nor z-order.
# OpenChamber is never targeted, moved, raised or closed.
[CmdletBinding()]
param(
    [string]$Label = 'glass',
    [switch]$Projects,
    [int]$WaitSeconds = 12
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes

Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class WinCapture {
    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr hwnd, IntPtr hdc, uint flags);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
    // PW_RENDERFULLCONTENT (2) is what makes PrintWindow work for composited/WPF
    // windows; without it the window comes back black.
    public const uint PW_RENDERFULLCONTENT = 0x00000002;
}
'@

$exe = '<repo>\apps\SessionLauncher\src\SessionLauncher.App\bin\Release\net10.0-windows\SessionLauncher.exe'
$out = "<repo>\apps\SessionLauncher\artifacts\$Label.png"
New-Item -ItemType Directory -Force -Path (Split-Path $out) | Out-Null

Get-Process SessionLauncher -ErrorAction SilentlyContinue |
    ForEach-Object { $_.Kill(); $_.WaitForExit(5000) } | Out-Null

$p = Start-Process $exe -PassThru
$root = $null
$deadline = (Get-Date).AddSeconds($WaitSeconds)
while ((Get-Date) -lt $deadline) {
    Start-Sleep -Milliseconds 700
    $p.Refresh()
    if ($p.HasExited) { throw "launcher exited, code $($p.ExitCode)" }
    $c = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ProcessIdProperty, $p.Id)
    $root = [System.Windows.Automation.AutomationElement]::RootElement.FindFirst(
        [System.Windows.Automation.TreeScope]::Children, $c)
    if ($root) { break }
}
if (-not $root) { throw "no launcher window" }

if ($Projects) {
    $cond = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
        [System.Windows.Automation.ControlType]::Text)
    $walker = [System.Windows.Automation.TreeWalker]::ControlViewWalker
    foreach ($t in $root.FindAll([System.Windows.Automation.TreeScope]::Descendants, $cond)) {
        if ($t.Current.Name -ne '项目') { continue }
        $node = $t
        for ($i = 0; $i -lt 8 -and $node; $i++) {
            try { $node.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke(); break }
            catch { $node = $walker.GetParent($node) }
        }
        break
    }
    Start-Sleep -Milliseconds 1500
}

$p.Refresh()
$hwnd = $p.MainWindowHandle
if ($hwnd -eq [IntPtr]::Zero) { throw "the launcher window has no handle" }

$rect = New-Object WinCapture+RECT
if (-not [WinCapture]::GetWindowRect($hwnd, [ref]$rect)) { throw "GetWindowRect failed" }
$w = $rect.Right - $rect.Left
$h = $rect.Bottom - $rect.Top

$bmp = New-Object System.Drawing.Bitmap($w, $h)
$g = [System.Drawing.Graphics]::FromImage($bmp)
$hdc = $g.GetHdc()
try {
    if (-not [WinCapture]::PrintWindow($hwnd, $hdc, [WinCapture]::PW_RENDERFULLCONTENT)) {
        throw "PrintWindow failed"
    }
}
finally {
    $g.ReleaseHdc($hdc)
    $g.Dispose()
}
$bmp.Save($out, [System.Drawing.Imaging.ImageFormat]::Png)
$bmp.Dispose()

Write-Host "saved $out  (${w}x${h})"
$p.CloseMainWindow() | Out-Null
$p.WaitForExit(8000) | Out-Null