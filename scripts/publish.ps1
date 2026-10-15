param([ValidateSet('Aot', 'Standard')][string]$Mode = 'Aot')
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
Push-Location $root
try {
    $env:DOTNET_CLI_HOME = Join-Path $root 'artifacts/cli-home'
    $env:NUGET_PACKAGES = Join-Path $root 'artifacts/packages'
    $env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
    $aot = ($Mode -eq 'Aot').ToString().ToLowerInvariant()
    $output = Join-Path $root "artifacts/publish/$($Mode.ToLowerInvariant())"
    dotnet publish src/FileOrganizer/FileOrganizer.csproj -c Release -r win-x64 --self-contained true -p:PublishAot=$aot -p:PublishTrimmed=$aot -o $output -m:1
    if ($LASTEXITCODE -ne 0) { throw "Publish failed ($Mode). For a missing C++/Windows SDK toolchain use -Mode Standard." }
    & (Join-Path $output 'FileOrganizer.exe') --help
    if ($LASTEXITCODE -ne 0) { throw 'Published executable smoke test failed' }
    Get-Item (Join-Path $output 'FileOrganizer.exe') | Select-Object FullName, Length
}
finally { Pop-Location }
