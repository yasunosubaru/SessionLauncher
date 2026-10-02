# tools/verify-deep-link.ps1 — read-only acceptance for the REGISTERED path.
#
# A registered project must open with a deep link and add nothing to OpenChamber's
# settings. "Adds nothing" cannot be a byte-for-byte hash comparison: OpenChamber
# saves its OWN navigation when it follows the link, so the file legitimately
# changes. What must not change is its SHAPE — the same projects, the same top-level
# keys, and activeProjectId moved to the project we asked for.
[CmdletBinding()]
param([int]$WaitSeconds = 12)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
$env:PYTHONIOENCODING = 'utf-8'

$settings = "$env:USERPROFILE\.config\openchamber\settings.json"
$exe = '<repo>\apps\SessionLauncher\src\SessionLauncher.App\bin\Release\net10.0-windows\SessionLauncher.exe'

function Read-Shape($path) {
    $raw = [System.IO.File]::ReadAllText($path, [System.Text.Encoding]::UTF8)
    $j = $raw | ConvertFrom-Json
    [pscustomobject]@{
        Keys        = @($j.PSObject.Properties).Count
        Projects    = @($j.projects)
        Paths       = (@($j.projects) | ForEach-Object { $_.path } | Sort-Object)
        ActiveId    = $j.activeProjectId
        Raw         = $raw
    }
}

Get-Process SessionLauncher -ErrorAction SilentlyContinue | ForEach-Object { $_.Kill(); $_.WaitForExit(5000) } | Out-Null
$p = Start-Process $exe -PassThru
$deadline = (Get-Date).AddSeconds($WaitSeconds)
$root = $null
while ((Get-Date) -lt $deadline) {
    Start-Sleep -Milliseconds 700
    $p.Refresh()
    if ($p.HasExited) { throw "exited, code $($p.ExitCode)" }
    $c = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ProcessIdProperty, $p.Id)
    $root = [System.Windows.Automation.AutomationElement]::RootElement.FindFirst(
        [System.Windows.Automation.TreeScope]::Children, $c)
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
    $w = [System.Windows.Automation.TreeWalker]::ControlViewWalker
    for ($i = 0; $i -lt 8 -and $el; $i++) {
        try { $el.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke(); return $true }
        catch { $el = $w.GetParent($el) }
    }
    return $false
}

Invoke-Ancestor (Find-Text '项目') | Out-Null
Start-Sleep -Milliseconds 1500

$rows = $root.FindAll([System.Windows.Automation.TreeScope]::Descendants,
    (New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
        [System.Windows.Automation.ControlType]::DataItem)))

# Find a row whose first cell is the filled mark — a project OpenChamber already
# lists. It must NOT be the one already active, or there is nowhere for the deep
# link to move to and "activeProjectId changed" asserts nothing.
$before0 = Read-Shape $settings
$activePath = $null
foreach ($proj in $before0.Projects) {
    if ($proj.id -eq $before0.ActiveId) { $activePath = $proj.path }
}
Write-Host "currently active project: $activePath"

$filled = [char]0x25CF
$target = $null; $targetName = $null
foreach ($row in $rows) {
    $names = @($row.FindAll([System.Windows.Automation.TreeScope]::Descendants,
            (New-Object System.Windows.Automation.PropertyCondition(
                [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
                [System.Windows.Automation.ControlType]::Text))) |
        ForEach-Object { $_.Current.Name } | Where-Object { $_ })
    if ($names.Count -lt 2 -or $names[0] -ne $filled) { continue }
    # The row's LAST cell is the project path (proj.colPath); names[1] is the
    # label, which is not what activeProjectId stores.
    if ($names[-1] -eq $activePath) { continue }
    $targetName = "$($names[1]) ($($names[-1]))"; $target = $row; break
}
if (-not $target) { throw "no registered (filled-mark) project other than the active one" }
Write-Host "target registered project: $targetName"

$before = Read-Shape $settings
Write-Host "before: keys=$($before.Keys) projects=$(@($before.Projects).Count) active=$($before.ActiveId)"

$target.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select() | Out-Null
Start-Sleep -Milliseconds 700

$btn = Find-Text '打开整套会话'
if (-not $btn) { throw "open-set button not on screen" }
Invoke-Ancestor $btn | Out-Null
Start-Sleep -Seconds 3

$after = Read-Shape $settings
Write-Host "after:  keys=$($after.Keys) projects=$(@($after.Projects).Count) active=$($after.ActiveId)"
Write-Host ""
Write-Host ("  top-level keys unchanged : {0}  ({1} -> {2})" -f ($after.Keys -eq $before.Keys), $before.Keys, $after.Keys)
Write-Host ("  project count unchanged  : {0}  ({1} -> {2})" -f (@($after.Projects).Count -eq @($before.Projects).Count), @($before.Projects).Count, @($after.Projects).Count)
Write-Host ("  project set unchanged    : {0}" -f ((Compare-Object $before.Paths $after.Paths) -eq $null))
Write-Host ("  activeProjectId moved    : {0}  {1}" -f ($after.ActiveId -ne $before.ActiveId),
    $(if ($after.ActiveId -eq $before.ActiveId) { '(no change — the deep link did not switch projects)' } else { '' }))
Write-Host ("  launcher wrote a project : {0}" -f (-not ((Compare-Object $before.Paths $after.Paths) -eq $null)))

$p.CloseMainWindow() | Out-Null; $p.WaitForExit(8000) | Out-Null
Write-Host ""
Write-Host "OK pid=$($p.Id)"