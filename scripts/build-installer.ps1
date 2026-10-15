param(
    [ValidatePattern('^\d+\.\d+\.\d+(\.\d+)?$')][string]$Version = '1.0.0',
    [ValidateSet('Aot', 'Standard')][string]$EngineMode = 'Aot',
    [string]$CompilerPath,
    [switch]$SkipPublish
)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
Push-Location $root
try {
    if (!$CompilerPath) {
        $compilerCommand = Get-Command ISCC.exe -ErrorAction SilentlyContinue
        $candidates = @(
            $(if ($compilerCommand) { $compilerCommand.Source }),
            (Join-Path $root 'artifacts/tools/inno/ISCC.exe'),
            "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
            "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe"
        )
        $CompilerPath = $candidates | Where-Object { $_ -and (Test-Path -LiteralPath $_ -PathType Leaf) } | Select-Object -First 1
    }
    if (!$CompilerPath -or !(Test-Path -LiteralPath $CompilerPath -PathType Leaf)) {
        throw 'Install Inno Setup 6.3+ (https://jrsoftware.org/isdl.php), or pass -CompilerPath <ISCC.exe>.'
    }
    if (!$SkipPublish) {
        & "$PSScriptRoot/publish.ps1" -Mode $EngineMode
        & "$PSScriptRoot/publish-configurator.ps1"
    }
    $engine = Join-Path $root "artifacts/publish/$($EngineMode.ToLowerInvariant())"
    $gui = Join-Path $root 'artifacts/publish/configurator'
    foreach ($required in @("$engine/FileOrganizer.exe", "$gui/FileOrganizer.Configurator.exe", "$gui/FileOrganizer.Configurator.runtimeconfig.json")) {
        if (!(Test-Path -LiteralPath $required -PathType Leaf)) { throw "Publish output missing: $required" }
    }
    $output = Join-Path $root 'artifacts/installer'
    $payload = Join-Path $output ('payload-' + [Guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Force "$payload/engine" | Out-Null
    # Copy only published application trees; never stage the repository or personal TOML files.
    Get-ChildItem -LiteralPath $gui | Where-Object { $_.Extension -ne '.pdb' } | Copy-Item -Destination $payload -Recurse
    if ($EngineMode -eq 'Aot') {
        Copy-Item -LiteralPath "$engine/FileOrganizer.exe" -Destination "$payload/engine"
    }
    else {
        Get-ChildItem -LiteralPath $engine | Where-Object { $_.Extension -ne '.pdb' } | Copy-Item -Destination "$payload/engine" -Recurse
    }
    Copy-Item -LiteralPath "$root/Config.Example.toml", "$root/README.md" -Destination $payload
    $notices = New-Item -ItemType Directory "$payload/licenses"
    Copy-Item -LiteralPath "$root/installer/licenses/Tomlyn.txt" -Destination $notices.FullName
    $runtime = Get-Content -LiteralPath "$gui/FileOrganizer.Configurator.runtimeconfig.json" -Raw | ConvertFrom-Json
    foreach ($framework in $runtime.runtimeOptions.includedFrameworks) {
        $package = Join-Path $root "artifacts/packages/$($framework.name.ToLowerInvariant()).runtime.win-x64/$($framework.version)"
        $licenses = @(Get-ChildItem -LiteralPath $package -File | Where-Object { $_.Name -like 'LICENSE*' -or $_.Name -like 'THIRD-PARTY-NOTICES*' })
        if (!$licenses.Count) { throw "Runtime license files missing: $package" }
        foreach ($license in $licenses) {
            Copy-Item -LiteralPath $license.FullName -Destination (Join-Path $notices.FullName ($framework.name + '-' + $license.Name))
        }
    }
    & $CompilerPath /Q "/DAppVersion=$Version" "/DPayloadDir=$payload" "/O$output" "$root/installer/FileOrganizer.iss"
    if ($LASTEXITCODE -ne 0) { throw 'Installer compilation failed' }
    $installer = Join-Path $output "FileOrganizer-$Version-win-x64-Setup.exe"
    Get-Item -LiteralPath $installer | Select-Object FullName, Length
    $hash = (Get-FileHash -LiteralPath $installer -Algorithm SHA256).Hash.ToLowerInvariant()
    "$hash  $([IO.Path]::GetFileName($installer))" | Set-Content -LiteralPath "$installer.sha256" -Encoding ascii
    Get-FileHash -LiteralPath $installer -Algorithm SHA256 | Format-List
}
finally { Pop-Location }
