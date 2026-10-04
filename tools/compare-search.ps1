<#
    compare-search.ps1 - head-to-head between this repo's SessionSearch and the
    variant produced by agent-swarm, on the REAL catalog.

    Each engine is compiled into its own throwaway console project (they share
    type names, so they cannot coexist in one assembly), run against the same
    queries, and their output is diffed.

    Usage:
        powershell -ExecutionPolicy Bypass -File .\tools\compare-search.ps1 `
            -Mine <path-to-SessionSearch.cs> -Theirs <path-to-SessionSearch.cs>
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string] $Mine,
    [Parameter(Mandatory = $true)][string] $Theirs,
    # Resolve the catalog the same way the app does: an explicit override first,
    # then the per-user default. No machine-specific path is baked in.
    [string] $Catalog = $(if ($env:SESSIONLAUNCHER_CATALOG) { $env:SESSIONLAUNCHER_CATALOG }
                         else { Join-Path $env:LOCALAPPDATA 'SessionLauncher\TOP-LEVEL-SESSIONS.md' }),
    # Generic probe terms: ASCII, CJK, and the mixed/multi-token shapes the
    # tokenizer has to get right. These were real queries against a real catalog
    # once; they are not needed to exercise any of those shapes.
    [string[]] $Queries = @('build', '笔记', 'agent', 'docker', '修复', 'report',
                            'build 笔记', 'te', 'review')
)

$ErrorActionPreference = 'Stop'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'

# Build the shared harness source once; only the engine file differs per run.
$reader = @'
using System.Globalization;
using System.Text;
using SessionLauncher.App.Services;

static List<SearchDoc> LoadCatalog(string path)
{
    var docs = new List<SearchDoc>();
    foreach (var raw in File.ReadAllLines(path, Encoding.UTF8))
    {
        var line = raw.TrimEnd();
        if (!line.StartsWith("|")) continue;
        var cells = Split(line);
        if (cells.Count < 7) continue;
        if (cells[0].Trim() == '#') continue;
        if (cells[0].Trim().StartsWith('-')) continue;
        var id = cells[6].Trim().Trim('`');
        if (id.Length == 0 || !id.StartsWith("ses_")) continue;
        if (!int.TryParse(cells[2].Trim(), out var msgs)) msgs = 0;
        docs.Add(new SearchDoc(id, cells[5].Trim(), cells[4].Trim().Trim('`'), cells[3].Trim(), msgs));
    }
    return docs;
}

static List<string> Split(string line)
{
    var t = line.StartsWith("|") ? line[1..] : line;
    if (t.EndsWith("|")) t = t[..^1];
    var cells = new List<string>();
    var cur = new StringBuilder();
    for (var i = 0; i < t.Length; i++)
    {
        if (t[i] == '\\' && i + 1 < t.Length && t[i + 1] == '|') { cur.Append('|'); i++; continue; }
        if (t[i] == '|') { cells.Add(cur.ToString()); cur.Clear(); continue; }
        cur.Append(t[i]);
    }
    cells.Add(cur.ToString());
    return cells;
}

var catalog = LoadCatalog(args[0]);
Console.WriteLine($"catalog={catalog.Count}");
foreach (var q in args.Skip(1))
{
    var results = SessionSearch.Search(catalog, q, 5);
    Console.WriteLine($"Q|{q}|{results.Count}|{SessionSearch.Count(catalog, q)}");
    foreach (var r in results)
        Console.WriteLine($"  R|{q}|{r.Hit.Doc.Id}|{Math.Round(r.Hit.Score, 1)}|{(r.IsFuzzy ? "fz" : "lit")}|{r.TitleRanges.Count}");
}
'@

function Invoke-Engine([string] $enginePath, [string] $tag) {
    $tmp = Join-Path $env:TEMP ("cmp-$tag-" + [Guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Force -Path $tmp | Out-Null
    try {
        Copy-Item $enginePath (Join-Path $tmp 'SessionSearch.cs') -Force
        Set-Content (Join-Path $tmp 'Compare.csproj') -Encoding UTF8 -Value @"
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable><ImplicitUsings>enable</ImplicitUsings>
    <NoWarn>CS8602;CS8604</NoWarn>
  </PropertyGroup>
</Project>
"@
        Set-Content (Join-Path $tmp 'Program.cs') -Encoding UTF8 -Value $reader

        Push-Location $tmp
        try {
            # `dotnet run` has no --nologo: it is not a run option, so it gets forwarded to
            # the app as args[0] and the harness then tries to open a file named "--nologo".
            # $env:DOTNET_NOLOGO is set at the top of this script instead.
            # 2>&1 is essential: the compiler diagnostics land on the error stream, and
            # without the merge a build failure shows up as a bare "engine exited 1".
            & dotnet run -c Release -- $Catalog @Queries 2>&1 | ForEach-Object { "$_" }
            if ($LASTEXITCODE -ne 0) { throw "engine '$tag' exited $LASTEXITCODE" }
        }
        finally { Pop-Location }
    }
    finally { Remove-Item $tmp -Recurse -Force -ErrorAction SilentlyContinue }
}

$mineOut = Invoke-Engine $Mine 'mine'
$theirsOut = Invoke-Engine $Theirs 'theirs'

Write-Host "=================== MINE ===================" -ForegroundColor Cyan
$mineOut | ForEach-Object { $_ }
Write-Host "=================== SWARM ==================" -ForegroundColor Magenta
$theirsOut | ForEach-Object { $_ }

$m = @{}; $mineOut   | Where-Object { $_ -like 'R|*' } | ForEach-Object { $k = ($_ -split '\|')[1] + '|' + ($_ -split '\|')[2]; $m[$k] = $_ }
$s = @{}; $theirsOut | Where-Object { $_ -like 'R|*' } | ForEach-Object { $k = ($_ -split '\|')[1] + '|' + ($_ -split '\|')[2]; $s[$k] = $_ }

Write-Host "=================== DIFF ===================" -ForegroundColor Yellow
$diff = 0
foreach ($k in $m.Keys) {
    if ($s[$k] -ne $m[$k]) { $diff++; Write-Host "  MISMATCH $k"; Write-Host "    mine  : $($m[$k])"; Write-Host "    swarm : $($s[$k])" }
}
if ($diff -eq 0) { Write-Host "  top-N orderings identical for every query" -ForegroundColor Green }
else { Write-Host "  $diff differing rows" }