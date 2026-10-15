using System.Text.Json;
using FileOrganizer.Core;
using FileOrganizer.Windows;
using Xunit;

namespace FileOrganizer.Tests;

[Collection("User instance lease")]
public sealed class MoveHistoryTests
{
    private static MoveHistory History(Fixture f, Func<DateTimeOffset>? clock = null) => new(Path.Combine(f.Root, "history"), clock);
    private static Task<TransferOutcome> Move(Fixture f, MoveHistory history, Configuration config, string source, bool copy = false, Action? hook = null, ManagedDirectories? managed = null) =>
        new SafeTransfer((_, _) => { }, history, f.ConfigPath).ExecuteAsync(config, config.Folders[0], config.Folders[0].Rules[0], source, forceCopy: copy, beforeCommit: hook, managed: managed);

    [WindowsTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PersistentHistoryRestoresOriginalNameAndBytes(bool forceCopy)
    {
        using Fixture f = new(); MoveHistory history = History(f);
        Directory.CreateDirectory(f.Destination); File.WriteAllText(Path.Combine(f.Destination, "hello.txt"), "data");
        string original = f.File();
        Assert.Equal(TransferOutcome.Moved, await Move(f, history, f.Config(), original, forceCopy));
        MoveHistory reopened = History(f); MoveRecord entry = Assert.Single(reopened.Read(f.ConfigPath));
        Assert.EndsWith("hello_copy_1.txt", entry.Destination); Assert.Equal("Committed", entry.State);
        Assert.True(Assert.Single(await reopened.UndoAsync([entry.Id], f.ConfigPath)).Success);
        Assert.Equal("data", File.ReadAllText(original)); Assert.False(File.Exists(entry.Destination));
        Assert.Equal("data", File.ReadAllText(Path.Combine(f.Destination, "hello.txt")));
        Assert.Equal("Undone", Assert.Single(reopened.Read()).State);
        Assert.False(Assert.Single(await reopened.UndoAsync([entry.Id], f.ConfigPath)).Success);
    }
    [WindowsTheory]
    [InlineData("content")]
    [InlineData("replacement")]
    [InlineData("collision")]
    [InlineData("missing")]
    public async Task ModifiedMissingReplacedOrOccupiedFilesArePreserved(string failure)
    {
        using Fixture f = new(); MoveHistory history = History(f); string source = f.File();
        await Move(f, history, f.Config(), source);
        MoveRecord entry = Assert.Single(history.Read()); DateTime written = File.GetLastWriteTimeUtc(entry.Destination);
        if (failure == "content") { File.WriteAllText(entry.Destination, "evil"); File.SetLastWriteTimeUtc(entry.Destination, written); }
        if (failure == "replacement")
        {
            File.Move(entry.Destination, entry.Destination + ".old"); File.WriteAllText(entry.Destination, "data"); File.SetLastWriteTimeUtc(entry.Destination, written);
        }
        if (failure == "collision") File.WriteAllText(source, "keep original");
        if (failure == "missing") File.Delete(entry.Destination);
        Assert.False(Assert.Single(await history.UndoAsync([entry.Id], f.ConfigPath)).Success);
        Assert.Equal("Committed", Assert.Single(history.Read()).State);
        if (failure == "collision") Assert.Equal("keep original", File.ReadAllText(source));
        if (failure == "content") Assert.Equal("evil", File.ReadAllText(entry.Destination));
    }
    [WindowsFact]
    public async Task SevenDayRetentionDeletesOnlyMetadataAndFiltersConfig()
    {
        using Fixture f = new(); DateTimeOffset time = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);
        MoveHistory history = History(f, () => time); await Move(f, history, f.Config(), f.File());
        MoveRecord entry = Assert.Single(history.Read());
        Assert.Empty(history.Read(Path.Combine(f.Root, "other.toml")));
        time = time.AddDays(7); Assert.Single(history.Read());
        time = time.AddSeconds(1); Assert.Empty(history.Read());
        Assert.False(Assert.Single(await history.UndoAsync([entry.Id], f.ConfigPath)).Success);
        time = time.AddDays(1); File.WriteAllText(Path.Combine(history.DirectoryPath, "keep.txt"), "keep");
        Assert.Empty(history.Read()); Assert.Empty(Directory.GetFiles(history.DirectoryPath, "history-*.jsonl"));
        Assert.Equal("data", File.ReadAllText(entry.Destination)); Assert.True(File.Exists(Path.Combine(history.DirectoryPath, "keep.txt")));
    }
    [WindowsFact]
    public async Task BatchUndoesMovementChainsInReverseOrder()
    {
        using Fixture f = new(); DateTimeOffset time = DateTimeOffset.UtcNow;
        MoveHistory history = History(f, () => time); Configuration first = f.Config(); string source = f.File();
        await Move(f, history, first, source, true);
        string secondDestination = Path.Combine(f.Root, "third");
        Configuration second = ConfigurationLoader.Parse(f.Text().Replace(f.Destination, secondDestination));
        await Move(f, history, second, Path.Combine(f.Destination, "hello.txt"), true, managed: new ManagedDirectories(second, new ManagedDirectories(first)));
        IReadOnlyList<MoveRecord> entries = history.Read(); Assert.Equal(2, entries.Count);
        IReadOnlyList<UndoResult> results = await history.UndoAsync(entries.Reverse().Select(e => e.Id), f.ConfigPath);
        Assert.All(results, r => Assert.True(r.Success, r.Message)); Assert.Equal("data", File.ReadAllText(source));
        Assert.All(history.Read(), e => Assert.Equal("Undone", e.State));
    }
    [WindowsFact]
    public async Task JournalFailurePreventsTheMoveAndDryRunProducesNoHistory()
    {
        using Fixture f = new(); string source = f.File(); string path = Path.Combine(f.Root, "not-a-directory"); File.WriteAllText(path, "keep");
        await Assert.ThrowsAnyAsync<IOException>(() => Move(f, new MoveHistory(path), f.Config(), source));
        Assert.Equal("data", File.ReadAllText(source));
        MoveHistory history = History(f); await Move(f, history, f.Config(general: "dry_run=true"), source);
        Assert.False(Directory.Exists(history.DirectoryPath));
    }
    [WindowsFact]
    public async Task PreparedRecordRecoversACompletedRenameButNotAnUnmovedFile()
    {
        using Fixture f = new(); MoveHistory history = History(f); string source = f.File();
        await Move(f, history, f.Config(), source); MoveRecord entry = Assert.Single(history.Read());
        string journal = Assert.Single(Directory.GetFiles(history.DirectoryPath, "*.jsonl"));
        File.WriteAllText(journal, JsonSerializer.Serialize(entry with { State = "Prepared" }, OperationJson.Default.MoveRecord) + "\n");
        Assert.True(Assert.Single(await history.UndoAsync([entry.Id], f.ConfigPath)).Success);
        Assert.Equal("data", File.ReadAllText(source));
        string other = f.File("other.txt");
        await Assert.ThrowsAsync<IOException>(() => Move(f, history, f.Config(), other, hook: () => throw new IOException("before commit")));
        MoveRecord pending = history.Read().Single(e => e.Source == other);
        Assert.False(Assert.Single(await history.UndoAsync([pending.Id], f.ConfigPath)).Success);
        Assert.Equal("data", File.ReadAllText(other));
    }
    [SecondVolumeFact]
    public async Task HistorySupportsRealCrossVolumeUndo()
    {
        using Fixture f = new(); MoveHistory history = History(f);
        string basePath = Paths.Normalize(Environment.GetEnvironmentVariable("FILEORGANIZER_TEST_SECOND_VOLUME")!);
        string destination = Path.Combine(basePath, "FileOrganizer.Tests-" + Guid.NewGuid().ToString("N"));
        try
        {
            string source = f.File(contents: new string('a', 100000));
            Configuration config = ConfigurationLoader.Parse(f.Text().Replace(f.Destination, destination));
            await Move(f, history, config, source);
            MoveRecord entry = Assert.Single(history.Read());
            Assert.True(Assert.Single(await history.UndoAsync([entry.Id], f.ConfigPath)).Success);
            Assert.Equal(new string('a', 100000), File.ReadAllText(source)); Assert.False(File.Exists(entry.Destination));
        }
        finally
        {
            Assert.Equal(Path.TrimEndingDirectorySeparator(basePath), Path.TrimEndingDirectorySeparator(Path.GetDirectoryName(destination)!));
            if (Directory.Exists(destination)) Directory.Delete(destination, true);
        }
    }

    [WindowsFact]
    public async Task InterruptedUndoIsRecognizedWithoutMovingAgain()
    {
        using Fixture f = new(); MoveHistory history = History(f); string source = f.File();
        await Move(f, history, f.Config(), source); MoveRecord entry = Assert.Single(history.Read());
        Assert.True(Assert.Single(await history.UndoAsync([entry.Id], f.ConfigPath)).Success);
        MoveRecord undone = Assert.Single(history.Read());
        File.WriteAllText(Assert.Single(Directory.GetFiles(history.DirectoryPath, "*.jsonl")),
            JsonSerializer.Serialize(undone with { State = "UndoPrepared" }, OperationJson.Default.MoveRecord) + "\n");
        Assert.True(Assert.Single(await history.UndoAsync([entry.Id], f.ConfigPath)).Success);
        Assert.Equal("data", File.ReadAllText(source)); Assert.Equal("Undone", Assert.Single(history.Read()).State);
    }
    [WindowsFact]
    public async Task CancellationAndDamagedHistoryNeverMoveFiles()
    {
        using Fixture f = new(); MoveHistory history = History(f); string source = f.File();
        await Move(f, history, f.Config(), source); MoveRecord entry = Assert.Single(history.Read());
        using CancellationTokenSource cancelled = new(); cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => history.UndoAsync([entry.Id], f.ConfigPath, cancelled.Token));
        Assert.True(File.Exists(entry.Destination)); Assert.False(File.Exists(source));
        File.WriteAllText(Assert.Single(Directory.GetFiles(history.DirectoryPath, "*.jsonl")), "{}\n");
        Assert.Throws<IOException>(() => History(f).Read());
        string other = f.File("other.txt");
        await Assert.ThrowsAsync<HistoryUnavailableException>(() => Move(f, History(f), f.Config(), other));
        Assert.True(File.Exists(other)); Assert.False(File.Exists(Path.Combine(f.Destination, "other.txt")));
    }
    [WindowsFact]
    public async Task HistoryFailureStopsWorkerWithoutUntrackedMoves()
    {
        using Fixture f = new(); string first = f.File(), second = f.File("second.txt");
        string unavailable = Path.Combine(f.Root, "blocked-history"); File.WriteAllText(unavailable, "keep");
        await using WorkerHost host = new(f.Config(), f.ConfigPath, f.Log(), new MoveHistory(unavailable));
        Assert.Equal(3, await host.ScanAsync()); Assert.True(host.HistoryFailed);
        Assert.True(File.Exists(first)); Assert.True(File.Exists(second));
    }
    [WindowsFact]
    public async Task InterruptedTailIsIgnoredAndRepairedBeforeTheNextMove()
    {
        using Fixture f = new(); MoveHistory history = History(f); await Move(f, history, f.Config(), f.File());
        string path = Assert.Single(Directory.GetFiles(history.DirectoryPath, "*.jsonl"));
        File.AppendAllText(path, "{\"Version\":1,\"Id\":\"");
        Assert.Single(History(f).Read());
        MoveHistory reopened = History(f); await Move(f, reopened, f.Config(), f.File("other.txt"));
        Assert.Equal(2, reopened.Read().Count);
        Assert.All(await reopened.UndoAsync(reopened.Read().Select(e => e.Id), null), result => Assert.True(result.Success));
    }
    [WindowsFact]
    public async Task AllConfigurationsCanUndoAfterTheTomlWasRenamed()
    {
        using Fixture f = new(); MoveHistory history = History(f); string source = f.File();
        await Move(f, history, f.Config(), source); MoveRecord entry = Assert.Single(history.Read());
        string renamed = Path.Combine(f.Root, "renamed.toml");
        Assert.Empty(history.Read(renamed));
        Assert.False(Assert.Single(await history.UndoAsync([entry.Id], renamed)).Success);
        Assert.True(Assert.Single(await history.UndoAsync([entry.Id], null)).Success); Assert.Equal("data", File.ReadAllText(source));
    }
}
