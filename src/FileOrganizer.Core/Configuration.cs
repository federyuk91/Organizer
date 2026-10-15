using System.Collections.Immutable;
using Tomlyn;
using Tomlyn.Model;

namespace FileOrganizer.Core;

public enum LogLevel { Error, Warning, Info, Debug }
public enum NameCollision { Increment, Skip }
public enum IdenticalContent { Copy, Skip, Delete }
public sealed record General(bool ScanOnStartup = true, bool WatchConfigChanges = true,
    bool DryRun = false, LogLevel LogLevel = LogLevel.Warning, int ReadyTimeoutSeconds = 120, int ParallelMoves = 2);
public sealed record DuplicatePolicy(NameCollision NameCollision = NameCollision.Increment,
    IdenticalContent IdenticalContent = IdenticalContent.Copy, string CopySuffix = "_copy_{n}");
public sealed record Rule(string Name, bool Enabled, string Destination, ImmutableArray<string> Extensions,
    ImmutableArray<string> Patterns, ImmutableArray<string> Exclusions, bool CaseSensitive);
public sealed record Folder(string Name, string Path, bool Enabled, bool Recursive,
    DuplicatePolicy Duplicates, ImmutableArray<Rule> Rules);
public sealed record Configuration(General General, ImmutableArray<Folder> Folders)
{
    public IEnumerable<string> Destinations => Folders.Where(f => f.Enabled)
        .SelectMany(f => f.Rules.Where(r => r.Enabled).Select(r => r.Destination));
}

public sealed class ConfigException(string message) : Exception(message);

public static class ConfigurationLoader
{
    public static Configuration Load(string path) => Parse(File.ReadAllText(path));

    public static Configuration Parse(string text)
    {
        Tomlyn.Syntax.DocumentSyntax syntax = Toml.Parse(text);
        if (syntax.HasErrors) throw new ConfigException(syntax.Diagnostics.ToString());
        TomlTable root = TomlData.Read(syntax);
        CheckKeys(root, "config", "version", "general", "folders");
        if (Get<long>(root, "version", "config") != 1) throw new ConfigException("version: expected 1");
        TomlTable general = Table(root, "general", "config", false);
        CheckKeys(general, "general", "scan_on_startup", "watch_config_changes", "dry_run", "log_level",
            "file_ready_timeout_seconds", "max_parallel_moves");
        General settings = new(Bool(general, "scan_on_startup", true), Bool(general, "watch_config_changes", true),
            Bool(general, "dry_run", false), EnumValue(general, "log_level", LogLevel.Warning),
            Positive(general, "file_ready_timeout_seconds", 120, 86400), Positive(general, "max_parallel_moves", 2, 16));
        if (!root.TryGetValue("folders", out object? value) || value is not TomlTableArray folders || folders.Count == 0)
            throw new ConfigException("folders: at least one [[folders]] is required");
        ImmutableArray<Folder>.Builder result = ImmutableArray.CreateBuilder<Folder>();
        HashSet<string> names = new(StringComparer.OrdinalIgnoreCase);
        foreach (TomlTable table in folders)
        {
            CheckKeys(table, "folder", "name", "path", "enabled", "recursive", "duplicates", "rules");
            string name = Get<string>(table, "name", "folder");
            if (!names.Add(name)) throw new ConfigException($"folders.{name}: duplicate name");
            string source = Paths.Normalize(Get<string>(table, "path", name));
            TomlTable duplicates = Table(table, "duplicates", name, false);
            CheckKeys(duplicates, name + ".duplicates", "name_collision", "identical_content", "copy_suffix");
            DuplicatePolicy policy = new(EnumValue(duplicates, "name_collision", NameCollision.Increment),
                EnumValue(duplicates, "identical_content", IdenticalContent.Copy),
                Optional(duplicates, "copy_suffix", "_copy_{n}"));
            string suffix = policy.CopySuffix;
            if (suffix.Split("{n}").Length != 2 || suffix.Replace("{n}", "").IndexOfAny(['{', '}', '/', '\\', ':']) >= 0 ||
                suffix.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || suffix.EndsWith('.') || suffix.EndsWith(' '))
                throw new ConfigException(name + ".copy_suffix: expected a filename suffix with exactly one {n}");
            ImmutableArray<Rule>.Builder rules = ImmutableArray.CreateBuilder<Rule>();
            if (!table.TryGetValue("rules", out object? ruleValue) || ruleValue is not TomlTableArray ruleTables || ruleTables.Count == 0)
                throw new ConfigException(name + ": at least one [[folders.rules]] is required");
            foreach (TomlTable item in ruleTables)
            {
                CheckKeys(item, name + ".rule", "name", "enabled", "destination", "extensions", "patterns", "exclude_patterns", "case_sensitive");
                string ruleName = Get<string>(item, "name", name + ".rule");
                ImmutableArray<string> extensions = Strings(item, "extensions").Select(e => e.StartsWith('.') ? e : "." + e).ToImmutableArray();
                ImmutableArray<string> patterns = Strings(item, "patterns");
                ImmutableArray<string> excludes = Strings(item, "exclude_patterns");
                if (extensions.Length == 0 && patterns.Length == 0) throw new ConfigException(ruleName + ": extensions or patterns required");
                foreach (string extension in extensions)
                    if (extension.Length < 2 || extension.IndexOfAny(['*', '?', '/', '\\', ':']) >= 0)
                        throw new ConfigException(ruleName + ": invalid extension " + extension);
                foreach (string glob in patterns.Concat(excludes))
                    if (glob.IndexOfAny(['/', '\\', ':', '[', ']', '{', '}', '\0']) >= 0)
                        throw new ConfigException(ruleName + ": invalid basename glob " + glob);
                rules.Add(new Rule(ruleName, Bool(item, "enabled", true), Paths.Normalize(Get<string>(item, "destination", ruleName), source),
                    extensions, patterns, excludes, Bool(item, "case_sensitive", false)));
            }
            result.Add(new Folder(name, source, Bool(table, "enabled", true), Bool(table, "recursive", false), policy, rules.ToImmutable()));
        }
        Configuration config = new(settings, result.ToImmutable());
        Paths.Validate(config);
        return config;
    }

    private static T Get<T>(TomlTable table, string key, string location)
    {
        if (!table.TryGetValue(key, out object? value) || value is not T typed || value is string s && string.IsNullOrWhiteSpace(s))
            throw new ConfigException($"{location}.{key}: missing or invalid {typeof(T).Name}");
        return typed;
    }
    private static T Optional<T>(TomlTable table, string key, T fallback) => table.ContainsKey(key) ? Get<T>(table, key, key) : fallback;
    private static bool Bool(TomlTable table, string key, bool fallback) => Optional(table, key, fallback);
    private static int Positive(TomlTable table, string key, int fallback, int max)
    {
        long value = Optional(table, key, (long)fallback);
        if (value < 1 || value > max) throw new ConfigException($"general.{key}: expected 1..{max}");
        return (int)value;
    }
    private static T EnumValue<T>(TomlTable table, string key, T fallback) where T : struct, Enum
    {
        if (!table.ContainsKey(key)) return fallback;
        string value = Get<string>(table, key, key);
        foreach (T candidate in Enum.GetValues<T>())
            if (string.Equals(candidate.ToString(), value, StringComparison.OrdinalIgnoreCase)) return candidate;
        throw new ConfigException(key + ": unknown value " + value);
    }
    private static TomlTable Table(TomlTable table, string key, string location, bool required) =>
        table.ContainsKey(key) || required ? Get<TomlTable>(table, key, location) : new TomlTable();
    private static ImmutableArray<string> Strings(TomlTable table, string key)
    {
        if (!table.ContainsKey(key)) return [];
        TomlArray values = Get<TomlArray>(table, key, key);
        ImmutableArray<string>.Builder result = ImmutableArray.CreateBuilder<string>();
        foreach (object? value in values)
            if (value is string s && s.Length > 0) result.Add(s);
            else throw new ConfigException(key + ": expected nonempty strings");
        return result.ToImmutable();
    }
    private static void CheckKeys(TomlTable table, string location, params string[] keys)
    {
        foreach (string key in table.Keys)
            if (!keys.Contains(key, StringComparer.Ordinal)) throw new ConfigException(location + ": unknown key " + key);
    }
}
