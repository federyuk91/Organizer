using FileOrganizer.Windows;
using Xunit;

namespace FileOrganizer.Tests;

[Collection("User instance lease")]
public sealed class EngineScanTests
{
    private static string Engine => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
        "../../../../../src/FileOrganizer/bin", new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name, "net10.0/FileOrganizer.exe"));
    [WindowsFact]
    public async Task LaunchesOneShotCliWithQuotedPathsAndPreservesDryRunFiles()
    {
        using Fixture f = new(); string config = Path.Combine(f.Root, "settings with spaces.toml");
        File.WriteAllText(config, f.Text(general: "dry_run=true\nlog_level='info'")); string original = f.File("50% image.txt");
        ScanResult result = await EngineScan.RunAsync(Engine, config);
        Assert.Equal(0, result.ExitCode); Assert.Contains("Dry run:", result.Error); Assert.Empty(result.Output);
        Assert.True(File.Exists(original)); Assert.False(Directory.Exists(f.Destination)); Assert.False(InstanceLease.IsWorkerRunning());
    }
    [WindowsFact]
    public async Task CancellationRequestsSafeStopAndWaitsForEngineExit()
    {
        using Fixture f = new(); File.WriteAllText(f.ConfigPath, f.Text(general: "dry_run=true").Replace("timeout_seconds = 2", "timeout_seconds = 20"));
        string source = f.File(); using FileStream locked = new(source, FileMode.Open, FileAccess.Write, FileShare.None);
        using CancellationTokenSource stop = new(); Task<ScanResult> scanning = EngineScan.RunAsync(Engine, f.ConfigPath, stop.Token);
        await Fixture.Eventually(InstanceLease.IsWorkerRunning);
        stop.Cancel(); ScanResult result = await scanning.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(130, result.ExitCode); Assert.True(File.Exists(source)); Assert.False(InstanceLease.IsWorkerRunning());
    }
    [WindowsFact]
    public async Task ReportsCliFailuresAndPreStartCancellationWithoutOrphaningWorkers()
    {
        using Fixture f = new(); File.WriteAllText(f.ConfigPath, "version=[broken");
        ScanResult result = await EngineScan.RunAsync(Engine, f.ConfigPath); Assert.Equal(2, result.ExitCode); Assert.NotEmpty(result.Error);
        using CancellationTokenSource cancelled = new(); cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => EngineScan.RunAsync(Engine, f.ConfigPath, cancelled.Token));
        Assert.False(InstanceLease.IsWorkerRunning());
    }
}
