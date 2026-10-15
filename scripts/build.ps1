param([switch]$SkipTests, [switch]$EngineOnly)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
Push-Location $root
try {
    $env:DOTNET_CLI_HOME = Join-Path $root 'artifacts/cli-home'
    $env:NUGET_PACKAGES = Join-Path $root 'artifacts/packages'
    $env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
    $target = if ($EngineOnly) { 'tests/FileOrganizer.Tests/FileOrganizer.Tests.csproj' } else { 'FileOrganizer.sln' }
    dotnet restore $target --disable-parallel -m:1
    if ($LASTEXITCODE -ne 0) { throw 'Restore failed' }
    dotnet build $target -c Release --no-restore -m:1
    if ($LASTEXITCODE -ne 0) { throw 'Build failed' }
    if (!$SkipTests) {
        dotnet test $target -c Release --no-build --no-restore -m:1 --logger 'console;verbosity=minimal' --blame-hang-timeout 60s
        if ($LASTEXITCODE -ne 0) { throw 'Tests failed' }
    }
}
finally { Pop-Location }
