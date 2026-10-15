using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace FileOrganizer.Core;

public static class Paths
{
    public static readonly StringComparer Comparer = StringComparer.OrdinalIgnoreCase;
    public static string Normalize(string path, string? relativeTo = null)
        => NormalizeCore(path, relativeTo, true);
    public static string NormalizeLiteral(string path, string? relativeTo = null)
        => NormalizeCore(path, relativeTo, false);
    private static string NormalizeCore(string path, string? relativeTo, bool expandVariables)
    {
        if (expandVariables)
        {
            path = Environment.ExpandEnvironmentVariables(path);
            if (System.Text.RegularExpressions.Regex.IsMatch(path, "%[^%]+%")) throw new ConfigException("Unresolved environment variable: " + path);
        }
        if (!Path.IsPathFullyQualified(path))
        {
            if (Path.IsPathRooted(path)) throw new ConfigException("Partially rooted path is ambiguous: " + path);
            if (relativeTo is null) throw new ConfigException("Expected absolute path: " + path);
            path = Path.Combine(relativeTo, path);
        }
        string full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        if (OperatingSystem.IsWindows() && (full.StartsWith(@"\\") || full.IndexOf(':', 2) >= 0))
            throw new ConfigException("Only local drive paths without alternate streams are supported: " + full);
        foreach (string segment in full[(Path.GetPathRoot(full)?.Length ?? 0)..].Split(Path.DirectorySeparatorChar))
            if (segment.EndsWith(' ') || segment.EndsWith('.') || segment.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
                OperatingSystem.IsWindows() && IsDeviceName(segment))
                throw new ConfigException("Ambiguous or invalid path segment: " + full);
        if (OperatingSystem.IsWindows())
        {
            NoReparse(full);
            full = ExpandShortNames(full);
        }
        return full;
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetLongPathNameW(string shortPath, StringBuilder longPath, uint bufferLength);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string path, uint access, uint share, nint security, uint creation, uint flags, nint template);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandleW(SafeFileHandle handle, StringBuilder path, uint length, uint flags);
    private static string ExpandShortNames(string path)
    {
        string existing = path;
        Stack<string> missing = new();
        while (!Directory.Exists(existing) && !File.Exists(existing))
        {
            missing.Push(Path.GetFileName(existing));
            string? parent = Path.GetDirectoryName(existing);
            if (parent is null) return path;
            existing = parent;
        }
        StringBuilder buffer = new(32768);
        uint length = GetLongPathNameW(@"\\?\" + existing, buffer, (uint)buffer.Capacity);
        if (length == 0 || length >= buffer.Capacity) throw new ConfigException("Cannot canonicalize path: " + path + ": " + new Win32Exception(Marshal.GetLastWin32Error()).Message);
        string result = buffer.ToString();
        if (result.StartsWith(@"\\?\")) result = result[4..];
        using SafeFileHandle handle = CreateFileW(@"\\?\" + result, 0x80, 7, 0, 3, 0x02200000, 0);
        if (handle.IsInvalid) throw new ConfigException("Cannot inspect path identity: " + result);
        buffer.Clear();
        length = GetFinalPathNameByHandleW(handle, buffer, (uint)buffer.Capacity, 0);
        if (length == 0 || length >= buffer.Capacity) throw new ConfigException("Cannot resolve final path: " + result);
        result = buffer.ToString();
        if (!result.StartsWith(@"\\?\") || result.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase))
            throw new ConfigException("Only local DOS drive paths are supported: " + result);
        result = result[4..];
        while (missing.TryPop(out string? segment)) result = Path.Combine(result, segment);
        return Path.TrimEndingDirectorySeparator(result);
    }
    private static bool IsDeviceName(string segment)
    {
        string stem = segment.Split('.')[0].ToUpperInvariant();
        return stem is "CON" or "PRN" or "AUX" or "NUL" or "CONIN$" or "CONOUT$" ||
            stem.Length == 4 && (stem.StartsWith("COM") || stem.StartsWith("LPT")) && "123456789¹²³".Contains(stem[3]);
    }
    public static bool Within(string path, string directory) => Comparer.Equals(path, directory) ||
        path.StartsWith(Path.TrimEndingDirectorySeparator(directory) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    public static void NoReparse(string path)
    {
        string? current = path;
        while (current is not null)
        {
            try
            {
                FileAttributes attributes = File.GetAttributes(current);
                if ((attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("Reparse points are not supported: " + current);
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
            current = Path.GetDirectoryName(current);
        }
    }
    public static void Validate(Configuration config)
    {
        Folder[] sources = config.Folders.Where(f => f.Enabled).ToArray();
        foreach (Folder source in sources)
        {
            NoReparse(source.Path);
            if (!Directory.Exists(source.Path)) throw new ConfigException("Source does not exist: " + source.Path);
            using IEnumerator<string> accessCheck = Directory.EnumerateFileSystemEntries(source.Path).GetEnumerator();
            accessCheck.MoveNext();
            foreach (Folder other in sources)
                if (!ReferenceEquals(source, other) && (Comparer.Equals(source.Path, other.Path) ||
                    source.Recursive && Within(other.Path, source.Path) || other.Recursive && Within(source.Path, other.Path)))
                    throw new ConfigException("Duplicate/overlapping monitors: " + source.Name + ", " + other.Name);
        }
        foreach (string destination in config.Destinations)
        {
            NoReparse(destination);
            if (File.Exists(destination)) throw new ConfigException("Destination is a file: " + destination);
            foreach (Folder source in sources)
                if (Comparer.Equals(destination, source.Path) || Within(source.Path, destination))
                    throw new ConfigException("Destination equals or contains a source: " + destination);
        }
        ManagedDirectories.ValidateDestinations(config);
    }
    public static bool Eligible(Configuration config, Folder folder, string path, string? configPath = null)
    {
        if (!folder.Enabled || Comparer.Equals(path, configPath) || !Within(path, folder.Path) || Comparer.Equals(path, folder.Path)) return false;
        if (!folder.Recursive && !Comparer.Equals(Path.GetDirectoryName(path), folder.Path)) return false;
        return !config.Destinations.Any(d => Within(path, d));
    }
    public static IEnumerable<string> Snapshot(Configuration config, Folder folder, string? configPath = null)
    {
        Stack<string> pending = new();
        pending.Push(folder.Path);
        while (pending.TryPop(out string? directory))
        {
            NoReparse(directory);
            foreach (string entry in Directory.EnumerateFileSystemEntries(directory))
            {
                if (!Eligible(config, folder, entry, configPath)) continue;
                FileAttributes attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.ReparsePoint) != 0) continue;
                if ((attributes & FileAttributes.Directory) == 0) yield return entry;
                else if (folder.Recursive) pending.Push(entry);
            }
        }
    }
}
