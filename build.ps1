<#
    build.ps1 - build SessionLauncher (GUI + MCP) and stage the catalog.

    Both halves build offline:
      - the GUI is .NET 10 WPF with zero NuGet references
      - the MCP server is dependency-free Node (JSON-RPC over stdio, by hand)

    Usage:
        powershell -ExecutionPolicy Bypass -File .\build.ps1
        powershell -ExecutionPolicy Bypass -File .\build.ps1 -RefreshCatalog
#>
[CmdletBinding()]
param(
    [switch] $RefreshCatalog,
    [string] $Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$app  = Join-Path $root 'src\SessionLauncher.App'
$mcp  = Join-Path $root 'src\SessionLauncher.Mcp'

function Step($text) { Write-Host "==> $text" -ForegroundColor Cyan }

Step "Refreshing catalog from opencode.db"
Push-Location $mcp
try {
    # refresh_catalog.mjs reports progress on stderr, which is the right choice for a
    # tool that shares stdout conventions with the MCP server. Under
    # $ErrorActionPreference='Stop' PowerShell still turns native stderr into a
    # NativeCommandError, so a successful run prints a red error block. Relax the
    # preference for the call and keep the real failure signal on the exit code.
    $ErrorActionPreference = 'Continue'
    $catalogOut = & node (Join-Path $mcp 'refresh_catalog.mjs') 2>&1
    $catalogExit = $LASTEXITCODE
    $ErrorActionPreference = 'Stop'

    $catalogOut | ForEach-Object { Write-Host "   $_" }
    if ($catalogExit -ne 0) { throw "refresh_catalog.mjs failed with exit $catalogExit" }
} finally { Pop-Location }

Step "Building WPF GUI ($Configuration)"
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'
& dotnet build (Join-Path $app 'SessionLauncher.App.csproj') -c $Configuration --nologo
if ($LASTEXITCODE -ne 0) { throw "dotnet build failed with exit $LASTEXITCODE" }

$exe = Join-Path $app "bin\$Configuration\net10.0-windows\SessionLauncher.exe"
if (-not (Test-Path $exe)) { throw "expected $exe to exist after build" }

Step "Syntax-checking MCP server"
foreach ($file in @('server.mjs', 'refresh_catalog.mjs', 'lib\catalog.mjs')) {
    & node --check (Join-Path $mcp $file)
    if ($LASTEXITCODE -ne 0) { throw "node --check failed for $file" }
    Write-Host "    ok  $file"
}

Step "Done"
Write-Host "    GUI : $exe"
Write-Host "    MCP : $mcp\server.mjs"
Write-Host ""
Write-Host "Launch the GUI:  '$exe'"
