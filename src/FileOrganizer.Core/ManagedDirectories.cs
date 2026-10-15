using System.Collections.Immutable;

namespace FileOrganizer.Core;

public sealed record ManagedFolder(Folder Folder, ImmutableArray<string> Destinations, ImmutableArray<string> Roots);

// Directory ownership is configuration/session state, never file provenance.
public sealed class ManagedDirectories
{
    public Configuration Configuration { get; }
    public ImmutableArray<ManagedFolder> Folders { get; }
    public ImmutableArray<string> WatchRoots { get; }

    public ManagedDirectories(Configuration configuration, ManagedDirectories? previous = null)
    {
        Configuration = configuration;
        ImmutableArray<ManagedFolder>.Builder folders = ImmutableArray.CreateBuilder<ManagedFolder>();
        foreach (Folder folder in configuration.Folders.Where(f => f.Enabled))
        {
            IEnumerable<string> current = folder.Rules.Where(r => r.Enabled).Select(r => r.Destination);
            ManagedFolder? old = previous?.Folders.FirstOrDefault(f => Paths.Comparer.Equals(f.Folder.Path, folder.Path));
            ImmutableArray<string> destinations = current.Concat(old?.Destinations ?? []).Distinct(Paths.Comparer).ToImmutableArray();
            ImmutableArray<string> roots = destinations.Where(d => !destinations.Any(other => !Paths.Comparer.Equals(other, d) && Paths.Within(d, other))).ToImmutableArray();
            folders.Add(new ManagedFolder(folder, destinations, roots));
        }
        Folders = folders.ToImmutable();
        ValidateOwnership(Folders, configuration);
        WatchRoots = Folders.SelectMany(f => f.Roots.Prepend(f.Folder.Path)).Distinct(Paths.Comparer).ToImmutableArray();
    }

    internal static void ValidateDestinations(Configuration config)
    {
        ManagedFolder[] folders = config.Folders.Where(f => f.Enabled).Select(f =>
            new ManagedFolder(f, f.Rules.Where(r => r.Enabled).Select(r => r.Destination).Distinct(Paths.Comparer).ToImmutableArray(), [])).ToArray();
        ValidateOwnership(folders, config);
    }
    private static void ValidateOwnership(IEnumerable<ManagedFolder> scopes, Configuration config)
    {
        ManagedFolder[] folders = scopes.ToArray();
        foreach (ManagedFolder owner in folders)
        foreach (string directory in owner.Destinations)
        {
            Paths.NoReparse(directory);
            if (File.Exists(directory)) throw new ConfigException("Managed destination is a file: " + directory);
            foreach (Folder source in config.Folders.Where(f => f.Enabled))
                if (Paths.Within(source.Path, directory)) throw new ConfigException("Managed destination equals or contains a source: " + directory);
            foreach (ManagedFolder other in folders)
                if (!ReferenceEquals(owner, other) && other.Destinations.Any(d => Paths.Within(d, directory) || Paths.Within(directory, d)))
                    throw new ConfigException("Destinations belonging to different monitors overlap: " + owner.Folder.Name + ", " + other.Folder.Name);
        }
    }
    public Folder? Owner(string path)
    {
        foreach (ManagedFolder folder in Folders)
            if (folder.Roots.Any(root => Paths.Within(path, root))) return folder.Folder;
        foreach (ManagedFolder folder in Folders)
            if (!Paths.Comparer.Equals(path, folder.Folder.Path) && Paths.Within(path, folder.Folder.Path) &&
                (folder.Folder.Recursive || Paths.Comparer.Equals(Path.GetDirectoryName(path), folder.Folder.Path))) return folder.Folder;
        return null;
    }
    public static bool AlreadyPlaced(string path, Rule rule) => Paths.Comparer.Equals(Path.GetDirectoryName(path), rule.Destination);
    public bool IsKnownDirectory(string path) => Folders.Any(f => Paths.Comparer.Equals(path, f.Folder.Path) || f.Destinations.Contains(path, Paths.Comparer));
    public bool AffectsWatchRoot(string path) => WatchRoots.Any(root => Paths.Within(root, path));

    public IEnumerable<string> Snapshot(Folder folder, string? configPath = null)
    {
        ManagedFolder scope = Folders.First(f => ReferenceEquals(f.Folder, folder));
        foreach (string file in Enumerate(folder.Path, folder.Recursive, false, folder, configPath)) yield return file;
        foreach (string root in scope.Roots)
        {
            if (!Directory.Exists(root)) continue;
            foreach (string file in Enumerate(root, true, true, folder, configPath)) yield return file;
        }
    }
    private IEnumerable<string> Enumerate(string root, bool recursive, bool managed, Folder owner, string? configPath)
    {
        Stack<string> pending = new(); pending.Push(root);
        while (pending.TryPop(out string? directory))
        {
            Paths.NoReparse(directory);
            foreach (string entry in Directory.EnumerateFileSystemEntries(directory))
            {
                if (Paths.Comparer.Equals(entry, configPath) || Paths.Comparer.Equals(entry, configPath + ".bak")) continue;
                FileAttributes attributes;
                try { attributes = File.GetAttributes(entry); }
                catch (FileNotFoundException) { continue; }
                catch (DirectoryNotFoundException) { continue; }
                if ((attributes & FileAttributes.ReparsePoint) != 0) continue;
                if ((attributes & FileAttributes.Directory) != 0)
                {
                    if (recursive && (managed || !Folders.Any(f => f.Roots.Any(d => Paths.Within(entry, d))))) pending.Push(entry);
                }
                else if (ReferenceEquals(Owner(entry), owner)) yield return entry;
            }
        }
    }
}
