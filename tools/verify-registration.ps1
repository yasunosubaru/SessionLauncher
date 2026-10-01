# tools/verify-registration.ps1 — read-only acceptance for the project dispatch.
#
# Selects a project row and invokes the open-set button through UIA patterns on
# OUR OWN window. Never drives input at OpenChamber and never touches its
# settings: the point is to watch the launcher wait, not to make it write.
[CmdletBinding()]
param(
    [int]$WaitSeconds = 12,
    # Skip the cancel click so the wait runs to its timeout instead.
    [switch]$SkipCancel,
    # How long to watch for the timeout message before giving up.
    [int]$TimeoutWatchSeconds = 20
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes

$settings = "$env:USERPROFILE\.config\openchamber\settings.json"
$exe = '<repo>\apps\SessionLauncher\src\SessionLauncher.App\bin\Release\net10.0-windows\SessionLauncher.exe'

function Hash-Of($path) { (Get-FileHash $path -Algorithm SHA256).Hash }

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
    $c = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
        [System.Windows.Automation.ControlType]::Text)
    $root.FindAll([System.Windows.Automation.TreeScope]::Descendants, $c)
}

function On-Screen($el) {
    $r = $el.Current.BoundingRectangle
    if ($r.IsEmpty -or $r.Width -le 0 -or $r.Height -le 0) { return $false }
    return ($r.X -ge $win.X -and $r.Y -ge $win.Y `
            -and ($r.X + $r.Width) -le ($win.X + $win.Width) `
            -and ($r.Y + $r.Height) -le ($win.Y + $win.Height))
}

function Find-Text($name) {
    foreach ($t in (Get-Texts)) { if ($t.Current.Name -eq $name -and (On-Screen $t)) { return $t } }
    return $null
}

function Invoke-Ancestor($el) {
    $walker = [System.Windows.Automation.TreeWalker]::ControlViewWalker
    for ($i = 0; $i -lt 8 -and $el; $i++) {
        try { $el.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke(); return $true }
        catch { $el = $walker.GetParent($el) }
    }
    return $false
}

Write-Host "== switch to the project view =="
if (-not (Invoke-Ancestor (Find-Text '项目'))) { throw "could not invoke the project view toggle" }
Start-Sleep -Milliseconds 1200

# WPF's ListView with a GridView reports its rows as DataItem inside a DataGrid,
# NOT as ListItem inside a List — which is why searching for ControlType.List here
# finds nothing and the first version of this script saw zero rows.
$items = $root.FindAll([System.Windows.Automation.TreeScope]::Descendants,
    (New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
        [System.Windows.Automation.ControlType]::DataItem)))
Write-Host "project rows on screen: $($items.Count)"
if ($items.Count -eq 0) { throw "no project rows" }

# Row 0 under the default sort is the most recently used project. Its mark tells
# us whether it is registered; either kind is a valid thing to click, and the two
# produce deliberately different behaviour.
$target = $items[0]
$sel = $target.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern)
$sel.Select()
Start-Sleep -Milliseconds 800
Write-Host "selected row 0"

$before = Hash-Of $settings
Write-Host "settings hash before: $before"

$label = '打开整套会话'
$btn = Find-Text $label
if (-not $btn) { $label = 'Open the whole set'; $btn = Find-Text $label }
if (-not $btn) { throw "the open-set button is not on screen" }
if (-not (Invoke-Ancestor $btn)) { throw "could not invoke the open-set button" }
Start-Sleep -Milliseconds 2000

Write-Host ""
Write-Host "== after invoking =="
foreach ($t in (Get-Texts)) {
    if (-not (On-Screen $t)) { continue }
    $n = $t.Current.Name
    if ($n -and ($n -match 'Quit|注册|等待|Cancel|Registered|timed out|Opened|Cancel wait')) {
        Write-Host "  status-ish: $n"
    }
}

$cancel = Find-Text '取消等待'
if (-not $cancel) { $cancel = Find-Text 'Cancel wait' }
Write-Host ("  cancel button visible: {0}" -f [bool]$cancel)

$after = Hash-Of $settings
Write-Host "  settings hash after:  $after"
Write-Host ("  settings UNCHANGED:   {0}" -f ($before -eq $after))

if ($SkipCancel) {
    Write-Host ""
    Write-Host "== watching for the timeout (not cancelling) =="
    $watch = (Get-Date).AddSeconds($TimeoutWatchSeconds)
    $timedOut = $false
    while ((Get-Date) -lt $watch) {
        Start-Sleep -Milliseconds 700
        foreach ($t in (Get-Texts)) {
            if (-not (On-Screen $t)) { continue }
            if ($t.Current.Name -match '超时|timed out') {
                Write-Host "  timed-out message: $($t.Current.Name)"
                $timedOut = $true
            }
        }
        if ($timedOut) { break }
    }
    if (-not $timedOut) { Write-Host "  [FAIL] no timeout message within $TimeoutWatchSeconds s" }

    $final = Hash-Of $settings
    Write-Host "  settings hash after timeout: $final"
    Write-Host ("  settings UNCHANGED:          {0}" -f ($final -eq $before))

    $cancelStill = Find-Text '取消等待'
    if (-not $cancelStill) { $cancelStill = Find-Text 'Cancel wait' }
    Write-Host ("  cancel button hidden:        {0}" -f (-not [bool]$cancelStill))
}
elseif ($cancel) {
    Write-Host ""
    Write-Host "== cancelling =="
    Invoke-Ancestor $cancel | Out-Null
    Start-Sleep -Milliseconds 1500
    $final = Hash-Of $settings
    Write-Host "  settings hash after cancel: $final"
    Write-Host ("  settings UNCHANGED:         {0}" -f ($final -eq $before))
    foreach ($t in (Get-Texts)) {
        if (-not (On-Screen $t)) { continue }
        if ($t.Current.Name -match '取消|超时|Cancelled|timed out') { Write-Host "  final status: $($t.Current.Name)" }
    }
}

$p.CloseMainWindow() | Out-Null
$p.WaitForExit(8000) | Out-Null
Write-Host ""
Write-Host "OK pid=$($p.Id)"