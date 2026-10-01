<#
    shot-screen.ps1 - capture the REAL composited screen, not PrintWindow.

    Why this exists: PrintWindow (tools\shot.ps1) cannot see mouse-over state.
    Verified: in a scratch WPF window, capturing with the pointer over a row and
    with the pointer off it produced byte-identical PNGs. A template trigger on
    IsMouseOver is live input state, and a PrintWindow render does not include it.
    So any hover "verification" done with shot.ps1 is measuring nothing.

    This grabs the actual screen instead, so it shows what the user really sees,
    including the pointer. Sample away from the pointer when measuring a fill.

    Usage:
        powershell -ExecutionPolicy Bypass -File .\tools\shot-screen.ps1 `
            -Process SessionLauncher -Out .\screen.png
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string] $Process,
    [Parameter(Mandatory = $true)][string] $Out,

    # Optional placement. Omit to capture the window where it already is, at its
    # current size. Supply these to move and resize first, which is usually what you
    # want so that a row you are about to hover is not scrolled out of view.
    [int] $X = -1,
    [int] $Y = -1,
    [int] $Width = 0,
    [int] $Height = 0
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName System.Windows.Forms

$p = Get-Process $Process -ErrorAction Stop | Select-Object -First 1
if (-not $p.MainWindowHandle) { throw "$Process has no main window; is it minimised?" }

# Bounds of the window on the virtual screen.
Add-Type @"
using System;
using System.Runtime.InteropServices;
public struct RECT2 { public int Left, Top, Right, Bottom; }
public class Win32Shot {
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT2 r);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int n);

    // SetForegroundWindow is refused whenever the caller is not the foreground
    // process, so a test harness cannot bring its own window up this way. Making the
    // window TOPMOST sidesteps that: hit-testing reaches it, so IsMouseOver becomes
    // live, which is the whole reason to prefer a screen grab over PrintWindow.
    // Keyboard focus is NOT required for a mouse-over trigger.
    [DllImport("user32.dll")] public static extern bool SetWindowPos(
        IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);

    public const int  SW_RESTORE  = 9;
    public const int  SW_HIDE     = 0;
    public static readonly IntPtr TOPMOST    = new IntPtr(-1);
    public static readonly IntPtr NOTOPMOST = new IntPtr(-2);

    public static void MakeTopmost(IntPtr h, int x, int y, int cx, int cy) {
        // 0x0040 = SWP_SHOWWINDOW
        SetWindowPos(h, TOPMOST, x, y, cx, cy, 0x0040);
    }
    public static void ClearTopmost(IntPtr h) {
        SetWindowPos(h, NOTOPMOST, 0, 0, 0, 0, 0x0003);  // SWP_NOSIZE|SWP_NOMOVE|SWP_NOACTIVATE
    }
}
"@

# Where is it now? If -X/-Y were not given, reuse the current spot so the pointer
# position the caller already chose still lands on the same row.
$here = New-Object RECT2
[Win32Shot]::GetWindowRect($p.MainWindowHandle, [ref] $here) | Out-Null

$px = if ($X -ge 0) { $X } else { $here.Left }
$py = if ($Y -ge 0) { $Y } else { $here.Top }
$pw = if ($Width  -gt 0) { $Width }  else { $here.Right - $here.Left }
$ph = if ($Height -gt 0) { $Height } else { $here.Bottom - $here.Top }

[Win32Shot]::ShowWindow($p.MainWindowHandle, 9) | Out-Null   # SW_RESTORE
[Win32Shot]::MakeTopmost($p.MainWindowHandle, $px, $py, $pw, $ph)

Start-Sleep -Milliseconds 600
$probe = New-Object RECT2
[Win32Shot]::GetWindowRect($p.MainWindowHandle, [ref] $probe) | Out-Null
Write-Host "  topmost engaged, window at ($($probe.Left),$($probe.Top)) size $($probe.Right-$probe.Left)x$($probe.Bottom-$probe.Top)"

$rect = New-Object RECT2
if (-not [Win32Shot]::GetWindowRect($p.MainWindowHandle, [ref] $rect)) {
    throw "GetWindowRect failed for $Process"
}

$w = $rect.Right - $rect.Left
$h = $rect.Bottom - $rect.Top
if ($w -le 0 -or $h -le 0) { throw "degenerate window bounds ${w}x${h}" }

$bmp = New-Object System.Drawing.Bitmap $w, $h
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.CopyFromScreen($rect.Left, $rect.Top, 0, 0, $bmp.Size)
$g.Dispose()
$bmp.Save($Out, [System.Drawing.Imaging.ImageFormat]::Png)
$bmp.Dispose()

# Always drop the TOPMOST flag we set above, even on the failure path, otherwise the
# user's app is left pinned above everything until they restart it.
try { [Win32Shot]::ClearTopmost($p.MainWindowHandle) } catch { }

Write-Host "  screen grab  ${w}x${h} from ($($rect.Left),$($rect.Top))  -> $Out"
Write-Host "  NOTE: the pointer is drawn into this image; sample away from it when"
Write-Host "        measuring a row fill, and remember it shows live hover state."
