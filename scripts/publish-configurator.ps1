$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
Push-Location $root
try {
    $env:DOTNET_CLI_HOME = Join-Path $root 'artifacts/cli-home'
    $env:NUGET_PACKAGES = Join-Path $root 'artifacts/packages'
    $env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
    dotnet publish src/FileOrganizer.Configurator/FileOrganizer.Configurator.csproj -c Release -r win-x64 --self-contained true -p:PublishAot=false -p:PublishTrimmed=false -o artifacts/publish/configurator -m:1
    if ($LASTEXITCODE -ne 0) { throw 'Configurator publish failed' }
    Get-Item artifacts/publish/configurator/FileOrganizer.Configurator.exe | Select-Object FullName,Length
}
finally { Pop-Location }
