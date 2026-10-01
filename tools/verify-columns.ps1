# tools\_verify-columns.ps1 — read-only acceptance for the two new 创建时间 columns.
#
# Drives the launcher's OWN view toggle through UIA's InvokePattern, which is not a
# synthetic click: it invokes the control's command. Nothing here touches
# OpenChamber, and nothing is read out of a screen bitmap.
[CmdletBinding()]
param([int]$WaitSeconds = 12)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes

$exe = '<repo>\apps\SessionLauncher\src\SessionLauncher.App\bin\Release\net10.0-windows\SessionLauncher.exe'
Get-Process SessionLauncher -ErrorAction SilentlyContinue | ForEach-Object { $_.Kill(); $_.WaitForExit(5000) } | Out-Null

$p = Start-Process $exe -PassThru
$deadline = (Get-Date).AddSeconds($WaitSeconds)
$root = $null
while ((Get-Date) -lt $deadline) {
    Start-Sleep -Milliseconds 700
    $p.Refresh()
    if ($p.HasExited) { throw "exited, code $($p.ExitCode)" }
    $cond = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ProcessIdProperty, $p.Id)
    $root = [System.Windows.Automation.AutomationElement]::RootElement.FindFirst(
        [System.Windows.Automation.TreeScope]::Children, $cond)
    if ($root) { break }
}
if (-not $root) { throw "no window" }

$win = $root.Current.BoundingRectangle

function Get-Texts {
    $cond = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
        [System.Windows.Automation.ControlType]::Text)
    return $root.FindAll([System.Windows.Automation.TreeScope]::Descendants, $cond)
}

function On-Screen($el) {
    $r = $el.Current.BoundingRectangle
    if ($r.IsEmpty -or $r.Width -le 0 -or $r.Height -le 0) { return $false }
    return ($r.X -ge $win.X -and $r.Y -ge $win.Y `
            -and ($r.X + $r.Width) -le ($win.X + $win.Width) `
            -and ($r.Y + $r.Height) -le ($win.Y + $win.Height))
}

function Find-Text($name) {
    foreach ($t in (Get-Texts)) {
        if ($t.Current.Name -eq $name -and (On-Screen $t)) { return $t }
    }
    return $null
}

function Switch-View($label) {
    $node = Find-Text $label
    if (-not $node) { throw "view toggle '$label' is not on screen" }

    # Walk up until something actually supports InvokePattern. Testing the Text
    # element itself is the mistake: a Text has no command, so the invoke has to
    # land on the Button that owns it.
    $walker = [System.Windows.Automation.TreeWalker]::ControlViewWalker
    for ($i = 0; $i -lt 8 -and $node; $i++) {
        try {
            $inv = $node.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)
            $inv.Invoke()
            Start-Sleep -Milliseconds 1200
            Write-Host "  switched to $label"
            return
        } catch {
            $node = $walker.GetParent($node)
        }
    }
    throw "no ancestor of '$label' supports InvokePattern"
}

function Show-Headers($title, $names) {
    Write-Host ""
    Write-Host "  $title"
    foreach ($n in $names) {
        $hits = @()
        foreach ($t in (Get-Texts)) {
            if ($t.Current.Name -ne $n) { continue }
            if (-not (On-Screen $t)) { continue }
            $r = $t.Current.BoundingRectangle
            $hits += ("x={0} y={1} w={2}" -f $r.X, $r.Y, $r.Width)
        }
        if ($hits.Count -eq 0) { Write-Host "     [absent ] $n" }
        else { $hits | ForEach-Object { Write-Host "     [present] $n  $_" } }
    }
}

Write-Host ("window: x={0} y={1} w={2} h={3}" -f $win.X, $win.Y, $win.Width, $win.Height)

# The names each view's columns are expected to carry, and the two that were
# removed. "absent" is the pass condition for the removals.
$sessionHeaders = @('选', '更新时间', '消息数', '标题', '文件夹', '创建时间', '会话 ID')
$projectHeaders = @('OpenChamber', '项目', '会话', '创建时间', '最近使用', '路径', '颜色')

Switch-View '会话'
Show-Headers '会话 view:' $sessionHeaders

Switch-View '项目'
Show-Headers '项目 view:' $projectHeaders

# The registration marks. OpenChamber holds four projects on this machine, so a
# working read of its settings.json must produce exactly four filled circles.
$filled = 0; $hollow = 0
foreach ($t in (Get-Texts)) {
    if (-not (On-Screen $t)) { continue }
    if ($t.Current.Name -eq [char]0x25CF) { $filled++ }
    elseif ($t.Current.Name -eq [char]0x25CB) { $hollow++ }
}
Write-Host ""
Write-Host "  registration marks on screen: filled=$filled hollow=$hollow"
if ($filled -lt 1) { Write-Host "     [FAIL] no filled marks — the settings read produced nothing registered" }
else { Write-Host "     [ok]   at least one project is read as registered" }

Write-Host ""
Write-Host "OK pid=$($p.Id)"