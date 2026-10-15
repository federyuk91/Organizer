using FileOrganizer.Core;
using FileOrganizer.Windows;
using Xunit;

namespace FileOrganizer.Tests;

public sealed class ReclassificationTests
{
    private static string Images(Fixture f) => Path.Combine(f.Source, "Images");
    private static string Cats(Fixture f) => Path.Combine(Images(f), "Cats");
    private static string Rule(string name, string destination, string? pattern = null, string extra = "") => $$"""
        [[folders.rules]]
        name='{{name}}'
        extensions=['.png']
        destination='{{destination}}'
        {{(pattern is null ? "" : "patterns=['" + pattern + "']")}}
        {{extra}}

        """;
    private static string Config(Fixture f, string rules, string general = "", string duplicates = "") => $$"""
        version=1
        [general]
        scan_on_startup=false
        file_ready_timeout_seconds=2
        {{general}}
        [[folders]]
        name='Downloads'
        path='{{f.Source}}'
        recursive=false
        [folders.duplicates]
        {{duplicates}}
        {{rules}}
        """;
    private static string Put(string directory, string name, string text = "data")
    { Directory.CreateDirectory(directory); string path = Path.Combine(directory, name); File.WriteAllText(path, text); return path; }
    private static void Save(string path, string text)
    { ConfigurationFile file = new(path); file.Open(); file.Save(text); }

    [WindowsFact]
    public async Task AddedHigherPriorityRuleReclassifiesPreviouslyOrganizedAndManualFiles()
    {
        using Fixture f = new();
        string original = Config(f, Rule("Images", Images(f)));
        File.WriteAllText(f.ConfigPath, original);
        Put(Images(f), "gatto.png"); Put(Images(f), "dog.png");
        Put(Path.Combine(Images(f), "manual"), "gattino.png");
        await using WorkerHost host = new(ConfigurationLoader.Load(f.ConfigPath), f.ConfigPath, f.Log());
        using CancellationTokenSource stop = new(); Task run = host.RunAsync(stop.Token);
        try
        {
            Save(f.ConfigPath, Config(f, Rule("Cats", Cats(f), "*gatt*") + Rule("Images", Images(f))));
            await Fixture.Eventually(() => File.Exists(Path.Combine(Cats(f), "gatto.png")));
            Assert.True(File.Exists(Path.Combine(Images(f), "manual", "gattino.png")));
            Assert.True(File.Exists(Path.Combine(Images(f), "dog.png")));
            Assert.False(File.Exists(Path.Combine(Images(f), "gatto.png")));
            Assert.Equal(0, host.Failures);
        }
        finally { stop.Cancel(); await run; }
    }
    [WindowsFact]
    public async Task ReorderingRulesMovesFilesBothDirectionsWithoutCopiesOrSelfDeletion()
    {
        using Fixture f = new();
        string imagesFirst = Config(f, Rule("Images", Images(f)) + Rule("Cats", Cats(f), "*gatt*"), duplicates: "identical_content='delete'");
        string catsFirst = Config(f, Rule("Cats", Cats(f), "*gatt*") + Rule("Images", Images(f)), duplicates: "identical_content='delete'");
        File.WriteAllText(f.ConfigPath, imagesFirst); Put(Images(f), "gatto.png");
        await using WorkerHost host = new(ConfigurationLoader.Load(f.ConfigPath), f.ConfigPath, f.Log());
        using CancellationTokenSource stop = new(); Task run = host.RunAsync(stop.Token);
        try
        {
            Save(f.ConfigPath, catsFirst);
            await Fixture.Eventually(() => File.Exists(Path.Combine(Cats(f), "gatto.png")));
            Save(f.ConfigPath, imagesFirst);
            await Fixture.Eventually(() => File.Exists(Path.Combine(Images(f), "gatto.png")));
            Assert.Single(Directory.GetFiles(Images(f), "*.png", SearchOption.AllDirectories));
            Assert.Equal("data", File.ReadAllText(Path.Combine(Images(f), "gatto.png")));
            Assert.Equal(0, host.Failures);
        }
        finally { stop.Cancel(); await run; }
    }
    [WindowsFact]
    public async Task ModifiedDestinationDrainsAndWatchesPreviousManagedDirectoryDuringSession()
    {
        using Fixture f = new();
        string oldRoot = Path.Combine(f.Root, "previous");
        string newRoot = Path.Combine(f.Root, "new", "Pictures");
        File.WriteAllText(f.ConfigPath, Config(f, Rule("Images", oldRoot)));
        Put(oldRoot, "existing.png");
        await using WorkerHost host = new(ConfigurationLoader.Load(f.ConfigPath), f.ConfigPath, f.Log());
        using CancellationTokenSource stop = new(); Task run = host.RunAsync(stop.Token);
        try
        {
            Save(f.ConfigPath, Config(f, Rule("Images", newRoot)));
            await Fixture.Eventually(() => File.Exists(Path.Combine(newRoot, "existing.png")));
            Put(oldRoot, "manual-after-change.png");
            await Fixture.Eventually(() => File.Exists(Path.Combine(newRoot, "manual-after-change.png")));
            Assert.Empty(Directory.GetFiles(oldRoot)); Assert.Equal(0, host.Failures);
        }
        finally { stop.Cancel(); await run; }
    }
    [WindowsFact]
    public async Task ManagedDestinationsAreWatchedWithNonRecursiveSourceAndNoStartupScan()
    {
        using Fixture f = new();
        Directory.CreateDirectory(Images(f)); Directory.CreateDirectory(Cats(f));
        File.WriteAllText(f.ConfigPath, Config(f, Rule("Cats", Cats(f), "*gatt*") + Rule("Images", Images(f))));
        await using WorkerHost host = new(ConfigurationLoader.Load(f.ConfigPath), f.ConfigPath, f.Log());
        using CancellationTokenSource stop = new(); Task run = host.RunAsync(stop.Token);
        try
        {
            Assert.Equal(4, host.WatcherCount); // Source, Images, Cats and shared parent guard.
            Put(Images(f), "manual-gatto.png");
            await Fixture.Eventually(() => File.Exists(Path.Combine(Cats(f), "manual-gatto.png")));
            string staging = Path.Combine(f.Root, "staging"); Put(staging, "gattino.png");
            Directory.Move(staging, Path.Combine(Images(f), "imported"));
            await Task.Delay(500);
            Assert.True(File.Exists(Path.Combine(Images(f), "imported", "gattino.png")));
            Assert.False(File.Exists(Path.Combine(Cats(f), "gattino.png")));
            string ignored = Put(Path.Combine(f.Source, "unmanaged"), "gatto.png");
            Assert.True(File.Exists(ignored)); Assert.Equal(0, host.Failures);
        }
        finally { stop.Cancel(); await run; }
    }
    [WindowsFact]
    public async Task MissingExternalDestinationGetsAnEventDrivenWatchWhenCreatedManually()
    {
        using Fixture f = new();
        string root = Path.Combine(f.Root, "new", "external", "Images");
        File.WriteAllText(f.ConfigPath, Config(f, Rule("Cats", Path.Combine(root, "Cats"), "*gatt*") + Rule("Images", root)));
        await using WorkerHost host = new(ConfigurationLoader.Load(f.ConfigPath), f.ConfigPath, f.Log());
        using CancellationTokenSource stop = new(); Task run = host.RunAsync(stop.Token);
        try
        {
            Put(root, "gatto.png");
            await Fixture.Eventually(() => File.Exists(Path.Combine(root, "Cats", "gatto.png")));
            Put(root, "second-gatto.png");
            await Fixture.Eventually(() => File.Exists(Path.Combine(root, "Cats", "second-gatto.png")));
        }
        finally { stop.Cancel(); await run; }
    }
    [WindowsTheory]
    [InlineData("copy")]
    [InlineData("skip")]
    [InlineData("delete")]
    public async Task AlreadyCorrectFilesAreNoOpsBeforeDuplicatePolicy(string policy)
    {
        using Fixture f = new();
        Configuration config = ConfigurationLoader.Parse(Config(f, Rule("Images", Images(f)), duplicates: "identical_content='" + policy + "'"));
        string path = Put(Images(f), "correct.png");
        SafeTransfer transfer = new((_, _) => throw new InvalidOperationException("A no-op must not log a move"));
        Assert.Equal(TransferOutcome.AlreadyPlaced, await transfer.ExecuteAsync(config, config.Folders[0], config.Folders[0].Rules[0], path));
        Assert.Equal("data", File.ReadAllText(path)); Assert.Single(Directory.GetFiles(Images(f)));
    }
    [WindowsTheory]
    [InlineData("copy", "data", "gatto_copy_1.png", false)]
    [InlineData("skip", "data", "", true)]
    [InlineData("delete", "data", "", false)]
    [InlineData("copy", "other", "gatto_1.png", false)]
    public async Task ReclassificationReusesCollisionAndIdenticalPolicies(string policy, string existing, string expected, bool retained)
    {
        using Fixture f = new();
        string text = Config(f, Rule("Cats", Cats(f), "*gatt*") + Rule("Images", Images(f)), duplicates: "identical_content='" + policy + "'");
        string source = Put(Images(f), "gatto.png"); Put(Cats(f), "gatto.png", existing);
        await using WorkerHost host = new(ConfigurationLoader.Parse(text), f.ConfigPath, f.Log());
        Assert.Equal(0, await host.ScanAsync());
        Assert.Equal(retained, File.Exists(source));
        Assert.Equal(existing, File.ReadAllText(Path.Combine(Cats(f), "gatto.png")));
        if (expected.Length > 0) Assert.Equal("data", File.ReadAllText(Path.Combine(Cats(f), expected)));
    }
    [WindowsFact]
    public async Task ExclusionsUnmatchedTemporaryFilesAndDryRunAreRespected()
    {
        using Fixture f = new();
        string text = Config(f, Rule("Cats", Cats(f), "*gatt*", "exclude_patterns=['*_draft.png']") + Rule("Images", Images(f)), general: "dry_run=true");
        Put(Images(f), "gatto.png"); Put(Images(f), "gatto_draft.png"); Put(Images(f), "notes.md"); Put(Images(f), "gatto.part");
        await using (WorkerHost dry = new(ConfigurationLoader.Parse(text), f.ConfigPath, f.Log())) Assert.Equal(0, await dry.ScanAsync());
        Assert.False(Directory.Exists(Cats(f))); Assert.Equal(4, Directory.GetFiles(Images(f)).Length);
        await using (WorkerHost live = new(ConfigurationLoader.Parse(text.Replace("dry_run=true", "dry_run=false")), f.ConfigPath, f.Log())) Assert.Equal(0, await live.ScanAsync());
        Assert.True(File.Exists(Path.Combine(Cats(f), "gatto.png")));
        Assert.True(File.Exists(Path.Combine(Images(f), "gatto_draft.png")));
        Assert.True(File.Exists(Path.Combine(Images(f), "notes.md"))); Assert.True(File.Exists(Path.Combine(Images(f), "gatto.part")));
    }
    [WindowsFact]
    public async Task CollisionNameThatWouldChangeRoutingFailsClosedInsteadOfLooping()
    {
        using Fixture f = new();
        string text = Config(f, Rule("Exact", Cats(f), "gatto.png") + Rule("Images", Images(f)));
        string source = Put(Images(f), "gatto.png"); Put(Cats(f), "gatto.png", "other");
        Configuration config = ConfigurationLoader.Parse(text);
        SafeTransfer transfer = new((_, _) => { });
        await Assert.ThrowsAsync<ConfigException>(() => transfer.ExecuteAsync(config, config.Folders[0], config.Folders[0].Rules[0], source));
        Assert.True(File.Exists(source)); Assert.Single(Directory.GetFiles(Cats(f)));
    }
    [WindowsFact]
    public void OverlappingDestinationsAcrossMonitorsAreRejectedAndOwnershipIsExplicit()
    {
        using Fixture f = new(); string second = Path.Combine(f.Root, "second"); Directory.CreateDirectory(second);
        string extra = $"\n[[folders]]\nname='second'\npath='{second}'\nrecursive=true\n";
        Assert.Throws<ConfigException>(() => ConfigurationLoader.Parse(Config(f, Rule("Images", Images(f))) + extra + Rule("Other", Cats(f))));
        string owned = Path.Combine(second, "owned-by-first"); string file = Put(owned, "gatto.png");
        Configuration config = ConfigurationLoader.Parse(Config(f, Rule("Images", owned)) + extra + Rule("Other", Path.Combine(f.Root, "other-out")));
        ManagedDirectories scope = new(config);
        Assert.Same(config.Folders[0], scope.Owner(file));
        Assert.Single(scope.Snapshot(config.Folders[0])); Assert.Empty(scope.Snapshot(config.Folders[1]));
    }
    [WindowsFact]
    public async Task ExternalManagedRootRenameAndRecreationRestoreWatching()
    {
        using Fixture f = new(); string external = Path.Combine(f.Root, "ExternalImages");
        Put(external, "dog.png");
        File.WriteAllText(f.ConfigPath, Config(f, Rule("Cats", Path.Combine(external, "Cats"), "*gatt*") + Rule("Images", external)));
        await using WorkerHost host = new(ConfigurationLoader.Load(f.ConfigPath), f.ConfigPath, f.Log());
        using CancellationTokenSource stop = new(); Task run = host.RunAsync(stop.Token);
        try
        {
            string removed = Path.Combine(f.Root, "outside-current-scope"); Directory.Move(external, removed);
            Put(external, "gatto.png");
            await Fixture.Eventually(() => File.Exists(Path.Combine(external, "Cats", "gatto.png")));
            Assert.True(File.Exists(Path.Combine(removed, "dog.png"))); Assert.Equal(0, host.Failures);
        }
        finally { stop.Cancel(); await run; }
    }
    [WindowsFact]
    public async Task InFlightOldGenerationIsRescheduledUsingNewRulePriority()
    {
        using Fixture f = new();
        string original = Config(f, Rule("Images", Images(f))).Replace("scan_on_startup=false", "scan_on_startup=true").Replace("timeout_seconds=2", "timeout_seconds=5");
        File.WriteAllText(f.ConfigPath, original); string source = Put(f.Source, "gatto.png");
        using FileStream writer = new(source, FileMode.Open, FileAccess.Write, FileShare.None);
        await using WorkerHost host = new(ConfigurationLoader.Load(f.ConfigPath), f.ConfigPath, f.Log());
        using CancellationTokenSource stop = new(); Task run = host.RunAsync(stop.Token);
        try
        {
            await Fixture.Eventually(() => host.Reconciliations >= 1);
            Save(f.ConfigPath, Config(f, Rule("Cats", Cats(f), "*gatt*") + Rule("Images", Images(f))).Replace("timeout_seconds=2", "timeout_seconds=5"));
            await Fixture.Eventually(() => host.Current.Folders[0].Rules[0].Name == "Cats");
            writer.Dispose();
            await Fixture.Eventually(() => File.Exists(Path.Combine(Cats(f), "gatto.png")));
            Assert.False(File.Exists(Path.Combine(Images(f), "gatto.png"))); Assert.Equal(0, host.Failures);
        }
        finally { writer.Dispose(); stop.Cancel(); await run; }
    }
    [WindowsFact]
    public async Task ManagedTraversalDoesNotFollowNestedJunctions()
    {
        using Fixture f = new();
        string outside = Path.Combine(f.Root, "outside"); string original = Put(outside, "gatto.png");
        Directory.CreateDirectory(Images(f)); string junction = Path.Combine(Images(f), "escape");
        System.Diagnostics.ProcessStartInfo start = new("cmd.exe") { CreateNoWindow = true, RedirectStandardOutput = true };
        foreach (string arg in new[] { "/c", "mklink", "/J", junction, outside }) start.ArgumentList.Add(arg);
        using System.Diagnostics.Process process = System.Diagnostics.Process.Start(start)!;
        process.WaitForExit(); Assert.Equal(0, process.ExitCode);
        try
        {
            string text = Config(f, Rule("Cats", Cats(f), "*gatt*") + Rule("Images", Images(f)));
            await using WorkerHost host = new(ConfigurationLoader.Parse(text), f.ConfigPath, f.Log());
            Assert.Equal(0, await host.ScanAsync()); Assert.True(File.Exists(original)); Assert.False(Directory.Exists(Cats(f)));
        }
        finally { Directory.Delete(junction); }
    }
    [WindowsFact]
    public void SessionDestinationHistoryCannotAcquireConflictingOwnership()
    {
        using Fixture f = new();
        string prior = Path.Combine(f.Root, "prior");
        ManagedDirectories old = new(ConfigurationLoader.Parse(Config(f, Rule("Images", prior))));
        string second = Path.Combine(f.Root, "second"); Directory.CreateDirectory(second);
        string next = Config(f, Rule("Images", Images(f))) + $"\n[[folders]]\nname='second'\npath='{second}'\n" + Rule("Other", prior);
        Configuration config = ConfigurationLoader.Parse(next);
        Assert.Throws<ConfigException>(() => new ManagedDirectories(config, old));
    }
    [WindowsFact]
    public async Task RepeatedReconciliationIsIdempotentAndHealthyIdleHasNoPolling()
    {
        using Fixture f = new(); Put(Cats(f), "gatto.png");
        File.WriteAllText(f.ConfigPath, Config(f, Rule("Cats", Cats(f), "*gatt*") + Rule("Images", Images(f))));
        await using WorkerHost host = new(ConfigurationLoader.Load(f.ConfigPath), f.ConfigPath, f.Log());
        using CancellationTokenSource stop = new(); Task run = host.RunAsync(stop.Token);
        try
        {
            host.Reconcile(); await Fixture.Eventually(() => host.Reconciliations >= 1);
            int count = host.Reconciliations;
            // A timed observation is intentional here: assert the absence of periodic work.
            await Task.Delay(1200);
            Assert.Equal(count, host.Reconciliations);
            host.Reconcile(); await Fixture.Eventually(() => host.Reconciliations > count);
            Assert.Single(Directory.GetFiles(Images(f), "*.png", SearchOption.AllDirectories)); Assert.Equal(0, host.Failures);
        }
        finally { stop.Cancel(); await run; }
    }
}
