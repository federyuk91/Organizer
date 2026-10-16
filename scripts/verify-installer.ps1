param(
    [string]$InstallerPath = 'artifacts/installer/FileOrganizer-1.3.4-win-x64-Setup.exe',
    [switch]$Isolated,
    [string]$CompilerPath
)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$installer = (Resolve-Path -LiteralPath $InstallerPath).Path
$identity = [Guid]::NewGuid().ToString('N')
$fixture = Join-Path $root "artifacts/installer/verification-$identity"
$install = Join-Path $fixture 'application'
$group = 'File Organizer'
$menu = Join-Path ([Environment]::GetFolderPath('Programs')) $group
$uninstallKey = 'Software\Microsoft\Windows\CurrentVersion\Uninstall\{3E9CD6A4-C084-4A41-9C10-4AF015B3A2EE}_is1'
if ($Isolated) {
    $build = Get-Content -LiteralPath "$installer.build.json" -Raw | ConvertFrom-Json
    $setupSource = Join-Path $root 'installer/FileOrganizer.iss'
    if ((Get-FileHash -LiteralPath $setupSource -Algorithm SHA256).Hash -ne $build.SetupSourceHash) { throw 'Installer source changed since build; rebuild before verification.' }
    if (!$CompilerPath) {
        $compilerCommand = Get-Command ISCC.exe -ErrorAction SilentlyContinue
        $CompilerPath = if ($compilerCommand) { $compilerCommand.Source } else { Join-Path $root 'artifacts/tools/inno/ISCC.exe' }
    }
    if (!(Test-Path -LiteralPath $CompilerPath)) { throw 'Isolated verification requires -CompilerPath <ISCC.exe>.' }
    New-Item -ItemType Directory -Force $fixture | Out-Null
    $testGuid = [Guid]::NewGuid().ToString().ToUpperInvariant()
    $group = "File Organizer Verification $identity"
    $menu = Join-Path ([Environment]::GetFolderPath('Programs')) $group
    $uninstallKey = "Software\Microsoft\Windows\CurrentVersion\Uninstall\{$testGuid}_is1"
    # The same payload and setup logic use a separate app identity and shortcut group only in this test copy.
    $testSource = [IO.File]::ReadAllText($setupSource).Replace('3E9CD6A4-C084-4A41-9C10-4AF015B3A2EE', $testGuid).
        Replace('DefaultGroupName=File Organizer', "DefaultGroupName=$group").
        Replace('OutputBaseFilename=FileOrganizer-{#AppVersion}-win-x64-Setup', 'OutputBaseFilename=FileOrganizer-{#AppVersion}-win-x64-Verification-Setup')
    $testScript = Join-Path $fixture 'verification.iss'
    [IO.File]::WriteAllText($testScript, $testSource)
    & $CompilerPath /Q "/DAppVersion=$($build.Version)" "/DPayloadDir=$($build.Payload)" "/O$fixture" $testScript
    if ($LASTEXITCODE -ne 0) { throw 'Isolated installer compilation failed' }
    $installer = Join-Path $fixture "FileOrganizer-$($build.Version)-win-x64-Verification-Setup.exe"
}
$runKey = 'Software\Microsoft\Windows\CurrentVersion\Run'
$ownedName = "FileOrganizer-$identity"
$otherName = "FileOrganizer-other-$identity"
$foreignName = "InstallerTest-$identity"
$existing = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey($uninstallKey)
if ($existing) { $existing.Dispose(); throw 'An installation already exists; use an isolated Windows account for installer verification.' }
if (Test-Path -LiteralPath $menu) { throw 'Start menu shortcuts already exist; use an isolated Windows account for installer verification.' }
New-Item -ItemType Directory -Force "$fixture/input", "$fixture/desktop" | Out-Null
function Invoke-Setup([string]$Executable, [string[]]$SetupArguments) {
    $process = Start-Process -FilePath $Executable -ArgumentList $SetupArguments -WindowStyle Hidden -PassThru -Wait
    if ($process.ExitCode -ne 0) { throw "Setup operation failed: $($process.ExitCode)" }
}
try {
    $arguments = @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', '/TASKS=', "/DIR=`"$install`"", "/LOG=`"$fixture/install.log`"")
    Invoke-Setup $installer $arguments
    if (!(Select-String -LiteralPath "$fixture/install.log" -Pattern 'Administrative install mode: No' -Quiet)) { throw 'Setup used administrative install mode' }
    $registry = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey($runKey)
    if ($registry) {
        try {
            foreach ($name in $registry.GetValueNames()) {
                if ([string]$registry.GetValue($name) -like "*$install*FileOrganizer.exe*") { throw 'Setup enabled autorun without consent' }
            }
        }
        finally { $registry.Dispose() }
    }
    foreach ($required in @('FileOrganizer.Configurator.exe', 'engine/FileOrganizer.exe', 'FileOrganizer.ico', 'Config.Example.toml', 'README.md', 'licenses/Tomlyn.txt', 'unins000.exe')) {
        if (!(Test-Path -LiteralPath (Join-Path $install $required))) { throw "Installed file missing: $required" }
    }
    if (Get-ChildItem -LiteralPath $install -Recurse -File | Where-Object { $_.Name -in @('AGENTS.md', 'GUI_PLAN.md', 'Config.Downloads.toml') -or $_.Extension -eq '.pdb' }) {
        throw 'Installer contains local/private build files'
    }
    $shell = New-Object -ComObject WScript.Shell
    foreach ($shortcut in @('File Organizer', 'Avvia organizzazione', 'Scansione adesso', 'Arresta organizzazione', 'Disinstalla File Organizer')) {
        $link = Join-Path $menu "$shortcut.lnk"
        if (!(Test-Path -LiteralPath $link)) { throw "Shortcut missing: $shortcut" }
        $shortcutData = $shell.CreateShortcut($link)
        $target = $shortcutData.TargetPath
        if (!(Test-Path -LiteralPath $target)) { throw "Shortcut target missing: $target" }
        if ($shortcutData.IconLocation -ne "$install\FileOrganizer.ico,0") { throw "Shortcut icon missing: $shortcut" }
    }
    $config = Join-Path $install 'user-settings.toml'
    $text = [IO.File]::ReadAllText((Join-Path $install 'Config.Example.toml'))
    $text = $text.Replace('%USERPROFILE%\Downloads', "$fixture/input").Replace('%USERPROFILE%\Desktop', "$fixture/desktop").Replace('dry_run = false', 'dry_run = true')
    [IO.File]::WriteAllText($config, $text)
    & "$install/engine/FileOrganizer.exe" validate --config $config
    if ($LASTEXITCODE -ne 0) { throw 'Installed engine rejected config' }
    [IO.File]::WriteAllText("$fixture/input/keep.png", 'preserve this file')
    & "$install/engine/FileOrganizer.exe" scan --config $config
    if ($LASTEXITCODE -ne 0 -or !(Test-Path "$fixture/input/keep.png") -or (Test-Path "$fixture/input/Images")) { throw 'Installed dry run failed' }
    $gui = Start-Process -FilePath "$install/FileOrganizer.Configurator.exe" -ArgumentList @('--config', "`"$config`"") -WindowStyle Hidden -PassThru
    try {
        if (!$gui.WaitForInputIdle(10000)) { throw 'Installed GUI did not reach its message loop' }
        $gui.Refresh()
        if ($gui.HasExited -or $gui.MainWindowHandle -eq [IntPtr]::Zero) { throw 'Installed GUI did not open its window' }
        $deadline = [Diagnostics.Stopwatch]::StartNew()
        while (!$gui.HasExited -and $deadline.Elapsed.TotalSeconds -lt 10) {
            $gui.Refresh()
            # WPF can reach its message loop while its asynchronous configuration load is still busy.
            if ($gui.MainWindowTitle.Contains($config)) { $null = $gui.CloseMainWindow() }
            $null = $gui.WaitForExit(200)
        }
        if (!$gui.HasExited) { throw 'Installed GUI did not load its config and close normally' }
        if ($gui.ExitCode -ne 0) { throw 'Installed GUI failed' }
    }
    finally { if (!$gui.HasExited) { Stop-Process -Id $gui.Id -Force }; $gui.Dispose() }
    $hash = (Get-FileHash -LiteralPath $config).Hash
    $registry = [Microsoft.Win32.Registry]::CurrentUser.CreateSubKey($runKey)
    try {
        $command = '"' + "$install\engine\FileOrganizer.exe" + '" run --config "' + $config + '"'
        $registry.SetValue($ownedName, $command)
        $registry.SetValue($otherName, '"C:\Unrelated\FileOrganizer.exe" run --config "C:\other.toml"')
        $registry.SetValue($foreignName, $command)
    }
    finally { $registry.Dispose() }
    # A reinstall exercises the same stable AppId and must not replace user TOML or autorun choices.
    Invoke-Setup $installer $arguments
    if ((Get-FileHash -LiteralPath $config).Hash -ne $hash) { throw 'Upgrade changed user configuration' }
    $registry = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey($runKey)
    try {
        $expectedCommand = '"' + "$install\engine\FileOrganizer.exe" + '" run --background --config "' + $config + '"'
        if ($registry.GetValue($ownedName) -ne $expectedCommand) { throw 'Upgrade did not migrate autorun safely' }
    }
    finally { $registry.Dispose() }
    Invoke-Setup "$install/unins000.exe" @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', "/LOG=`"$fixture/uninstall.log`"")
    if ((Test-Path "$install/engine/FileOrganizer.exe") -or (Test-Path "$install/FileOrganizer.Configurator.exe") -or (Test-Path -LiteralPath $menu)) { throw 'Uninstall left application files/shortcuts' }
    if ((Get-FileHash -LiteralPath $config).Hash -ne $hash -or !(Test-Path "$fixture/input/keep.png")) { throw 'Uninstall changed user data' }
    $registry = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey($runKey)
    try {
        if ($registry.GetValue($ownedName)) { throw 'Uninstall left owned autorun' }
        if (!$registry.GetValue($otherName) -or !$registry.GetValue($foreignName)) { throw 'Uninstall removed unrelated autorun' }
    }
    finally { $registry.Dispose() }
    Write-Output "Installer verification passed: install, shortcuts, engine, dry run, GUI startup, upgrade, config retention, uninstall and scoped autorun cleanup. Logs: $fixture"
}
finally {
    $registry = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey($runKey, $true)
    if ($registry) {
        try { foreach ($name in @($ownedName, $otherName, $foreignName)) { $registry.DeleteValue($name, $false) } }
        finally { $registry.Dispose() }
    }
    if (Test-Path "$install/unins000.exe") {
        Invoke-Setup "$install/unins000.exe" @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART')
    }
}
