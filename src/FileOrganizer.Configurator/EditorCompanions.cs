using System.IO;
using System.Windows;
using System.Windows.Input;
using FileOrganizer.Core;
using FileOrganizer.Windows;

namespace FileOrganizer.Configurator;

public sealed partial class EditorViewModel
{
    private MoveHistory history = null!;
    private EngineStateStore states = null!;
    private FileSystemWatcher? historyWatcher, stateWatcher;
    private EngineSnapshot? runningSnapshot;
    private string? diskHash;
    private bool diskValid;
    private string savedSummary = "File values have not been verified.", runtimeMessage = "Engine state has not been verified.", historyStatus = "Refresh to read the last seven days.";
    private string runtimeWarning = "Applied engine settings have not been verified.";
    private IReadOnlyList<MoveRecord> historyEntries = [];
    private string[] selectedHistoryIds = [];
    private CancellationTokenSource? undoCancellation;
    private bool showAllHistory;
    private long verificationVersion;
    private int historyNoticeQueued;
    public bool ShowAllHistory
    {
        get => showAllHistory;
        set { showAllHistory = value; Notify(); if (RefreshHistoryCommand.CanExecute(null)) RefreshHistoryCommand.Execute(null); }
    }
    public string SavedSummary => savedSummary;
    public string RuntimeMessage => runtimeMessage;
    public string RuntimeWarning => runtimeWarning;
    public string SyncMessage => file is null ? "UNSAVED DRAFT — no configuration file is active in this editor." :
        external ? "CONFLICT — form fields are a draft. See actual file values below." :
        !diskValid ? "ON-DISK CONFIGURATION IS NOT VERIFIED — engine settings may differ; see the status below." :
        IsDirty ? "UNSAVED DRAFT — changes shown in the form are NOT applied. Save to update the file. See current file values below." : "Form synchronized with the file at the last verification.";
    public string HistoryStatus => historyStatus;
    public IReadOnlyList<MoveRecord> HistoryEntries => historyEntries;
    public ICommand RefreshHistoryCommand { get; private set; } = null!;
    public ICommand UndoHistoryCommand { get; private set; } = null!;
    public ICommand CheckSyncCommand { get; private set; } = null!;
    public ICommand OpenEngineConfigCommand { get; private set; } = null!;
    public ICommand StopWorkerCommand { get; private set; } = null!;
    public ICommand CancelUndoCommand { get; private set; } = null!;
    public void SelectHistory(IEnumerable<MoveRecord> selected)
    { selectedHistoryIds = selected.Select(e => e.Id).ToArray(); CommandManager.InvalidateRequerySuggested(); }

    private void InitializeCompanions(MoveHistory moves, EngineStateStore engineStates)
    {
        history = moves; states = engineStates;
        RefreshHistoryCommand = new EditorCommand(async () => await RefreshHistoryAsync(), () => !Busy && (file is not null || showAllHistory));
        UndoHistoryCommand = new EditorCommand(async () => await UndoSelectedAsync(), () => !Busy && (file is not null || showAllHistory) && selectedHistoryIds.Length > 0);
        CheckSyncCommand = new EditorCommand(() => ScheduleExternalCheck(), () => !Busy);
        OpenEngineConfigCommand = new EditorCommand(async () => { if (runningSnapshot is not null && ConfirmDiscard()) await LoadAsync(runningSnapshot.ConfigPath); }, () => !Busy && runningSnapshot is not null);
        StopWorkerCommand = new EditorCommand(() => Guard(() => Status = InstanceLease.RequestStop() ? "Safe stop requested. The worker will remain stopped." : "No current worker accepted the stop request."), () => !Busy);
        CancelUndoCommand = new EditorCommand(() => undoCancellation?.Cancel(), () => undoCancellation is not null);
        owner.Activated += OnActivated;
        try
        {
            Paths.NoReparse(history.DirectoryPath); Directory.CreateDirectory(history.DirectoryPath);
            historyWatcher = new(history.DirectoryPath, "history-*.jsonl") { NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size };
            historyWatcher.Changed += HistoryChanged; historyWatcher.Created += HistoryChanged; historyWatcher.Deleted += HistoryChanged;
            historyWatcher.Renamed += (_, _) => HistoryChanged(null, null);
            historyWatcher.Error += (_, _) => HistoryChanged(null, null);
            historyWatcher.EnableRaisingEvents = true;
            string stateDirectory = Path.GetDirectoryName(states.FilePath)!;
            Paths.NoReparse(stateDirectory); Directory.CreateDirectory(stateDirectory);
            stateWatcher = new(stateDirectory, Path.GetFileName(states.FilePath)) { NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size };
            stateWatcher.Changed += StateChanged; stateWatcher.Created += StateChanged; stateWatcher.Deleted += StateChanged;
            stateWatcher.Renamed += (_, _) => StateChanged(null, null);
            stateWatcher.Error += (_, _) => StateChanged(null, null);
            stateWatcher.EnableRaisingEvents = true;
        }
        catch (Exception exception) when (Expected(exception)) { runtimeMessage = "Monitoring unavailable; use Verify state: " + exception.Message; }
    }
    private void OnActivated(object? sender, EventArgs args) { if (!disposed) ScheduleExternalCheck(); }
    private void HistoryChanged(object? sender, FileSystemEventArgs? args)
    {
        if (disposed || Interlocked.Exchange(ref historyNoticeQueued, 1) != 0) return;
        owner.Dispatcher.BeginInvoke(() =>
        {
            Interlocked.Exchange(ref historyNoticeQueued, 0);
            if (!disposed) { historyStatus = "History changed. Refresh to display the latest movements."; Notify(nameof(HistoryStatus)); }
        });
    }
    private void StateChanged(object? sender, FileSystemEventArgs? args) => owner.Dispatcher.BeginInvoke(async () =>
    { if (!disposed) await RefreshDiskAndRuntimeAsync(); });

    private async Task RefreshDiskAndRuntimeAsync()
    {
        ConfigurationFile? observed = file;
        long version = Interlocked.Increment(ref verificationVersion);
        try
        {
            (string? Text, Configuration? Config, EngineSnapshot? Engine) snapshot = await Task.Run(() =>
            {
                if (observed is not null) Paths.NoReparse(observed.Path);
                string? text = observed is null ? null : ConfigurationFile.ReadSharedText(observed.Path);
                Configuration? config = null;
                if (text is not null) { try { config = ConfigurationLoader.Parse(text); } catch (ConfigException) { } }
                return (text, config, states.Read());
            });
            if (disposed || !ReferenceEquals(observed, file) || version != Volatile.Read(ref verificationVersion)) return;
            diskHash = snapshot.Text is null ? null : EngineStateStore.Hash(snapshot.Text);
            diskValid = snapshot.Config is not null;
            savedSummary = snapshot.Config is null ? "ON DISK: missing or invalid configuration; do not assume the engine uses the form values." :
                "ON DISK — " + observed!.Path + "\n" + string.Join("\n", snapshot.Config.Folders.Select(f =>
                    $"{f.Name}: enabled={f.Enabled}; Include subfolders={f.Recursive}; source={f.Path}; path overrides={(f.PathOverrides.IsDefault ? 0 : f.PathOverrides.Length)} (see Managed paths)"));
            runningSnapshot = snapshot.Engine;
            ObserveWorkerProcess(snapshot.Engine);
            CheckAppliedTree(snapshot.Engine);
            runtimeWarning = snapshot.Engine is null ? "Engine is stopped or its applied settings are not verifiable." :
                observed is null || !Paths.Comparer.Equals(snapshot.Engine.ConfigPath, observed.Path) ? "WARNING — engine uses another TOML: " + snapshot.Engine.ConfigPath :
                snapshot.Engine.ConfigHash != diskHash ? "WARNING — the engine has NOT applied the current saved file." : "";
            if (snapshot.Engine?.ReloadError is not null) runtimeWarning += " Reload rejected: " + snapshot.Engine.ReloadError;
            runtimeMessage = snapshot.Engine is null ? "ENGINE: not verified (stopped or an older version without status reporting)." :
                $"ENGINE PID {snapshot.Engine.Pid} — {snapshot.Engine.Executable}\nTOML: {snapshot.Engine.ConfigPath}\n" +
                (observed is null || !Paths.Comparer.Equals(snapshot.Engine.ConfigPath, observed.Path) ? "WARNING: engine uses ANOTHER TOML. " :
                    snapshot.Engine.ConfigHash != diskHash ? "WARNING: engine has NOT applied the current file. " : "Applied snapshot matches this file. ") +
                (snapshot.Engine.ReloadError is null ? "" : "Reload rejected: " + snapshot.Engine.ReloadError + ". ") +
                string.Join("; ", snapshot.Engine.Folders.Select(f => $"{f.Name}: enabled={f.Enabled}, Include subfolders={f.Recursive}, path overrides={f.PathOverrides?.Length ?? 0} (see Managed paths)"));
            Notify(nameof(SavedSummary)); Notify(nameof(RuntimeMessage)); Notify(nameof(RuntimeWarning)); Notify(nameof(SyncMessage)); CommandManager.InvalidateRequerySuggested();
        }
        catch (Exception exception) when (Expected(exception))
        {
            if (disposed || !ReferenceEquals(observed, file) || version != Volatile.Read(ref verificationVersion)) return;
            diskValid = false; savedSummary = "ON DISK: UNVERIFIED — " + exception.Message; runtimeMessage = "ENGINE: UNVERIFIED"; runtimeWarning = "WARNING — applied settings cannot be verified.";
            CheckAppliedTree(null);
            ObserveWorkerProcess(null);
            Notify(nameof(SavedSummary)); Notify(nameof(RuntimeMessage)); Notify(nameof(RuntimeWarning)); Notify(nameof(SyncMessage));
            CommandManager.InvalidateRequerySuggested();
        }
    }
    public async Task RefreshHistoryAsync()
    {
        if (file is null && !showAllHistory) return;
        Busy = true;
        try
        {
            string? path = showAllHistory ? null : file?.Path;
            historyEntries = await Task.Run(() => history.Read(path)); selectedHistoryIds = [];
            historyStatus = $"{historyEntries.Count} movements in the last seven days. Undo never overwrites an original path.";
            Notify(nameof(HistoryEntries));
        }
        catch (Exception exception) when (Expected(exception)) { historyStatus = "History unavailable: " + exception.Message; }
        finally { Notify(nameof(HistoryStatus)); Busy = false; }
    }
    private async Task UndoSelectedAsync()
    {
        if (file is null && !showAllHistory || selectedHistoryIds.Length == 0) return;
        if (MessageBox.Show(owner, $"Undo {selectedHistoryIds.Length} selected movement(s)? The background engine will be stopped and remain stopped. Changed files and occupied original paths will be skipped.",
            "Undo movements", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        undoCancellation = new();
        Busy = true;
        try
        {
            string[] ids = selectedHistoryIds; string? path = showAllHistory ? null : file?.Path;
            CancellationToken token = undoCancellation.Token;
            IReadOnlyList<UndoResult> results = await Task.Run(async () =>
            {
                InstanceLease.RequestStop();
                return await history.UndoAsync(ids, path, token);
            });
            historyEntries = await Task.Run(() => history.Read(path)); Notify(nameof(HistoryEntries)); selectedHistoryIds = [];
            historyStatus = $"Restored: {results.Count(r => r.Success)}. Skipped: {results.Count(r => !r.Success)}. Engine remains stopped.\n" +
                string.Join("\n", results.Where(r => !r.Success).Take(8).Select(r => r.Id + ": " + r.Message));
            await RefreshDiskAndRuntimeAsync();
        }
        catch (OperationCanceledException)
        {
            historyStatus = "Undo cancelled. Completed restores are retained; the engine remains stopped.";
            try { historyEntries = await Task.Run(() => history.Read(showAllHistory ? null : file?.Path)); Notify(nameof(HistoryEntries)); }
            catch (Exception exception) when (Expected(exception)) { historyStatus += " History unavailable: " + exception.Message; }
        }
        catch (Exception exception) when (Expected(exception)) { historyStatus = "Undo stopped safely: " + exception.Message; }
        finally { undoCancellation.Dispose(); undoCancellation = null; Notify(nameof(HistoryStatus)); Busy = false; }
    }
    private void DisposeCompanions()
    { owner.Activated -= OnActivated; historyWatcher?.Dispose(); stateWatcher?.Dispose(); observedWorker?.Dispose(); undoCancellation?.Cancel(); }
}
