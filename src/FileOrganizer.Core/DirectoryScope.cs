using System.Collections.Immutable;

namespace FileOrganizer.Core;

// The same directory policy drives enumeration, event admission and the configuration tree.
public sealed class DirectoryScope(Folder folder, IEnumerable<string>? destinations = null)
{
    public Folder Folder { get; } = folder;
    public ImmutableArray<string> BaseRoots { get; } = (destinations ?? folder.Rules.Where(r => r.Enabled).Select(r => r.Destination))
        .Prepend(folder.Path).Distinct(Paths.Comparer).ToImmutableArray();
    public ImmutableArray<PathOverride> Overrides { get; } = folder.PathOverrides.IsDefault ? [] : folder.PathOverrides;
    public ImmutableArray<string> ExplicitRoots { get; } = (destinations ?? folder.Rules.Where(r => r.Enabled).Select(r => r.Destination))
        .Prepend(folder.Path).Concat((folder.PathOverrides.IsDefault ? [] : folder.PathOverrides).Where(p => p.Mode != DirectoryMode.Off).Select(p => p.Path)).Distinct(Paths.Comparer).ToImmutableArray();
    public DirectoryMode Mode(string directory)
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
