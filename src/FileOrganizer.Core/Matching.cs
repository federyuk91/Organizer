namespace FileOrganizer.Core;

public static class Matching
{
    public static bool IsTemporary(string path) => Path.GetExtension(path).ToLowerInvariant() is ".tmp" or ".part" or ".crdownload";
    public static Rule? First(Folder folder, string path)
    {
        if (!folder.Enabled || IsTemporary(path)) return null;
        string name = Path.GetFileName(path);
        foreach (Rule rule in folder.Rules)
        {
            StringComparison comparison = rule.CaseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
            if (rule.Enabled && (rule.Extensions.IsEmpty || rule.Extensions.Any(e => string.Equals(e, Path.GetExtension(name), comparison))) &&
                (rule.Patterns.IsEmpty || rule.Patterns.Any(p => Glob(name, p, rule.CaseSensitive))) &&
                !rule.Exclusions.Any(p => Glob(name, p, rule.CaseSensitive))) return rule;
        }
        return null;
    }
    // Iterative wildcard matching avoids regex compilation and backtracking allocations.
    public static bool Glob(string name, string pattern, bool caseSensitive = false)
    {
        int n = 0, p = 0, star = -1, retry = 0;
        while (n < name.Length)
        {
            if (p < pattern.Length && (pattern[p] == '?' || Equal(name[n], pattern[p], caseSensitive))) { n++; p++; }
            else if (p < pattern.Length && pattern[p] == '*') { star = p++; retry = n; }
            else if (star >= 0) { p = star + 1; n = ++retry; }
            else return false;
        }
        while (p < pattern.Length && pattern[p] == '*') p++;
        return p == pattern.Length;
    }
    private static bool Equal(char a, char b, bool sensitive) => sensitive ? a == b : char.ToUpperInvariant(a) == char.ToUpperInvariant(b);
}
