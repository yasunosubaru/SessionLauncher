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
    [int]$TimeoutWatchSeconds = 20,
    # After starting the wait, interfere with it: select another row and press the
    # open-set button again. Both used to destroy the live wait.
    [switch]$SecondClick
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes

$settings = "$env:USERPROFILE\.config\openchamber\settings.json"
$exe = '<repo>\apps\SessionLauncher\src\SessionLauncher.App\bin\Release\net10.0-windows\SessionLauncher.exe'

# A byte hash is NOT a valid check here. OpenChamber is RUNNING during this test
# and saves its own state whenever it likes, so a hash comparison can fail for
# reasons that have nothing to do with the launcher -- and a byte-for-byte match
# would be luck rather than evidence. Compare the document's SHAPE instead, the
# way verify-deep-link.ps1 does: same projects, same top-level keys.
#
# What this test is really asserting is already visible without any of it: an
# unregistered project clicked while OpenChamber runs produces the tray-Quit
# message and a cancel button, and neither of those paths writes.
function Get-Shape($path) {
    $j = [System.IO.File]::ReadAllText($path, [System.Text.Encoding]::UTF8) | ConvertFrom-Json
    [pscustomobject]@{
        Keys     = @($j.PSObject.Properties).Count
        Projects = @($j.projects).Count
        Paths    = (@($j.projects) | ForEach-Object { $_.path } | Sort-Object)
    }
}
function Same-Shape($a, $b) {
    ($a.Keys -eq $b.Keys) -and ($a.Projects -eq $b.Projects) `
        -and ((Compare-Object $a.Paths $b.Paths) -eq $null)
}

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

# Pick a row that is explicitly UNREGISTERED (hollow mark). Clicking row 0 and
# hoping is not a test: which of the three dispatch branches runs depends on the
# sort order, and the wait branch is the one this script is about.
$hollow = [char]0x25CB
$target = $null; $targetName = $null
foreach ($row in $items) {
    $names = @($row.FindAll([System.Windows.Automation.TreeScope]::Descendants,
            (New-Object System.Windows.Automation.PropertyCondition(
                [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
                [System.Windows.Automation.ControlType]::Text))) |
        ForEach-Object { $_.Current.Name } | Where-Object { $_ })
    if ($names.Count -gt 1 -and $names[0] -eq $hollow) {
        $target = $row; $targetName = $names[1]; break
    }
}
if (-not $target) { throw "no unregistered (hollow-mark) project row found" }
Write-Host "target UNREGISTERED project: $targetName"
$target.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select() | Out-Null
Start-Sleep -Milliseconds 800

$before = Get-Shape $settings
Write-Host "settings before: keys=$($before.Keys) projects=$($before.Projects)"

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

$after = Get-Shape $settings
Write-Host "  settings after:  keys=$($after.Keys) projects=$($after.Projects)"
Write-Host ("  settings UNCHANGED:   {0}" -f (Same-Shape $before $after))

if ($SecondClick) {
    Write-Host ""
    Write-Host "== interfering: select another row, then press the button again =="
    # Selecting a different row used to re-enable ProjectOcButton, and the second
    # press used to dispose the live wait and then report a timeout for it. The
    # wait must survive both.
    $other = $null
    foreach ($row in $items) {
        if (-not [object]::ReferenceEquals($row, $target)) { $other = $row; break }
    }
    if ($other) {
        $other.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select() | Out-Null
        Start-Sleep -Milliseconds 900
        Write-Host "  selected a different row"
    }

    $btn2 = Find-Text '打开整套会话'
    if ($btn2) { Invoke-Ancestor $btn2 | Out-Null; Start-Sleep -Milliseconds 1500 }

    $cancel2 = Find-Text '取消等待'
    if (-not $cancel2) { $cancel2 = Find-Text 'Cancel wait' }
    Write-Host ("  cancel button still visible: {0}   (expected True)" -f [bool]$cancel2)

    $stillWaiting = $false
    foreach ($t in (Get-Texts)) {
        if (-not (On-Screen $t)) { continue }
        if ($t.Current.Name -match 'Quit|退出' -and $t.Current.Name -notmatch '超时|timed out') {
            $stillWaiting = $true
        }
        if ($t.Current.Name -match '已在等待|already waiting') { $stillWaiting = $true }
    }
    Write-Host ("  wait still in progress:       {0}   (expected True)" -f $stillWaiting)

    # What the status line actually says: read the bottom strip of the window, where
    # StatusText and the cancel button live. A boolean that comes back False needs
    # the reason next to it, not just the fact.
    Write-Host "  --- bottom strip of the window ---"
    foreach ($t in (Get-Texts)) {
        if (-not (On-Screen $t)) { continue }
        $r = $t.Current.BoundingRectangle
        if ($r.Y -lt ($win.Y + $win.Height - 140)) { continue }
        Write-Host ("    > [{0}] '{1}'" -f $r.X, $t.Current.Name)
    }
    Write-Host "  -----------------------------------------------"
    if (-not $cancel2 -or -not $stillWaiting) {
        Write-Host "  [FAIL] the second click killed the live wait"
    } else {
        Write-Host "  [ok]   the second click was refused and the wait survived"
    }
}

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

    $final = Get-Shape $settings
    Write-Host "  settings after timeout: keys=$($final.Keys) projects=$($final.Projects)"
    Write-Host ("  settings UNCHANGED:          {0}" -f (Same-Shape $before $final))

    $cancelStill = Find-Text '取消等待'
    if (-not $cancelStill) { $cancelStill = Find-Text 'Cancel wait' }
    Write-Host ("  cancel button hidden:        {0}" -f (-not [bool]$cancelStill))
}
elseif ($cancel) {
    Write-Host ""
    Write-Host "== cancelling =="
    Invoke-Ancestor $cancel | Out-Null
    Start-Sleep -Milliseconds 1500
    $final = Get-Shape $settings
    Write-Host "  settings after cancel: keys=$($final.Keys) projects=$($final.Projects)"
    Write-Host ("  settings UNCHANGED:         {0}" -f (Same-Shape $before $final))
    foreach ($t in (Get-Texts)) {
        if (-not (On-Screen $t)) { continue }
        if ($t.Current.Name -match '取消|超时|Cancelled|timed out') { Write-Host "  final status: $($t.Current.Name)" }
    }
}

$p.CloseMainWindow() | Out-Null
$p.WaitForExit(8000) | Out-Null
Write-Host ""
Write-Host "OK pid=$($p.Id)"