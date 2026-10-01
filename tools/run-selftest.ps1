<#
    run-selftest.ps1 - execute the plain-logic self tests headlessly.

    The app has zero NuGet references, so there is no test framework to lean on.
    This copies the non-UI service sources into a throwaway console project and
    runs SelfTest.Run(), then deletes the project.

    Usage:
        powershell -ExecutionPolicy Bypass -File .\tools\run-selftest.ps1
#>
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'

$here  = Split-Path -Parent $MyInvocation.MyCommand.Path
$root  = Split-Path -Parent $here
$src   = Join-Path $root 'src\SessionLauncher.App'
$temp  = Join-Path $env:TEMP ("sl-selftest-" + [Guid]::NewGuid().ToString('N'))

# Self-contained: no WPF, no System.Windows, no App.xaml.
# Adding a new service here is not optional: a service that is not staged is simply
# not tested, and nothing warns you. ProjectCatalog and ProjectSort carry their own
# RunSelfTest and are called from SelfTest.Run.
$sources = @(
    'Models\AppLang.cs'
    'Models\AppView.cs'
    'Models\SessionInfo.cs'
    'Models\ProjectRow.cs'
    'Services\Loc.cs'
    'Services\AppSettings.cs'
    'Services\SessionSearch.cs'
    'Services\SessionCatalog.cs'
    'Services\ProjectCatalog.cs'
    'Services\ProjectSort.cs'
    'Services\WorkspaceService.cs'
    'Services\OpenChamberBridge.cs'
    'Services\SelfTest.cs'
)

Write-Host "==> Staging $($sources.Count) source files into $temp" -ForegroundColor Cyan
New-Item -ItemType Directory -Force -Path (Join-Path $temp 'Models'), (Join-Path $temp 'Services') | Out-Null

foreach ($rel in $sources) {
    $from = Join-Path $src $rel
    if (-not (Test-Path $from)) { throw "missing source: $from" }
    $to = Join-Path $temp ($rel -replace '[\\/]', [IO.Path]::DirectorySeparatorChar)
    Copy-Item $from $to -Force
}

@'
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <AssemblyName>SelfTestRunner</AssemblyName>
    <RootNamespace>SelfTestRunner</RootNamespace>
  </PropertyGroup>
</Project>
'@ | Set-Content (Join-Path $temp 'SelfTestRunner.csproj') -Encoding UTF8

@'
using SessionLauncher.App.Services;
try
{
    var checks = SelfTest.Run();
    Console.WriteLine($"SELFTEST OK  assertions={checks}");
    return 0;
}
catch (Exception ex)
{
    Console.Error.WriteLine($"SELFTEST FAIL  {ex.GetType().Name}: {ex.Message}");
    Console.Error.WriteLine(ex.StackTrace);
    return 1;
}
'@ | Set-Content (Join-Path $temp 'Program.cs') -Encoding UTF8

$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'

try {
    Push-Location $temp
    Write-Host "==> dotnet run" -ForegroundColor Cyan
    & dotnet run -c Release --nologo
    $code = $LASTEXITCODE
    Pop-Location
}
finally {
    Remove-Item $temp -Recurse -Force -ErrorAction SilentlyContinue
}

if ($code -ne 0) { throw "self tests failed (exit $code)" }
Write-Host "==> Self tests passed" -ForegroundColor Green
