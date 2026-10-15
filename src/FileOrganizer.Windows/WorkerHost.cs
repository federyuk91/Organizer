using System.Collections.Concurrent;
using System.Threading.Channels;
using FileOrganizer.Core;

namespace FileOrganizer.Windows;

public sealed class WorkerHost : IAsyncDisposable
{
    private sealed record Work(ManagedDirectories Scope, Folder Folder, string Path, bool DiscoverDirectory = false)
    { public Configuration Config => Scope.Configuration; }
    private readonly Channel<Work> queue = Channel.CreateBounded<Work>(new BoundedChannelOptions(256) { FullMode = BoundedChannelFullMode.Wait });
    private readonly Channel<bool> changes = Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });
    private readonly ConcurrentDictionary<string, ManagedDirectories> pending = new(Paths.Comparer);
    private readonly List<FileSystemWatcher> watchers = [];
    private readonly SemaphoreSlim scanGate = new(1);
    private readonly CancellationTokenSource lifetime = new();
    private readonly RotatingLog log;
    private readonly string configPath;
    private readonly MoveHistory? history;
    private readonly Action<string, Configuration>? accepted;
    private readonly Action<string>? rejected;
    private ManagedDirectories current;
    private Task[] workers = [];
    private FileSystemWatcher? configWatcher;
    private bool unavailableRoots;
    private int reloadRequested, recoveryRequested, refreshRequested, failures, rejectedConfigurations, watcherRecoveries, reconciliations;
    private ManagedDirectories CurrentScope => Volatile.Read(ref current);
    public Configuration Current => CurrentScope.Configuration;
    public int Failures => Volatile.Read(ref failures);
    public int RejectedConfigurations => Volatile.Read(ref rejectedConfigurations);
    public int WatcherRecoveries => Volatile.Read(ref watcherRecoveries);
    public int Reconciliations => Volatile.Read(ref reconciliations);
    public int WatcherCount => watchers.Count;
    public bool HistoryFailed { get; private set; }

    public WorkerHost(Configuration config, string configPath, RotatingLog log, MoveHistory? history = null,
        Action<string, Configuration>? accepted = null, Action<string>? rejected = null)
    { current = new ManagedDirectories(config); this.configPath = configPath; this.log = log; this.history = history; this.accepted = accepted; this.rejected = rejected; }

    public async Task<int> ScanAsync(CancellationToken token = default)
    {
        StartWorkers();
        using CancellationTokenRegistration registration = token.Register(lifetime.Cancel);
        try
        {
            await ScanSnapshotAsync(CurrentScope, lifetime.Token);
            queue.Writer.TryComplete();
            await Task.WhenAll(workers);
            return Failures == 0 ? 0 : 3;
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { return HistoryFailed ? 3 : 130; }
    }

    public async Task RunAsync(CancellationToken token)
    {
        StartWorkers();
        using CancellationTokenRegistration registration = token.Register(lifetime.Cancel);
        ReplaceWatchers(CurrentScope);
        configWatcher = new FileSystemWatcher(Path.GetDirectoryName(configPath)!)
        { NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size };
        configWatcher.Changed += ConfigChanged;
        configWatcher.Created += ConfigChanged;
        configWatcher.Deleted += ConfigChanged;
        configWatcher.Renamed += (s, e) => { ConfigChanged(s, e); if (Paths.Comparer.Equals(e.OldFullPath, configPath)) RequestReload(); };
        configWatcher.Error += (_, e) => { log.Write(LogLevel.Warning, "Config watcher error: " + e.GetException().Message); RequestReload(); };
        configWatcher.EnableRaisingEvents = true;
        if (Current.General.ScanOnStartup) await ScanSnapshotAsync(CurrentScope, lifetime.Token);
        try
        {
            await foreach (bool ignored in changes.Reader.ReadAllAsync(lifetime.Token))
            {
                await Task.Delay(500, lifetime.Token);
                if (Interlocked.Exchange(ref reloadRequested, 0) != 0)
                {
                    try
                    {
                        string text = ConfigurationFile.ReadSharedText(configPath);
                        ManagedDirectories candidate = new(ConfigurationLoader.Parse(text), CurrentScope);
                        ReplaceWatchers(candidate);
                        Volatile.Write(ref current, candidate);
                        log.Level = candidate.Configuration.General.LogLevel;
                        log.ConsoleOnly = candidate.Configuration.General.DryRun;
                        accepted?.Invoke(text, candidate.Configuration);
                        log.Write(LogLevel.Info, "Configuration reloaded");
                    }
                    catch (Exception exception) when (Recoverable(exception))
                    { Interlocked.Increment(ref rejectedConfigurations); rejected?.Invoke(exception.Message); log.Write(LogLevel.Warning, "Config rejected; keeping last good snapshot: " + exception.Message); }
                }
                if (Interlocked.Exchange(ref recoveryRequested, 0) != 0)
                {
                    try
                    {
                        ReplaceWatchers(CurrentScope); Interlocked.Increment(ref watcherRecoveries);
                        if (unavailableRoots) { await Task.Delay(TimeSpan.FromSeconds(5), lifetime.Token); RequestRecovery(); }
                        else log.Write(LogLevel.Info, "Watchers recovered");
                    }
                    catch (Exception exception) when (Recoverable(exception))
                    {
                        log.Write(LogLevel.Warning, "Watcher recovery pending: " + exception.Message);
                        await Task.Delay(TimeSpan.FromSeconds(5), lifetime.Token);
                        RequestRecovery();
                    }
                }
                if (Interlocked.Exchange(ref refreshRequested, 0) != 0)
                {
                    try { ReplaceWatchers(CurrentScope); }
                    catch (Exception exception) when (Recoverable(exception)) { HandleWatcherError(exception); }
                }
                await ScanSnapshotAsync(CurrentScope, lifetime.Token);
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        finally
        {
            DisposeWatchers();
            configWatcher.Dispose();
            configWatcher = null;
            queue.Writer.TryComplete();
            await Task.WhenAll(workers);
        }
    }

    public void Reconcile() => changes.Writer.TryWrite(true);
    internal void HandleWatcherError(Exception exception)
    { log.Write(LogLevel.Warning, "Watcher overflow/failure: " + exception.Message); RequestRecovery(); }
    private void RequestRecovery() { Interlocked.Exchange(ref recoveryRequested, 1); Reconcile(); }
    private void RequestReload()
    { if (Current.General.WatchConfigChanges) { Interlocked.Exchange(ref reloadRequested, 1); Reconcile(); } }
    private void ConfigChanged(object sender, FileSystemEventArgs args)
    { if (Paths.Comparer.Equals(args.FullPath, configPath)) RequestReload(); }

    private void ReplaceWatchers(ManagedDirectories scope)
    {
        List<FileSystemWatcher> candidate = [];
        try
        {
            bool unavailable = scope.Folders.Any(f => !Directory.Exists(f.Folder.Path));
            Dictionary<string, bool> requested = new(Paths.Comparer);
            foreach (string root in scope.WatchRoots)
            {
                string? existing = root;
                while (existing is not null && !Directory.Exists(existing)) existing = Path.GetDirectoryName(existing);
                if (existing is null) { unavailable = true; continue; }
                Paths.NoReparse(existing);
                // Missing destinations watch their nearest existing ancestor nonrecursively until created.
                bool recursive = Paths.Comparer.Equals(existing, root) &&
                    (scope.Folders.Any(f => f.Roots.Contains(root, Paths.Comparer)) ||
                    scope.Folders.Any(f => Paths.Comparer.Equals(f.Folder.Path, root) && f.Folder.Recursive));
                requested[existing] = recursive || requested.GetValueOrDefault(existing);
                // Observe root rename/recreation even when its old recursive handle remains valid.
                string? parent = Path.GetDirectoryName(root);
                if (Paths.Comparer.Equals(existing, root) && parent is not null) requested.TryAdd(parent, false);
            }
            foreach (KeyValuePair<string, bool> request in requested)
            {
                if (requested.Any(other => other.Value && !Paths.Comparer.Equals(other.Key, request.Key) && Paths.Within(request.Key, other.Key))) continue;
                FileSystemWatcher watcher = new(request.Key)
                {
                    IncludeSubdirectories = request.Value,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size
                };
                candidate.Add(watcher);
                watcher.Created += (_, e) => OnEvent(scope, e.FullPath, e.ChangeType);
                watcher.Changed += (_, e) => OnEvent(scope, e.FullPath, e.ChangeType);
                watcher.Renamed += (_, e) =>
                {
                    if (ReferenceEquals(scope, CurrentScope) && scope.AffectsWatchRoot(e.OldFullPath))
                    { Interlocked.Exchange(ref refreshRequested, 1); Reconcile(); }
                    OnEvent(scope, e.FullPath, e.ChangeType);
                };
                watcher.Deleted += (_, e) => OnEvent(scope, e.FullPath, e.ChangeType);
                watcher.Error += (_, e) => HandleWatcherError(e.GetException());
            }
            foreach (FileSystemWatcher watcher in candidate) watcher.EnableRaisingEvents = true;
            unavailableRoots = unavailable;
            if (unavailable) RequestRecovery();
        }
        catch { foreach (FileSystemWatcher watcher in candidate) watcher.Dispose(); throw; }
        DisposeWatchers();
        watchers.AddRange(candidate);
    }
    private void DisposeWatchers() { foreach (FileSystemWatcher watcher in watchers) watcher.Dispose(); watchers.Clear(); }
    private void OnEvent(ManagedDirectories scope, string path, WatcherChangeTypes change)
    {
        if (!ReferenceEquals(scope, CurrentScope) || Ignored(path)) return;
        if (change != WatcherChangeTypes.Changed && scope.AffectsWatchRoot(path))
        { Interlocked.Exchange(ref refreshRequested, 1); Reconcile(); }
        if (change == WatcherChangeTypes.Deleted || scope.IsKnownDirectory(path)) return;
        Folder? folder = scope.Owner(path);
        if (folder is null) return;
        Rule? rule = Matching.First(folder, path);
        if (change == WatcherChangeTypes.Changed && (rule is null || ManagedDirectories.AlreadyPlaced(path, rule))) return;
        if (!pending.TryAdd(path, scope))
        {
            if (pending.TryGetValue(path, out ManagedDirectories? queued) && !ReferenceEquals(queued, scope)) Reconcile();
            return;
        }
        if (!queue.Writer.TryWrite(new Work(scope, folder, path, change is WatcherChangeTypes.Created or WatcherChangeTypes.Renamed)))
        { pending.TryRemove(path, out _); Reconcile(); }
    }
    private bool Ignored(string path) => Paths.Comparer.Equals(path, configPath) || Paths.Comparer.Equals(path, configPath + ".bak") ||
        Paths.Within(path, log.DirectoryPath) || history is not null && Paths.Within(path, history.DirectoryPath) ||
        Paths.Comparer.Equals(path, Path.Combine(EngineStateStore.DefaultDirectory, "engine-state.json")) || Matching.IsTemporary(path);
    private void StartWorkers()
    {
        if (workers.Length == 0) workers = Enumerable.Range(0, 16).Select(ConsumeAsync).ToArray();
    }
    private async Task ScanSnapshotAsync(ManagedDirectories scope, CancellationToken token)
    {
        await scanGate.WaitAsync(token);
        try
        {
            Interlocked.Increment(ref reconciliations);
            foreach (ManagedFolder managed in scope.Folders)
            {
                Folder folder = managed.Folder;
                try
                {
                    foreach (string file in scope.Snapshot(folder, configPath))
                    {
                        token.ThrowIfCancellationRequested();
                        Rule? rule = Matching.First(folder, file);
                        if (Ignored(file) || rule is null || ManagedDirectories.AlreadyPlaced(file, rule) || !pending.TryAdd(file, scope)) continue;
                        try { await queue.Writer.WriteAsync(new Work(scope, folder, file), token); }
                        catch { pending.TryRemove(file, out _); throw; }
                    }
                }
                catch (Exception exception) when (Recoverable(exception))
                { Interlocked.Increment(ref failures); log.Write(LogLevel.Warning, "Scan failed: " + exception.Message); RequestRecovery(); }
            }
        }
        finally { scanGate.Release(); }
    }
    private readonly SemaphoreSlim moveGate = new(1, 1);
    private async Task ConsumeAsync(int worker)
    {
        SafeTransfer transfer = new(log.Write, history, configPath);
        try
        {
            await foreach (Work work in queue.Reader.ReadAllAsync(lifetime.Token))
            {
                // A per-snapshot semaphore enforces the configured parallelism even during reload.
                SemaphoreSlim concurrency = concurrencyLimits.GetValue(work.Config, c => new SemaphoreSlim(c.General.ParallelMoves));
                try
                {
                    await concurrency.WaitAsync(lifetime.Token);
                    try
                    {
                        if (Directory.Exists(work.Path))
                        {
                            bool recursiveScope = work.Folder.Recursive || work.Scope.Folders.Any(f => f.Roots.Any(root => Paths.Within(work.Path, root)));
                            if (work.DiscoverDirectory && recursiveScope) Reconcile();
                            continue;
                        }
                        if (!File.Exists(work.Path)) continue;
                        if (!ReferenceEquals(work.Scope, CurrentScope)) { Reconcile(); continue; }
                        Rule? rule = Matching.First(work.Folder, work.Path);
                        if (rule is null || ManagedDirectories.AlreadyPlaced(work.Path, rule)) continue;
                        if (!await Readiness.WaitAsync(work.Path, TimeSpan.FromSeconds(work.Config.General.ReadyTimeoutSeconds), lifetime.Token))
                        {
                            if (!File.Exists(work.Path)) continue;
                            Interlocked.Increment(ref failures); log.Write(LogLevel.Warning, "File not ready or inaccessible: " + work.Path); continue;
                        }
                        for (int attempt = 0; attempt < 3; attempt++)
                        {
                            try
                            {
                                // Serialize mutation across configuration generations; readiness remains parallel.
                                await moveGate.WaitAsync(lifetime.Token);
                                try
                                {
                                    if (!ReferenceEquals(work.Scope, CurrentScope)) { Reconcile(); break; }
                                    await transfer.ExecuteAsync(work.Config, work.Folder, rule, work.Path, lifetime.Token, managed: work.Scope);
                                }
                                finally { moveGate.Release(); }
                                break;
                            }
                            catch (IOException exception) when (exception is not HistoryUnavailableException && attempt < 2 && File.Exists(work.Path))
                            { await Task.Delay(200 * (attempt + 1), lifetime.Token); }
                        }
                    }
                    finally { concurrency.Release(); }
                }
                catch (HistoryUnavailableException exception)
                {
                    HistoryFailed = true; Interlocked.Increment(ref failures);
                    log.Write(LogLevel.Error, exception.Message); lifetime.Cancel();
                }
                catch (Exception exception) when (Recoverable(exception))
                {
                    if (!File.Exists(work.Path)) continue;
                    Interlocked.Increment(ref failures); log.Write(LogLevel.Warning, "Processing failed: " + work.Path + ": " + exception.Message);
                }
                finally
                {
                    pending.TryRemove(work.Path, out _);
                    if (!ReferenceEquals(work.Scope, CurrentScope)) Reconcile();
                }
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
    }
    private readonly System.Runtime.CompilerServices.ConditionalWeakTable<Configuration, SemaphoreSlim> concurrencyLimits = new();
    private static bool Recoverable(Exception exception) => exception is IOException or UnauthorizedAccessException or ConfigException or ArgumentException;
    public async ValueTask DisposeAsync()
    {
        lifetime.Cancel();
        queue.Writer.TryComplete();
        DisposeWatchers();
        configWatcher?.Dispose();
        await Task.WhenAll(workers);
        foreach (KeyValuePair<Configuration, SemaphoreSlim> pair in concurrencyLimits) pair.Value.Dispose();
        moveGate.Dispose(); scanGate.Dispose(); lifetime.Dispose();
    }
}
