using FileOrganizer.Core;
using FileOrganizer.Windows;
using Xunit;

namespace FileOrganizer.Tests;

[Collection("User instance lease")]
public sealed class RuntimeTests
{
    [WindowsFact]
    public async Task T20_T22_RealWatcherCoalescesAndProcessesRenamedDownload()
    {
        using Fixture f = new();
        Configuration config = f.Config(general: "scan_on_startup=false");
        File.WriteAllText(f.ConfigPath, f.Text(general: "scan_on_startup=false"));
        await using WorkerHost host = new(config, f.ConfigPath, f.Log());
        using CancellationTokenSource stop = new();
        Task running = host.RunAsync(stop.Token);
        try
        {
            string temp = f.File("download.part");
            Assert.Null(Matching.First(config.Folders[0], temp));
            Assert.Null(Matching.First(config.Folders[0], "download.tmp"));
            Assert.Null(Matching.First(config.Folders[0], "download.crdownload"));
            string final = Path.ChangeExtension(temp, ".txt");
            File.Move(temp, final);
            for (int i = 0; i < 10; i++) File.SetLastWriteTimeUtc(final, DateTime.UtcNow);
            await Fixture.Eventually(() => File.Exists(Path.Combine(f.Destination, "download.txt")) && !File.Exists(final));
            Assert.Single(Directory.GetFiles(f.Destination));
        }
        finally { stop.Cancel(); await running; }
    }
    [WindowsFact]
    public async Task T21_T24_ExplicitReconciliationAndStartupToggle()
    {
        using Fixture f = new();
        string existing = f.File();
        Configuration config = f.Config(general: "scan_on_startup=false");
        File.WriteAllText(f.ConfigPath, f.Text(general: "scan_on_startup=false"));
        await using WorkerHost host = new(config, f.ConfigPath, f.Log());
        using CancellationTokenSource stop = new();
        Task running = host.RunAsync(stop.Token);
        try
        {
            Assert.True(File.Exists(existing));
            host.HandleWatcherError(new InternalBufferOverflowException("synthetic overflow"));
            host.Reconcile();
            await Fixture.Eventually(() => File.Exists(Path.Combine(f.Destination, "hello.txt")));
            Assert.Single(Directory.GetFiles(f.Destination));
            Assert.True(host.WatcherRecoveries > 0);
        }
        finally { stop.Cancel(); await running; }
    }
    [WindowsFact]
    public async Task T06_ConfigReloadValidAndInvalidKeepsSnapshot()
    {
        using Fixture f = new();
        string original = f.Text(general: "scan_on_startup=false");
        File.WriteAllText(f.ConfigPath, original);
        Configuration config = ConfigurationLoader.Load(f.ConfigPath);
        await using WorkerHost host = new(config, f.ConfigPath, f.Log());
        using CancellationTokenSource stop = new();
        Task running = host.RunAsync(stop.Token);
        try
        {
            File.WriteAllText(f.ConfigPath, original.Replace("[general]", "[general]\ndry_run=true"));
            await Fixture.Eventually(() => host.Current.General.DryRun);
            Configuration good = host.Current;
            File.WriteAllText(f.ConfigPath, "version = [invalid");
            // A source event plus reconciliation exercises the retained snapshot.
            string file = f.File();
            host.Reconcile();
            await Fixture.Eventually(() => host.RejectedConfigurations > 0);
            Assert.Same(good, host.Current);
            Assert.True(File.Exists(file)); Assert.False(Directory.Exists(f.Destination));
        }
        finally { stop.Cancel(); await running; }
    }
    [WindowsFact]
    public async Task T22_T23_LockedReadinessTimesOutThenSucceeds()
    {
        using Fixture f = new();
        string source = f.File();
        using (FileStream writer = new(source, FileMode.Open, FileAccess.Write, FileShare.None))
        {
            Assert.False(await Readiness.WaitAsync(source, TimeSpan.FromMilliseconds(150), default, TimeSpan.FromMilliseconds(25)));
            Assert.True(File.Exists(source));
        }
        Assert.True(await Readiness.WaitAsync(source, TimeSpan.FromSeconds(1), default, TimeSpan.FromMilliseconds(25)));
    }
    [WindowsFact]
    public async Task T24_OneShotScanRespectsRecursionAndIgnoresConfig()
    {
        using Fixture f = new();
        f.File();
        string child = Path.Combine(f.Source, "child");
        Directory.CreateDirectory(child);
        File.WriteAllText(Path.Combine(child, "nested.txt"), "nested");
        Configuration config = f.Config();
        await using WorkerHost host = new(config, f.ConfigPath, f.Log());
        Assert.Equal(0, await host.ScanAsync());
        Assert.True(File.Exists(Path.Combine(f.Destination, "hello.txt")));
        Assert.True(File.Exists(Path.Combine(child, "nested.txt")));
    }
    [WindowsFact]
    public void T27_AutostartIsReversibleAndQuotesPaths()
    {
        using Fixture f = new();
        FakeStartup store = new();
        string executable = Path.Combine(f.Root, "folder with spaces", "FileOrganizer.exe");
        StartupRegistration.Install(store, executable, f.ConfigPath);
        Assert.Equal($"\"{executable}\" run --background --config \"{f.ConfigPath}\"", store.Values[StartupRegistration.EntryName(f.ConfigPath)]);
        store.Values.Add("unrelated", "keep");
        StartupRegistration.Uninstall(store, f.ConfigPath);
        Assert.Single(store.Values); Assert.Equal("keep", store.Values["unrelated"]);
    }
    [WindowsFact]
    public void T28_LogFilteringAndBoundedRotation()
    {
        using Fixture f = new();
        string directory = Path.Combine(f.Root, "log");
        RotatingLog log = new(directory, LogLevel.Warning, 256, 2);
        log.Write(LogLevel.Debug, "hidden");
        Assert.False(Directory.Exists(directory));
        for (int i = 0; i < 15; i++) log.Write(LogLevel.Warning, new string('x', 120));
        Assert.InRange(Directory.GetFiles(directory).Length, 1, 3);
        Assert.All(Directory.GetFiles(directory), path => Assert.True(new FileInfo(path).Length <= 256));
    }
    [WindowsFact]
    public async Task T26_SingleInstanceAcrossConfigsAndSafeStop()
    {
        if (!OperatingSystem.IsWindows()) return;
        using Fixture f = new();
        using (InstanceLease first = new(f.ConfigPath))
        {
            Assert.Throws<IOException>(() => new InstanceLease(f.ConfigPath.ToUpperInvariant()));
            Assert.Throws<IOException>(() => new InstanceLease(Path.Combine(f.Root, "different.toml")));
            Assert.True(InstanceLease.RequestStop());
            await Fixture.Eventually(() => first.StopRequested.IsCancellationRequested);
        }
        using (InstanceLease reopened = new(f.ConfigPath)) Assert.False(reopened.StopRequested.IsCancellationRequested);
        Assert.False(InstanceLease.RequestStop());
        string legacyName = @"Local\FileOrganizer-" + System.Security.Principal.WindowsIdentity.GetCurrent().User!.Value + "-" + StartupRegistration.Identity(f.ConfigPath);
        using Mutex legacy = new(false, legacyName);
        Assert.True(legacy.WaitOne(0));
        try { Assert.Throws<IOException>(() => new InstanceLease(f.ConfigPath)); }
        finally { legacy.ReleaseMutex(); }
    }
    [WindowsFact]
    public async Task T17_DirectoryCannotBeRedirectedDuringCommit()
    {
        using Fixture f = new();
        string source = f.File();
        await f.Move(f.Config(), source, hook: () =>
            Assert.Throws<IOException>(() => Directory.Move(f.Destination, f.Destination + ".redirected")));
        Assert.True(File.Exists(Path.Combine(f.Destination, "hello.txt")));
    }
    [WindowsFact]
    public async Task T21_SourceDeletedAndReinstatedRecovers()
    {
        using Fixture f = new();
        File.WriteAllText(f.ConfigPath, f.Text(general: "scan_on_startup=false"));
        await using WorkerHost host = new(f.Config(general: "scan_on_startup=false"), f.ConfigPath, f.Log());
        using CancellationTokenSource stop = new();
        Task running = host.RunAsync(stop.Token);
        try
        {
            Directory.Delete(f.Source);
            Directory.CreateDirectory(f.Source);
            f.File();
            await Fixture.Eventually(() => host.WatcherRecoveries > 0, 15000);
            await Fixture.Eventually(() => File.Exists(Path.Combine(f.Destination, "hello.txt")));
        }
        finally { stop.Cancel(); await running; }
    }
    [WindowsFact]
    public async Task T26_CliHelpValidateAndInvalidConfig()
    {
        using Fixture f = new();
        f.File();
        File.WriteAllText(f.ConfigPath, f.Text());
        Assert.Equal(0, await Program.Main(["--help"]));
        Assert.Equal(0, await Program.Main(["validate", "--config", f.ConfigPath]));
        Assert.True(File.Exists(Path.Combine(f.Source, "hello.txt")));
        File.WriteAllText(f.ConfigPath, "version=8");
        Assert.Equal(2, await Program.Main(["validate", "--config", f.ConfigPath]));
        Assert.Equal(2, await Program.Main(["scan", "--unknown"]));
    }
    private sealed class FakeStartup : IStartupStore
    {
        public Dictionary<string, string> Values { get; } = [];
        public void Set(string name, string command) => Values[name] = command;
        public void Remove(string name) => Values.Remove(name);
    }
}
