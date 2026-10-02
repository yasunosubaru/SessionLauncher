# tools/verify-scaling.ps1 — does the new 创建时间 column survive a large font?
#
# Column widths are fixed doubles, so they do not follow FontSize. At 140% a
# "2026-10-01 17:39" that fitted at 100% clips to "2026-10-01 1", which is a
# wrong answer rather than an obviously-cut one. This reads the rendered cell
# text and requires the whole timestamp to be there.
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
function Rows {
    $root.FindAll([System.Windows.Automation.TreeScope]::Descendants,
        (New-Object System.Windows.Automation.PropertyCondition(
            [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
            [System.Windows.Automation.ControlType]::DataItem)))
}

# A cell showing a full timestamp has 16 characters: "2026-10-01 17:39".
$stamp = '^\d{4}-\d{2}-\d{2} \d{2}:\d{2}$'
function Measure-Stamps($label) {
    # $bad counts TRUNCATION only. An em dash is the correct rendering for a catalog
    # that carries no creation times at all, so counting it as a failure would report
    # exactly the stale-catalog case as broken.
    $bad = 0; $good = 0; $undated = 0; $samples = @()
    foreach ($row in (Rows)) {
        foreach ($t in $row.FindAll([System.Windows.Automation.TreeScope]::Descendants,
                (New-Object System.Windows.Automation.PropertyCondition(
                    [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
                    [System.Windows.Automation.ControlType]::Text)))) {
            $n = $t.Current.Name
            if (-not $n) { continue }
            if ($n -eq ([char]0x2014)) { $undated++; continue }
            if ($n -match '^\d{4}-\d{2}-\d{2}') {
                if ($n -match $stamp) { $good++ }
                else { $bad++; if ($samples.Count -lt 4) { $samples += $n } }
            }
        }
    }
    Write-Host ("  {0}: complete={1} truncated={2} undated(dash)={3} {4}" -f `
                $label, $good, $bad, $undated, ($samples -join ' '))
    return $bad
}

# Start from the default so the run is repeatable.
Invoke-Ancestor (Find-Text 'A') | Out-Null
Start-Sleep -Milliseconds 400
$reset = Find-Text ([char]0x25A1)
if ($reset) { Invoke-Ancestor $reset | Out-Null; Start-Sleep -Milliseconds 400 }

Write-Host "== at the app's default font =="
$bad0 = Measure-Stamps 'default'

Write-Host "== stepping up with A+ =="
for ($i = 0; $i -lt 6; $i++) {
    $plus = Find-Text 'A+'
    if (-not $plus) { break }
    Invoke-Ancestor $plus | Out-Null
    Start-Sleep -Milliseconds 350
}
$pct = $null
foreach ($t in (Get-Texts)) {
    if ($t.Current.Name -match '^字体\s*\d+%') { $pct = $t.Current.Name }
}
Write-Host "  font now: $pct"
$bad1 = Measure-Stamps 'large'

Write-Host ""
Write-Host "  verdict:"
if ($bad0 -eq 0 -and $bad1 -eq 0) { Write-Host "    [ok]   no truncated timestamp at either size" }
elseif ($bad0 -gt 0) { Write-Host "    [FAIL] $bad0 cell(s) already truncated at the default size — the column is too narrow at 100%" }
else { Write-Host "    [FAIL] $bad1 cell(s) truncated at the large size — the column does not follow the font" }

$p.CloseMainWindow() | Out-Null; $p.WaitForExit(8000) | Out-Null
Write-Host ""
Write-Host "OK pid=$($p.Id)"