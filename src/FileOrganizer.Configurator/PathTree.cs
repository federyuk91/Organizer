using System.Collections.Immutable;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows.Input;
using FileOrganizer.Core;
using FileOrganizer.Windows;

namespace FileOrganizer.Configurator;

public sealed class PathTreeLayout
{
    private DirectoryScope[] scopes;
    private readonly Dictionary<string, PathTreeNode> nodes = new(Paths.Comparer);
    public int Version { get; private set; }
    public PathTreeLayout(Configuration config) { scopes = config.Folders.Select(f => new DirectoryScope(f)).ToArray(); }
    public IEnumerable<string> KnownPaths => scopes.SelectMany(s => s.ExplicitRoots.Concat(s.Overrides.Select(p => p.Path))).Distinct(Paths.Comparer);
    public int Owner(string path)
    {
        return scopes.SelectMany((s, index) => s.ExplicitRoots.Concat(s.Overrides.Select(p => p.Path)).Distinct(Paths.Comparer).Where(root => Paths.Within(path, root)).Select(root =>
            (Index: index, Enabled: s.Folder.Enabled, Destination: s.BaseRoots.Contains(root, Paths.Comparer) && !Paths.Comparer.Equals(root, s.Folder.Path), Active: s.Mode(path) != DirectoryMode.Off, Length: root.Length)))
            .OrderByDescending(p => p.Enabled).ThenByDescending(p => p.Destination).ThenByDescending(p => p.Active).ThenByDescending(p => p.Length).Select(p => p.Index).FirstOrDefault();
    }
    public DirectoryScope Scope(string path) => scopes[Owner(path)];
    public DirectoryMode Mode(string path) => Scope(path).Mode(path);
    public bool HasActiveDescendant(string path) => KnownPaths.Any(p => !Paths.Comparer.Equals(path, p) && Paths.Within(p, path) && Mode(p) != DirectoryMode.Off);
    public bool Complete(string path) => Scope(path).Complete(path) &&
        !KnownPaths.Any(p => Paths.Within(p, path) && Mode(p) != DirectoryMode.Recursive);
    public IEnumerable<string> KnownChildren(string path) => KnownPaths.Where(p => !Paths.Comparer.Equals(p, path) && Paths.Within(p, path))
        .Select(p => System.IO.Path.Combine(path, System.IO.Path.GetRelativePath(path, p).Split(System.IO.Path.DirectorySeparatorChar)[0])).Distinct(Paths.Comparer);
    public string Label(string path, bool root)
    {
        List<string> roles = [];
        foreach (DirectoryScope scope in scopes)
        {
            if (Paths.Comparer.Equals(scope.Folder.Path, path)) roles.Add(scope.Folder.Name + ": Source");
            else if (scope.BaseRoots.Contains(path, Paths.Comparer)) roles.Add(scope.Folder.Name + ": Destination");
        }
        string name = root ? path : System.IO.Path.GetFileName(path);
        return roles.Count == 0 ? name : name + " — " + string.Join("; ", roles);
    }
    public PathTreeNode Node(string path, bool root = false, bool unsafePath = false)
    {
        if (!nodes.TryGetValue(path, out PathTreeNode? node)) nodes[path] = node = new(this, path, root, unsafePath);
        return node;
    }
    public IEnumerable<PathTreeNode> Roots => KnownPaths.Where(p => !KnownPaths.Any(other => !Paths.Comparer.Equals(p, other) && Paths.Within(p, other)))
        .Order(Paths.Comparer).Select(p => Node(p, true));
    public void Update(Configuration config)
    {
        scopes = config.Folders.Select(f => new DirectoryScope(f)).ToArray(); Version++;
        foreach (PathTreeNode node in nodes.Values.ToArray()) node.Update();
    }
    public async Task OpenConfiguredBranchesAsync(PathTreeNode node)
    {
        if (node.IsOff) return;
        await node.ProbeLeafAsync();
        if (node.IsOff || !HasActiveDescendant(node.Path)) return;
        await node.LoadChildrenAsync(); node.Expanded = true;
        foreach (PathTreeNode child in node.Children.Where(c => !c.Placeholder && (Mode(c.Path) != DirectoryMode.Off || HasActiveDescendant(c.Path))).ToArray())
            await OpenConfiguredBranchesAsync(child);
    }
}

public sealed class PathTreeNode : INotifyPropertyChanged
{
    public PathTreeLayout Layout { get; }
    private DirectoryScope Scope => Layout.Scope(Path);
    private bool expanded, loaded;
    private readonly bool root;
    private bool? hasSubdirectories;
    private readonly HashSet<PathTreeNode> observedChildren = [];
    public int FolderIndex => Layout.Owner(Path);
    public string Path { get; }
    public string Label => Placeholder ? "Cannot inspect this directory" : Layout.Label(Path, root);
    public bool Unsafe { get; }
    public bool Placeholder { get; }
    public ObservableCollection<PathTreeNode> Children { get; } = [];
    public bool IsLeaf => hasSubdirectories == false && !Layout.KnownChildren(Path).Any();
    public bool IsOff => Unsafe || Placeholder || Layout.Mode(Path) == DirectoryMode.Off && !Layout.HasActiveDescendant(Path);
    public DirectoryMode State => IsOff ? DirectoryMode.Off : Layout.Complete(Path) || IsLeaf ||
        Layout.Mode(Path) != DirectoryMode.Off && AllLoadedChildrenComplete ? DirectoryMode.Recursive : DirectoryMode.Direct;
    private bool AllLoadedChildrenComplete => loaded && Children.Count > 0 && Children.All(c => !c.Placeholder && c.State == DirectoryMode.Recursive);
    public string Symbol => State switch { DirectoryMode.Off => "□", DirectoryMode.Direct => "■", _ => "✓" };
    public string Description => Path + "\n" + (!Scope.Folder.Enabled ? "Monitor disabled. Enable it in Folders and rules before changing its paths." : Unsafe ? "Unavailable or reparse point: never explored." :
        Layout.Mode(Path) == DirectoryMode.Off && !IsOff ? "Only the configured descendant paths shown here are monitored; direct files are excluded." :
        IsLeaf && !IsOff ? "No subfolders: all current direct files are included. Future folders follow the saved directory mode." :
        State == DirectoryMode.Recursive && Layout.Mode(Path) == DirectoryMode.Direct ? "Direct files and all current subfolders are included. Future folders follow the saved selections; choose recursive explicitly to include future descendants." :
        State switch { DirectoryMode.Off => "Excluded: descendants are hidden.", DirectoryMode.Direct => "Active: direct files; only explicitly active child paths are included.", _ => "Active: all descendants recursively, including future subfolders." });
    public bool Blocked => Scope.Overrides.Any(p => p.Mode == DirectoryMode.Off && !Paths.Comparer.Equals(p.Path, Path) && Paths.Within(Path, p.Path));
    public bool CanCycle => Scope.Folder.Enabled && !Unsafe && !Placeholder && !Blocked;
    public bool Expanded { get => expanded; set { expanded = value; Notify(nameof(Expanded)); } }
    public event PropertyChangedEventHandler? PropertyChanged;
    internal PathTreeNode(PathTreeLayout layout, string path, bool root, bool unsafePath = false, bool placeholder = false)
    {
        Layout = layout; Path = path; this.root = root; Unsafe = unsafePath; Placeholder = placeholder;
        Children.CollectionChanged += (_, args) =>
        {
            if (args.Action == System.Collections.Specialized.NotifyCollectionChangedAction.Reset)
            {
                foreach (PathTreeNode child in observedChildren) child.PropertyChanged -= ChildChanged;
                observedChildren.Clear();
            }
            if (args.OldItems is not null) foreach (PathTreeNode child in args.OldItems)
                if (observedChildren.Remove(child)) child.PropertyChanged -= ChildChanged;
            if (args.NewItems is not null) foreach (PathTreeNode child in args.NewItems)
                if (observedChildren.Add(child)) child.PropertyChanged += ChildChanged;
            Notify(nameof(State)); Notify(nameof(Symbol)); Notify(nameof(Description));
        };
        if (!IsOff && !Unsafe && !Placeholder) AddPlaceholder();
    }
    private void AddPlaceholder() => Children.Add(new(Layout, Path, false, placeholder: true));
    private void ChildChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName != nameof(State)) return;
        Notify(nameof(State)); Notify(nameof(Symbol)); Notify(nameof(Description));
    }
    private void Notify(string name) => PropertyChanged?.Invoke(this, new(name));
    public void Update()
    {
        if (IsOff) { Children.Clear(); loaded = false; Expanded = false; }
        else if (Children.Count == 0 && !loaded && !Unsafe && !Placeholder) AddPlaceholder();
        foreach (string property in new[] { nameof(IsOff), nameof(IsLeaf), nameof(State), nameof(Symbol), nameof(Description), nameof(Blocked), nameof(CanCycle), nameof(Label) }) Notify(property);
    }
    public async Task ProbeLeafAsync(bool refresh = false)
    {
        if (Unsafe || Placeholder) return;
        if (!refresh && hasSubdirectories.HasValue) return;
        if (Layout.Mode(Path) == DirectoryMode.Off && Layout.HasActiveDescendant(Path)) { hasSubdirectories = true; Update(); return; }
        int version = Layout.Version;
        bool? observed = await Task.Run(() =>
        {
            try
            {
                Paths.NoReparse(Path);
                if (!Directory.Exists(Path)) return (bool?)null;
                using IEnumerator<string> directories = Directory.EnumerateDirectories(Path).GetEnumerator();
                return directories.MoveNext();
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ConfigException) { return null; }
        });
        if (version != Layout.Version) return;
        hasSubdirectories = observed; Update();
    }
    public async Task LoadChildrenAsync()
    {
        if (loaded || IsOff || Unsafe || Placeholder) return;
        int version = Layout.Version;
        List<(string Path, bool Unsafe)> children;
        try
        {
            children = await Task.Run(() =>
            {
                Dictionary<string, bool> result = new(Paths.Comparer);
                if (Layout.Mode(Path) != DirectoryMode.Off && Directory.Exists(Path))
                {
                    Paths.NoReparse(Path);
                    foreach (string child in Directory.EnumerateDirectories(Path))
                        result[child] = (File.GetAttributes(child) & FileAttributes.ReparsePoint) != 0;
                }
                foreach (string child in Layout.KnownChildren(Path)) result.TryAdd(child, false);
                return result.OrderBy(p => p.Key, Paths.Comparer).Select(p => (p.Key, p.Value)).ToList();
            });
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ConfigException)
        {
            if (version == Layout.Version) { Children.Clear(); loaded = true; Children.Add(new(Layout, Path, false, true, true)); }
            return;
        }
        if (version != Layout.Version || IsOff) return;
        Children.Clear(); loaded = true;
        foreach ((string path, bool unsafePath) in children)
        {
            if (version != Layout.Version || IsOff) return;
            PathTreeNode child = Layout.Node(path, unsafePath: unsafePath); Children.Add(child);
            if (!child.IsOff) await child.ProbeLeafAsync();
        }
    }
}

public sealed partial class EditorViewModel
{
    public static string[] PathViews { get; } = ["Draft", "Saved file", "Applied engine"];
    private string pathView = "Draft", treeStatus = "Load the tree to inspect managed paths. No excluded branch is explored.";
    private ConfigurationDocument? treeDocument;
    private EngineSnapshot? treeApplied;
    private PathTreeLayout? treeLayout;
    private bool treeStale = true;
    public string PathView { get => pathView; set { pathView = value; Notify(); MarkTreeStale(); } }
    public string TreeStatus => treeStatus;
    public bool CanEditTree => CanEditForm && PathView == "Draft" && !treeStale && ReferenceEquals(treeDocument, document);
    public ObservableCollection<PathTreeNode> PathRoots { get; } = [];
    public ICommand LoadPathsCommand { get; private set; } = null!;
    private void InitializeTree() => LoadPathsCommand = new EditorCommand(async () => await LoadPathsAsync(), () => !Busy);
    private void MarkTreeStale()
    {
        treeStale = true; treeStatus = "Tree is not current. Choose a view and Load / refresh paths.";
        Notify(nameof(TreeStatus)); Notify(nameof(CanEditTree));
    }
    private void CheckAppliedTree(EngineSnapshot? snapshot)
    {
        if (PathView == "Applied engine" && !treeStale && treeApplied is not null &&
            (snapshot is null || snapshot.Pid != treeApplied.Pid || snapshot.StartedUtcTicks != treeApplied.StartedUtcTicks || snapshot.ConfigHash != treeApplied.ConfigHash))
            MarkTreeStale();
    }
    public async Task LoadPathsAsync()
    {
        Busy = true;
        try
        {
            Configuration config;
            if (PathView == "Applied engine")
            {
                EngineSnapshot? applied = await Task.Run(() => states.Read());
                if (applied is null || applied.Version < 3) throw new IOException("Applied path scope unavailable. Start engine 1.2.6 or later to verify the current directory policy.");
                treeApplied = applied;
                config = new(new General(), applied.Folders.Select(f => new Folder(f.Name, f.Path, f.Enabled, f.Recursive, new DuplicatePolicy(),
                    (f.Destinations ?? []).Select(p => new Rule("Destination", true, p, [".unused"], [], [], false)).ToImmutableArray(),
                    (f.PathOverrides ?? []).ToImmutableArray())).ToImmutableArray());
                treeStatus = "READ ONLY — applied engine snapshot: " + applied.ConfigPath;
            }
            else
            {
                config = PathView == "Draft" ? document?.Validate() ?? throw new ConfigException("Open a configuration first") :
                    await Task.Run(() => ConfigurationLoader.Parse(ConfigurationFile.ReadSharedText(file?.Path ?? throw new IOException("No saved file"))));
                treeStatus = PathView == "Draft" ? "DRAFT — clicks change monitoring only after Save. Rules may still write files into excluded destinations." : "READ ONLY — values read from the saved TOML.";
            }
            PathRoots.Clear();
            treeLayout = new(config);
            foreach (PathTreeNode root in treeLayout.Roots.ToArray()) PathRoots.Add(root);
            foreach (PathTreeNode root in PathRoots) await treeLayout.OpenConfiguredBranchesAsync(root);
            treeDocument = document; treeStale = false;
        }
        catch (Exception exception) when (Expected(exception)) { PathRoots.Clear(); treeStale = true; treeStatus = "Path scope unavailable: " + exception.Message; }
        finally { Busy = false; Notify(nameof(TreeStatus)); Notify(nameof(CanEditTree)); }
    }
    public async Task CyclePathAsync(PathTreeNode node, bool reverse)
    {
        if (!CanEditTree || !node.CanCycle || !ReferenceEquals(node.Layout, treeLayout)) return;
        bool wasLeaf = node.IsLeaf && !node.IsOff;
        await node.ProbeLeafAsync(true);
        if (!CanEditTree || !node.CanCycle || !ReferenceEquals(node.Layout, treeLayout)) return;
        DirectoryMode next = node.IsLeaf || wasLeaf ? node.IsOff ? DirectoryMode.Recursive : DirectoryMode.Off :
            (DirectoryMode)(((int)node.State + (reverse ? 2 : 1)) % 3);
        ConfigurationSection section = document!.Folders[node.FolderIndex];
        Configuration config = document.Validate();
        Folder folder = config.Folders[node.FolderIndex];
        List<PathOverride> selections = (folder.PathOverrides.IsDefault ? [] : folder.PathOverrides)
            .Where(p => !Paths.Within(p.Path, node.Path)).ToList();
        selections.Add(new(node.Path, next));
        foreach ((string key, DirectoryMode mode) in new[] { ("direct_paths", DirectoryMode.Direct), ("recursive_paths", DirectoryMode.Recursive), ("excluded_paths", DirectoryMode.Off) })
            section.Fields[key].Text = string.Join("\n", selections.Where(p => p.Mode == mode).Select(p => p.Path));
        Configuration updated = document.Validate();
        treeLayout!.Update(updated);
        treeStale = false; treeStatus = "DRAFT — selection changed. Save to apply; inactive branches stay gray and hidden below their first level.";
        Notify(nameof(TreeStatus)); Notify(nameof(CanEditTree));
        if (next != DirectoryMode.Off) { await node.LoadChildrenAsync(); node.Expanded = true; }
    }
}
