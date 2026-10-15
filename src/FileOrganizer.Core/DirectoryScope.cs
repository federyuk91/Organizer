using System.Collections.Immutable;

namespace FileOrganizer.Core;

// The same directory policy drives enumeration, event admission and the configuration tree.
public sealed class DirectoryScope
{
    public Folder Folder { get; }
    public ImmutableArray<string> BaseRoots { get; }
    public ImmutableArray<PathOverride> Overrides { get; }
    public ImmutableArray<string> ExplicitRoots { get; }
    private readonly HashSet<string> connectingPaths = new(Paths.Comparer);
    public DirectoryScope(Folder folder, IEnumerable<string>? destinations = null)
    {
        Folder = folder; Overrides = folder.PathOverrides.IsDefault ? [] : folder.PathOverrides;
        BaseRoots = (destinations ?? folder.Rules.Where(r => r.Enabled).Select(r => r.Destination))
            .Prepend(folder.Path).Distinct(Paths.Comparer).ToImmutableArray();
        ImmutableArray<string> declared = BaseRoots.Concat(Overrides.Where(p => p.Mode != DirectoryMode.Off).Select(p => p.Path))
            .Distinct(Paths.Comparer).ToImmutableArray();
        foreach (string target in declared.Where(p => ConfiguredMode(p) != DirectoryMode.Off))
        {
            string? anchor = declared.Where(p => !Paths.Comparer.Equals(p, target) && Paths.Within(target, p) && ConfiguredMode(p) != DirectoryMode.Off)
                .MinBy(p => p.Length);
            if (anchor is null) continue;
            for (string? parent = Path.GetDirectoryName(target); parent is not null && !Paths.Comparer.Equals(parent, anchor); parent = Path.GetDirectoryName(parent))
                if (ConfiguredMode(parent) == DirectoryMode.Off && !Overrides.Any(p => p.Mode == DirectoryMode.Off && Paths.Within(parent, p.Path))) connectingPaths.Add(parent);
        }
        ExplicitRoots = declared.Concat(connectingPaths).Distinct(Paths.Comparer).ToImmutableArray();
    }
    public DirectoryMode Mode(string directory) => connectingPaths.Contains(directory) ? DirectoryMode.Direct : ConfiguredMode(directory);
    private DirectoryMode ConfiguredMode(string directory)
    {
        if (!Folder.Enabled || Overrides.Any(p => p.Mode == DirectoryMode.Off && Paths.Within(directory, p.Path))) return DirectoryMode.Off;
        PathOverride? selection = Overrides.Where(p => Paths.Within(directory, p.Path)).MaxBy(p => p.Path.Length);
        if (selection is not null)
        {
            if (Paths.Comparer.Equals(selection.Path, directory) || selection.Mode == DirectoryMode.Recursive) return selection.Mode;
            return DirectoryMode.Off;
        }
        if (BaseRoots.Contains(directory, Paths.Comparer)) return Folder.Recursive ? DirectoryMode.Recursive : DirectoryMode.Direct;
        return Folder.Recursive && BaseRoots.Any(p => Paths.Within(directory, p)) ? DirectoryMode.Recursive : DirectoryMode.Off;
    }
    public bool Complete(string directory) => Mode(directory) == DirectoryMode.Recursive &&
        !Overrides.Any(p => !Paths.Comparer.Equals(p.Path, directory) && Paths.Within(p.Path, directory) && p.Mode != DirectoryMode.Recursive);
    public IEnumerable<string> ActiveRoots => ExplicitRoots.Where(p => Mode(p) != DirectoryMode.Off);
}
