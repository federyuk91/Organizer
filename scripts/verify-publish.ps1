param([ValidateSet('Aot', 'Standard')][string]$Mode = 'Aot', [int]$IdleSeconds = 10)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$exe = Join-Path $root "artifacts/publish/$($Mode.ToLowerInvariant())/FileOrganizer.exe"
$fixture = Join-Path $root "artifacts/verification/$($Mode.ToLowerInvariant())-$([Guid]::NewGuid().ToString('N'))"
$process = $null
try {
    $source = Join-Path $fixture 'input'
    $desktop = Join-Path $fixture 'desktop'
    New-Item -ItemType Directory -Force $source, $desktop | Out-Null
    $config = Join-Path $fixture 'config.toml'
    $text = [IO.File]::ReadAllText((Join-Path $root 'Config.Example.toml'))
    $text = $text.Replace('%USERPROFILE%\Downloads', $source).Replace('%USERPROFILE%\Desktop', $desktop)
    $text = $text.Replace('dry_run = false', 'dry_run = true').Replace('scan_on_startup = true', 'scan_on_startup = false')
    [IO.File]::WriteAllText($config, $text)
    & $exe --help
    if ($LASTEXITCODE -ne 0) { throw 'Help failed' }
    & $exe validate --config $config
    if ($LASTEXITCODE -ne 0) { throw 'Sample validation failed' }
    [IO.File]::WriteAllText((Join-Path $source 'test.png'), 'preserve me')
    & $exe scan --config $config
    if ($LASTEXITCODE -ne 0) { throw 'Dry-run scan failed' }
    if (!(Test-Path (Join-Path $source 'test.png')) -or (Test-Path (Join-Path $source 'Images'))) { throw 'Dry run mutated files' }
    $process = Start-Process -FilePath $exe -ArgumentList @('run', '--background', '--config', "`"$config`"") -WindowStyle Hidden -PassThru -RedirectStandardError (Join-Path $fixture 'stderr.txt') -RedirectStandardOutput (Join-Path $fixture 'stdout.txt')
    # Warm up only the verification process; production has no idle polling loop.
    Start-Sleep -Seconds 2
    $process.Refresh()
    if ($process.HasExited) { throw 'Worker exited during startup' }
    $otherConfig = Join-Path $fixture 'different-config.toml'
    Copy-Item -LiteralPath $config -Destination $otherConfig
    $duplicate = Start-Process -FilePath $exe -ArgumentList @('run', '--config', "`"$otherConfig`"") -WindowStyle Hidden -PassThru -RedirectStandardError (Join-Path $fixture 'duplicate.txt')
    if (!$duplicate.WaitForExit(5000)) { Stop-Process -Id $duplicate.Id -Force; throw 'Second instance was not refused' }
    if ($duplicate.ExitCode -ne 3) { throw 'Second instance returned an unexpected exit code' }
    $cpuBefore = $process.TotalProcessorTime.TotalMilliseconds
    $clock = [Diagnostics.Stopwatch]::StartNew()
    Start-Sleep -Seconds $IdleSeconds
    $process.Refresh()
    $elapsed = $clock.Elapsed.TotalMilliseconds
    $result = [ordered]@{
        Mode = $Mode
        ExecutableBytes = (Get-Item $exe).Length
        DeploymentBytes = (Get-ChildItem (Split-Path $exe) -File | Measure-Object Length -Sum).Sum
        IdleSampleSeconds = [Math]::Round($elapsed / 1000, 3)
        IdleCpuMilliseconds = $process.TotalProcessorTime.TotalMilliseconds - $cpuBefore
        IdleOneCorePercent = [Math]::Round(100 * ($process.TotalProcessorTime.TotalMilliseconds - $cpuBefore) / $elapsed, 4)
        WorkingSetBytes = $process.WorkingSet64
    }
    $result | ConvertTo-Json | Tee-Object -FilePath (Join-Path $root "artifacts/verification-$($Mode.ToLowerInvariant()).json")
    & $exe stop
    if ($LASTEXITCODE -ne 0 -or !$process.WaitForExit(10000)) { throw 'Graceful background shutdown failed' }
    if ($process.ExitCode -notin @(0,130)) { throw 'Background worker failed during shutdown' }
}
finally {
    if ($process -and !$process.HasExited) { Stop-Process -Id $process.Id -Force; $process.WaitForExit() }
    # Only this freshly created, resolved fixture beneath artifacts/verification is removed.
    $verifiedRoot = [IO.Path]::GetFullPath((Join-Path $root 'artifacts/verification')) + [IO.Path]::DirectorySeparatorChar
    $verifiedFixture = [IO.Path]::GetFullPath($fixture)
    if (!$verifiedFixture.StartsWith($verifiedRoot, [StringComparison]::OrdinalIgnoreCase)) { throw 'Unsafe fixture cleanup path' }
    if (Test-Path -LiteralPath $verifiedFixture) { Remove-Item -LiteralPath $verifiedFixture -Recurse -Force }
}
