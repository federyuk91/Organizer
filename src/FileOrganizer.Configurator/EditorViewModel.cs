using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using FileOrganizer.Core;
using FileOrganizer.Windows;
using Microsoft.Win32;

namespace FileOrganizer.Configurator;

public sealed class EditorCommand(Action execute, Func<bool>? enabled = null) : ICommand
{
    public bool CanExecute(object? parameter) => enabled?.Invoke() ?? true;
    public void Execute(object? parameter) => execute();
    public event EventHandler? CanExecuteChanged { add => CommandManager.RequerySuggested += value; remove => CommandManager.RequerySuggested -= value; }
}

public sealed class EditorViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly Window owner;
    private readonly IStartupStore startup;
    private ConfigurationFile? file;
    private ConfigurationDocument? document;
    private FileSystemWatcher? watcher;
    private CancellationTokenSource? debounce;
    private ConfigurationSection? selectedFolder, selectedRule;
    private string status = "Open a TOML configuration or create a new one.", rawText = "", enginePath = "", previewName = "example.png", previewResult = "";
    private string resolvedPaths = "Validate or preview to resolve paths and exclusions.";
    private bool rawPending, textReplaced, busy, external, disposed;
    private StartupState startupState = new(StartupStatus.Absent, null, "Select a configuration");
    public event PropertyChangedEventHandler? PropertyChanged;
    public ConfigurationDocument? Document => document;
    public ObservableCollection<ConfigurationSection>? Folders => document?.Folders;
    public string ConfigPath => file?.Path ?? "Unsaved configuration";
    public string Title => (IsDirty ? "* " : "") + "File Organizer — " + ConfigPath;
    public bool IsDirty => rawPending || textReplaced || document?.IsDirty == true;
    public bool Busy { get => busy; private set { busy = value; Refresh(); } }
    public bool CanEditForm => !busy && !rawPending && document is not null;
    public bool HasFolder => CanEditForm && selectedFolder is not null;
    public bool HasRule => HasFolder && selectedRule is not null;
    public string Status { get => status; private set { status = value; Notify(); } }
    public bool ExternalChanged => external;
    public bool CanManageStartup => !busy && file is not null;
    public bool IsInteractive => !busy;
    public void PrepareRaw() { if (!rawPending) Guard(UpdateRaw); }
    public string ExternalMessage => external ? "Changed on disk. Reload or Save As; saving over this file is blocked." : "";
    public ConfigurationSection? SelectedFolder
    {
        get => selectedFolder;
        set { selectedFolder = value; SelectedRule = value?.Rules.FirstOrDefault(); Notify(); Notify(nameof(Rules)); Refresh(); }
    }
    public ObservableCollection<ConfigurationSection>? Rules => selectedFolder?.Rules;
    public ConfigurationSection? SelectedRule { get => selectedRule; set { selectedRule = value; Notify(); Refresh(); } }
    public string RawText { get => rawText; set { if (rawText == value) return; rawText = value; rawPending = true; Notify(); Refresh(); } }
    public string EnginePath { get => enginePath; set { enginePath = value; Notify(); RefreshStartup(); } }
    public string PreviewName { get => previewName; set { previewName = value; Notify(); } }
    public string PreviewResult { get => previewResult; private set { previewResult = value; Notify(); } }
    public string ResolvedPaths { get => resolvedPaths; private set { resolvedPaths = value; Notify(); } }
    public string StartupMessage => startupState.Message + (startupState.Command is null ? "" : "\n" + startupState.Command);
    public bool? StartWithWindows => startupState.Status switch { StartupStatus.Absent => false, StartupStatus.Registered => true, _ => null };
    public static string[] LogLevels => ["error", "warning", "info", "debug"];
    public static string[] NamePolicies => ["increment", "skip"];
    public static string[] ContentPolicies => ["copy", "skip", "delete"];

    public ICommand NewCommand { get; }
    public ICommand OpenCommand { get; }
    public ICommand SaveCommand { get; }
    public ICommand SaveAsCommand { get; }
    public ICommand ReloadCommand { get; }
    public ICommand ValidateCommand { get; }
    public ICommand PreviewCommand { get; }
    public ICommand AddFolderCommand { get; }
    public ICommand RemoveFolderCommand { get; }
    public ICommand FolderUpCommand { get; }
    public ICommand FolderDownCommand { get; }
    public ICommand AddRuleCommand { get; }
    public ICommand DuplicateRuleCommand { get; }
    public ICommand RemoveRuleCommand { get; }
    public ICommand RuleUpCommand { get; }
    public ICommand RuleDownCommand { get; }
    public ICommand BrowseSourceCommand { get; }
    public ICommand BrowseDestinationCommand { get; }
    public ICommand BrowseEngineCommand { get; }
    public ICommand RefreshStartupCommand { get; }
    public ICommand ApplyRawCommand { get; }
    public ICommand RefreshRawCommand { get; }

    public EditorViewModel(Window owner, IStartupStore? startupStore = null)
    {
        this.owner = owner;
        startup = startupStore ?? new RegistryStartupStore();
        NewCommand = new EditorCommand(New, () => !Busy);
        OpenCommand = new EditorCommand(async () => await OpenAsync(), () => !Busy);
        SaveCommand = new EditorCommand(async () => await SaveAsync(false), () => !Busy && (document is not null || rawPending));
        SaveAsCommand = new EditorCommand(async () => await SaveAsync(true), () => !Busy && (document is not null || rawPending));
        ReloadCommand = new EditorCommand(async () => { if (file is not null && ConfirmDiscard()) await LoadAsync(file.Path); }, () => !Busy && file is not null);
        ValidateCommand = new EditorCommand(async () => await ValidateAsync(), () => !Busy && (document is not null || rawPending));
        PreviewCommand = new EditorCommand(async () => await PreviewAsync(), () => HasFolder);
        AddFolderCommand = new EditorCommand(() => SelectedFolder = document!.AddFolder(), () => CanEditForm);
        RemoveFolderCommand = new EditorCommand(() => { if (ConfirmRemove("folder and all its rules")) { int index = Folders!.IndexOf(selectedFolder!); Folders.Remove(selectedFolder!); SelectedFolder = Folders.ElementAtOrDefault(Math.Min(index, Folders.Count - 1)); } }, () => HasFolder);
        FolderUpCommand = new EditorCommand(() => MoveFolder(-1), () => HasFolder && Folders!.IndexOf(selectedFolder!) > 0);
        FolderDownCommand = new EditorCommand(() => MoveFolder(1), () => HasFolder && Folders!.IndexOf(selectedFolder!) < Folders.Count - 1);
        AddRuleCommand = new EditorCommand(() => SelectedRule = document!.AddRule(selectedFolder!), () => HasFolder);
        DuplicateRuleCommand = new EditorCommand(() => SelectedRule = document!.DuplicateRule(selectedFolder!, selectedRule!), () => HasRule);
        RemoveRuleCommand = new EditorCommand(() => { if (ConfirmRemove("rule")) { int index = Rules!.IndexOf(selectedRule!); Rules.Remove(selectedRule!); SelectedRule = Rules.ElementAtOrDefault(Math.Min(index, Rules.Count - 1)); } }, () => HasRule);
        RuleUpCommand = new EditorCommand(() => MoveRule(-1), () => HasRule && Rules!.IndexOf(selectedRule!) > 0);
        RuleDownCommand = new EditorCommand(() => MoveRule(1), () => HasRule && Rules!.IndexOf(selectedRule!) < Rules.Count - 1);
        BrowseSourceCommand = new EditorCommand(() => PickFolder(selectedFolder!.Fields["path"]), () => HasFolder);
        BrowseDestinationCommand = new EditorCommand(() => PickFolder(selectedRule!.Fields["destination"]), () => HasRule);
        BrowseEngineCommand = new EditorCommand(() => { OpenFileDialog picker = new() { Filter = "Background engine (FileOrganizer.exe)|FileOrganizer.exe", CheckFileExists = true }; if (picker.ShowDialog(owner) == true) EnginePath = picker.FileName; }, () => !Busy);
        RefreshStartupCommand = new EditorCommand(RefreshStartup, () => !Busy);
        ApplyRawCommand = new EditorCommand(() => Guard(ApplyRaw), () => !Busy && rawPending);
        RefreshRawCommand = new EditorCommand(() => { if (!rawPending || ConfirmRemove("unapplied TOML edits")) UpdateRaw(); }, () => !Busy && document is not null);
        enginePath = FindEngine();
    }
    public async Task InitializeAsync(string? path)
    {
        path ??= Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FileOrganizer", "config.toml");
        if (File.Exists(path)) await LoadAsync(path);
        else Status = "Configuration not found: " + path + ". Use Open or New; no files have been created.";
    }
    private async Task OpenAsync()
    {
        if (!ConfirmDiscard()) return;
        OpenFileDialog picker = new() { Filter = "TOML configuration|*.toml|All files|*.*" };
        if (picker.ShowDialog(owner) == true) await LoadAsync(picker.FileName);
    }
    private async Task LoadAsync(string path)
    {
        Busy = true;
        try
        {
            ConfigurationFile candidate = new(Paths.Normalize(Path.GetFullPath(path)));
            ConfigurationDocument? loaded = null;
            string? error = null;
            try { loaded = await Task.Run(candidate.Open); }
            catch (ConfigException exception) { error = exception.Message; }
            file = candidate; Attach(loaded); textReplaced = rawPending = false; external = false;
            SetRaw(candidate.Text); Watch(); RefreshStartup();
            Status = error is null ? "Loaded. Save validates the complete configuration. The engine runs independently." : "Invalid TOML; correct it in the TOML tab: " + error;
        }
        catch (Exception exception) when (Expected(exception)) { Status = exception.Message; }
        finally { Busy = false; }
    }
    private void Attach(ConfigurationDocument? value)
    {
        if (document is not null) document.Changed -= DocumentChanged;
        document = value;
        if (document is not null) document.Changed += DocumentChanged;
        SelectedFolder = document?.Folders.FirstOrDefault(); Refresh();
    }
    private void DocumentChanged(object? sender, EventArgs e) { ResolvedPaths = "Settings changed. Validate or preview again to resolve paths."; Refresh(); }
    private void New()
    {
        if (!ConfirmDiscard()) return;
        watcher?.Dispose(); watcher = null; file = null; external = false;
        Attach(new ConfigurationDocument("version = 1\n[general]\ndry_run = true\n"));
        SelectedFolder = document!.AddFolder(); textReplaced = true; UpdateRaw(); RefreshStartup(); Refresh();
        Status = "New configuration: choose a source, add a matching rule and Save As. Dry run is enabled.";
    }
    private void SetRaw(string text) { rawText = text; Notify(nameof(RawText)); }
    public void UpdateRaw() { SetRaw(document?.Render() ?? rawText); rawPending = false; Refresh(); }
    private void ApplyRaw()
    {
        ConfigurationDocument parsed = new(rawText);
        Attach(parsed); rawPending = false; textReplaced = file is null || rawText != file.Text; Refresh();
        Status = "TOML applied to the form; not saved.";
    }
    private string Candidate()
    {
        if (rawPending) ApplyRaw();
        return document?.Render() ?? throw new ConfigException("Open or create a configuration first");
    }
    private async Task ValidateAsync()
    {
        Busy = true;
        try { string text = Candidate(); Configuration config = await Task.Run(() => ConfigurationLoader.Parse(text)); ShowResolution(config); Status = "Configuration valid for the engine. No files were modified."; }
        catch (Exception exception) when (Expected(exception)) { Status = "Validation failed: " + exception.Message; }
        finally { Busy = false; }
    }
    private async Task<bool> SaveAsync(bool saveAs)
    {
        Busy = true;
        try
        {
            string text = Candidate();
            Configuration config = await Task.Run(() => ConfigurationLoader.Parse(text));
            ConfigurationFile target;
            if (saveAs || file is null)
            {
                SaveFileDialog picker = new() { Filter = "TOML configuration|*.toml", DefaultExt = ".toml", FileName = file?.Path ?? "config.toml", OverwritePrompt = false };
                if (picker.ShowDialog(owner) != true) return false;
                string path = Paths.Normalize(Path.GetFullPath(picker.FileName));
                if (file is not null && Paths.Comparer.Equals(path, file.Path)) target = file;
                else
                {
                    if (File.Exists(path)) throw new ConfigurationConflictException("Save As requires a new filename. Open an existing file first to edit it safely.");
                    target = new ConfigurationFile(path);
                }
            }
            else target = file;
            await Task.Run(() => target.Save(text));
            file = target; Attach(new ConfigurationDocument(text)); textReplaced = rawPending = external = false;
            SetRaw(text); Watch(); RefreshStartup();
            Status = "Saved atomically. " + (config.General.WatchConfigChanges ? "A running engine may immediately reload and reconcile files." : "Restart the engine to apply changes; config watching is disabled.");
            return true;
        }
        catch (Exception exception) when (Expected(exception)) { Status = "Save failed; changes retained: " + exception.Message; return false; }
        finally { Busy = false; }
    }
    private async Task PreviewAsync()
    {
        Busy = true;
        try
        {
            string text = Candidate(); int index = Folders!.IndexOf(selectedFolder!);
            Configuration config = await Task.Run(() => ConfigurationLoader.Parse(text));
            ShowResolution(config);
            Rule? rule = Matching.First(config.Folders[index], Path.GetFileName(previewName));
            PreviewResult = rule is null ? "No enabled rule matches." : rule.Name + " → " + rule.Destination;
        }
        catch (Exception exception) when (Expected(exception)) { PreviewResult = exception.Message; }
        finally { Busy = false; }
    }
    private void MoveFolder(int delta) { int index = Folders!.IndexOf(selectedFolder!); Folders.Move(index, index + delta); Refresh(); }
    private void ShowResolution(Configuration config)
    {
        int index = selectedFolder is null ? -1 : Folders!.IndexOf(selectedFolder);
        if (index < 0 || index >= config.Folders.Length) return;
        string source = config.Folders[index].Path;
        string[] excluded = config.Destinations.Where(d => Paths.Within(d, source)).Distinct(Paths.Comparer).ToArray();
        string[] managed = config.Folders[index].Rules.Where(r => r.Enabled).Select(r => r.Destination).Distinct(Paths.Comparer).ToArray();
        ResolvedPaths = "Source: " + source + "\nManaged destinations (recursive reconciliation): " + string.Join("; ", managed) +
            "\nDelegated from source traversal to managed ownership: " + (excluded.Length == 0 ? "none inside this source" : string.Join("; ", excluded));
    }
    private void MoveRule(int delta) { int index = Rules!.IndexOf(selectedRule!); Rules.Move(index, index + delta); Refresh(); }
    private void PickFolder(ConfigurationField field)
    {
        OpenFolderDialog picker = new() { Title = "Select folder (or type a relative destination / environment expression)" };
        if (picker.ShowDialog(owner) == true) field.Text = picker.FolderName;
    }
    public void RefreshStartup()
    {
        startupState = file is null ? new(StartupStatus.Absent, null, "Save a configuration to manage its Windows startup entry.") : StartupRegistration.Read(startup, enginePath, file.Path);
        Notify(nameof(StartWithWindows)); Notify(nameof(StartupMessage));
    }
    public async Task ChangeStartupAsync(bool enabled)
    {
        if (Busy) { RefreshStartup(); return; }
        try
        {
            if (enabled)
            {
                if (IsDirty || file is null) { Status = "Save the configuration before enabling Windows startup."; return; }
                if (!Path.IsPathFullyQualified(enginePath) || !File.Exists(enginePath) || !Path.GetFileName(enginePath).Equals("FileOrganizer.exe", StringComparison.OrdinalIgnoreCase))
                    throw new IOException("Select the published background FileOrganizer.exe, not the Configurator.");
                Busy = true;
                await Task.Run(() => { ConfigurationLoader.Load(file.Path); StartupRegistration.Install(startup, enginePath, file.Path); });
            }
            else
            {
                if (file is null) return;
                Busy = true; await Task.Run(() => StartupRegistration.Uninstall(startup, file.Path));
            }
            RefreshStartup();
            if (startupState.Status != (enabled ? StartupStatus.Registered : StartupStatus.Absent)) throw new IOException("Autorun change could not be verified: " + startupState.Message);
            Status = enabled ? "Background engine registered for login. The GUI is not registered or started." : "This configuration's engine startup entry removed.";
        }
        catch (Exception exception) when (Expected(exception)) { Status = "Windows startup failed: " + exception.Message; }
        finally { RefreshStartup(); Busy = false; }
    }
    public bool ConfirmDiscard() => !IsDirty || MessageBox.Show(owner, "Discard unsaved changes? Use Save first if you want to keep them.", "Unsaved configuration", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes;
    private bool ConfirmRemove(string what) => MessageBox.Show(owner, "Remove " + what + "? This changes the draft only until Save.", "Confirm removal", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes;
    private void Guard(Action action) { try { action(); } catch (Exception exception) when (Expected(exception)) { Status = exception.Message; } }
    private void Watch()
    {
        watcher?.Dispose(); watcher = null;
        if (file is null) return;
        try
        {
            watcher = new FileSystemWatcher(Path.GetDirectoryName(file.Path)!) { NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size };
            watcher.Changed += OnDiskChange; watcher.Created += OnDiskChange; watcher.Deleted += OnDiskChange;
            watcher.Renamed += (_, e) => { if (Paths.Comparer.Equals(e.FullPath, file?.Path) || Paths.Comparer.Equals(e.OldFullPath, file?.Path)) ScheduleExternalCheck(); };
            watcher.Error += (_, e) => owner.Dispatcher.BeginInvoke(() => Status = "Configuration watcher failed; Save will still check conflicts: " + e.GetException().Message);
            watcher.EnableRaisingEvents = true;
        }
        catch (Exception exception) when (Expected(exception)) { Status = "Config watcher unavailable; Save still checks conflicts: " + exception.Message; }
    }
    private void OnDiskChange(object sender, FileSystemEventArgs e) { if (Paths.Comparer.Equals(e.FullPath, file?.Path)) ScheduleExternalCheck(); }
    private void ScheduleExternalCheck() => owner.Dispatcher.BeginInvoke(async () =>
    {
        if (disposed) return;
        debounce?.Cancel(); debounce?.Dispose(); debounce = new CancellationTokenSource();
        CancellationToken token = debounce.Token;
        ConfigurationFile? observed = file;
        try
        {
            await Task.Delay(300, token);
            bool changed = observed is not null && await Task.Run(observed.HasExternalChanges, token);
            if (ReferenceEquals(observed, file)) { external = changed; Notify(nameof(ExternalChanged)); Notify(nameof(ExternalMessage)); if (changed) Status = ExternalMessage; }
        }
        catch (OperationCanceledException) { }
        catch (Exception exception) when (Expected(exception)) { external = true; Status = "Cannot verify external changes: " + exception.Message; Refresh(); }
    });
    private static string FindEngine()
    {
        string local = Path.Combine(AppContext.BaseDirectory, "FileOrganizer.exe");
        if (File.Exists(local)) return local;
        string installed = Path.Combine(AppContext.BaseDirectory, "engine", "FileOrganizer.exe");
        if (File.Exists(installed)) return installed;
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            foreach (string mode in new[] { "aot", "standard" })
            { string path = Path.Combine(directory.FullName, "artifacts", "publish", mode, "FileOrganizer.exe"); if (File.Exists(path)) return path; }
            directory = directory.Parent;
        }
        return "";
    }
    private static bool Expected(Exception e) => e is IOException or UnauthorizedAccessException or ConfigException or ArgumentException or InvalidOperationException or System.Security.SecurityException;
    private void Notify([CallerMemberName] string? property = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));
    private void Refresh()
    {
        foreach (string property in new[] { nameof(Document), nameof(Folders), nameof(IsDirty), nameof(Title), nameof(Busy), nameof(CanEditForm), nameof(HasFolder), nameof(HasRule), nameof(ConfigPath), nameof(ExternalMessage), nameof(ExternalChanged), nameof(CanManageStartup), nameof(IsInteractive) }) Notify(property);
        CommandManager.InvalidateRequerySuggested();
    }
    public void Dispose()
    { disposed = true; watcher?.Dispose(); debounce?.Cancel(); debounce?.Dispose(); if (document is not null) document.Changed -= DocumentChanged; }
}
