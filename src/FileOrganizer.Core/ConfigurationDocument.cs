using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using Tomlyn;
using Tomlyn.Syntax;

namespace FileOrganizer.Core;

public enum FieldKind { Text, Boolean, Integer, List }

public sealed class ConfigurationField : INotifyPropertyChanged
{
    private string text;
    private readonly string initial;
    public string Key { get; }
    public FieldKind Kind { get; }
    internal ValueSyntax? Node { get; }
    public bool Changed => text != initial;
    public string Text
    {
        get => text;
        set { if (text == value) return; text = value; Notify(); Notify(nameof(Flag)); }
    }
    public bool Flag { get => text == "true"; set => Text = value ? "true" : "false"; }
    public event PropertyChangedEventHandler? PropertyChanged;
    private void Notify([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    internal ConfigurationField(string key, FieldKind kind, string fallback, ValueSyntax? node)
    {
        Key = key; Kind = kind; Node = node;
        text = initial = node switch
        {
            StringValueSyntax s => s.Value ?? "",
            BooleanValueSyntax b => b.Value ? "true" : "false",
            IntegerValueSyntax i => i.Value.ToString(CultureInfo.InvariantCulture),
            ArraySyntax a when kind == FieldKind.List => string.Join("\n", a.Items.Select(i => i.Value is StringValueSyntax s ? s.Value : i.Value?.ToString())),
            null => fallback,
            _ => node.ToString().Trim()
        };
        if (key is "log_level" or "duplicates.name_collision" or "duplicates.identical_content")
            text = initial = text.ToLowerInvariant();
    }
    public string Literal() => Kind switch
    {
        FieldKind.Text => Quote(Text),
        FieldKind.List => "[" + string.Join(", ", Text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).Select(s => Quote(s.Trim()))) + "]",
        _ => Text.Trim()
    };
    internal static string Quote(string value)
    {
        StringBuilder result = new("\"");
        foreach (char c in value)
            result.Append(c switch
            {
                '\\' => "\\\\", '"' => "\\\"", '\n' => "\\n", '\r' => "\\r", '\t' => "\\t",
                _ when c < 32 || c == 127 => "\\u" + ((int)c).ToString("X4", CultureInfo.InvariantCulture),
                _ => c.ToString()
            });
        return result.Append('"').ToString();
    }
}

public sealed class ConfigurationSection : INotifyPropertyChanged
{
    public Dictionary<string, ConfigurationField> Fields { get; } = new(StringComparer.Ordinal);
    public ObservableCollection<ConfigurationSection> Rules { get; } = [];
    public string DisplayName => Fields.TryGetValue("name", out ConfigurationField? name) ? name.Text : "General";
    internal string Location { get; }
    internal int Start { get; set; } = -1;
    internal int End { get; set; } = -1;
    internal List<ConfigurationSection> OriginalRules { get; } = [];
    public event PropertyChangedEventHandler? PropertyChanged;
    internal ConfigurationSection(string location) { Location = location; }
    internal void Add(string key, FieldKind kind, string fallback, ValueSyntax? node, Action changed)
    {
        ConfigurationField field = new(key, kind, fallback, node);
        field.PropertyChanged += (_, _) => { changed(); PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(DisplayName))); };
        Fields.Add(key, field);
    }
}

public sealed class ConfigurationDocument
{
    private sealed record Table(string Location, TableSyntaxBase? Syntax, int Start, int BodyEnd);
    private sealed record Edit(int Start, int Length, string Text);
    private readonly Dictionary<string, ValueSyntax> values = new(StringComparer.Ordinal);
    private readonly List<Table> tables = [];
    private readonly List<ConfigurationSection> originalFolders = [];
    private readonly string source;
    private readonly string newline;
    private bool structuralChange;
    public ConfigurationSection General { get; }
    public ObservableCollection<ConfigurationSection> Folders { get; } = [];
    public event EventHandler? Changed;
    public bool IsDirty => structuralChange || General.Fields.Values.Any(f => f.Changed) ||
        Folders.Any(f => f.Fields.Values.Any(p => p.Changed) || f.Rules.Any(r => r.Fields.Values.Any(p => p.Changed)));

    public ConfigurationDocument(string text)
    {
        source = text;
        newline = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        DocumentSyntax syntax = Toml.Parse(text);
        if (syntax.HasErrors) throw new ConfigException(syntax.Diagnostics.ToString());
        IndexValues("", syntax.KeyValues);
        Dictionary<string, int> indices = new(StringComparer.Ordinal);
        List<(string Path, TableSyntaxBase Syntax, int Start)> headers = [];
        foreach (TableSyntaxBase table in syntax.Tables)
        {
            string[] keys = Keys(table.Name!);
            string location = "";
            for (int i = 0; i < keys.Length; i++)
            {
                string next = Join(location, keys[i]);
                if (i == keys.Length - 1 && table is TableArraySyntax)
                { int index = indices.TryGetValue(next, out int old) ? old + 1 : 0; indices[next] = index; location = next + "#" + index; }
                else location = indices.TryGetValue(next, out int index) ? next + "#" + index : next;
            }
            headers.Add((location, table, BlockStart(table.OpenBracket!.Span.Offset)));
            IndexValues(location, table.Items);
        }
        tables.Add(new Table("", null, 0, headers.Count > 0 ? headers[0].Start : source.Length));
        for (int i = 0; i < headers.Count; i++)
            tables.Add(new Table(headers[i].Path, headers[i].Syntax, headers[i].Start, i + 1 < headers.Count ? headers[i + 1].Start : source.Length));
        General = MakeSection("general", false, false);
        foreach (Table table in tables.Where(t => t.Syntax is TableArraySyntax && IsFolder(t.Location)))
        {
            ConfigurationSection folder = MakeSection(table.Location, true, false);
            folder.Start = table.Start;
            folder.End = RegionEnd(table.Location, table.Start);
            foreach (Table ruleTable in tables.Where(t => t.Syntax is TableArraySyntax && IsRule(t.Location, table.Location)))
            {
                ConfigurationSection rule = MakeSection(ruleTable.Location, false, true);
                rule.Start = ruleTable.Start; rule.End = RegionEnd(ruleTable.Location, ruleTable.Start);
                folder.Rules.Add(rule); folder.OriginalRules.Add(rule);
            }
            WatchRules(folder); Folders.Add(folder); originalFolders.Add(folder);
        }
        Folders.CollectionChanged += (_, _) => { structuralChange = true; NotifyChanged(); };
    }
    private static bool IsFolder(string path) => path.StartsWith("folders#", StringComparison.Ordinal) && !path.Contains('/');
    private static bool IsRule(string path, string folder) => path.StartsWith(folder + "/rules#", StringComparison.Ordinal) && path[(folder.Length + 1)..].Count(c => c == '/') == 0;
    private void WatchRules(ConfigurationSection folder) => folder.Rules.CollectionChanged += (_, _) => { structuralChange = true; NotifyChanged(); };
    private void NotifyChanged() => Changed?.Invoke(this, EventArgs.Empty);
    private int RegionEnd(string path, int start) => tables.FirstOrDefault(t => t.Start > start && t.Location != path && !t.Location.StartsWith(path + "/", StringComparison.Ordinal))?.Start ?? source.Length;
    private ConfigurationSection MakeSection(string path, bool folder, bool rule)
    {
        ConfigurationSection section = new(path);
        void Field(string key, FieldKind kind, string fallback) => section.Add(key, kind, fallback, values.GetValueOrDefault(Join(path, key.Replace('.', '/'))), NotifyChanged);
        if (folder || rule)
        {
            Field("name", FieldKind.Text, folder ? "New folder" : "New rule");
            Field("enabled", FieldKind.Boolean, "true");
        }
        if (folder)
        {
            Field("path", FieldKind.Text, ""); Field("recursive", FieldKind.Boolean, "false");
            Field("direct_paths", FieldKind.List, ""); Field("recursive_paths", FieldKind.List, ""); Field("excluded_paths", FieldKind.List, "");
            Field("duplicates.name_collision", FieldKind.Text, "increment");
            Field("duplicates.identical_content", FieldKind.Text, "copy");
            Field("duplicates.copy_suffix", FieldKind.Text, "_copy_{n}");
        }
        else if (rule)
        {
            Field("destination", FieldKind.Text, ""); Field("extensions", FieldKind.List, "");
            Field("patterns", FieldKind.List, ""); Field("exclude_patterns", FieldKind.List, "");
            Field("case_sensitive", FieldKind.Boolean, "false");
        }
        else
        {
            Field("scan_on_startup", FieldKind.Boolean, "true"); Field("watch_config_changes", FieldKind.Boolean, "true");
            Field("dry_run", FieldKind.Boolean, "false"); Field("log_level", FieldKind.Text, "warning");
            Field("file_ready_timeout_seconds", FieldKind.Integer, "120"); Field("max_parallel_moves", FieldKind.Integer, "2");
        }
        return section;
    }
    public ConfigurationSection AddFolder()
    {
        ConfigurationSection folder = MakeSection("new-folder-" + Guid.NewGuid().ToString("N"), true, false);
        folder.Fields["enabled"].Flag = false; WatchRules(folder); Folders.Add(folder); AddRule(folder);
        return folder;
    }
    public ConfigurationSection AddRule(ConfigurationSection folder)
    {
        ConfigurationSection rule = MakeSection("new-rule-" + Guid.NewGuid().ToString("N"), false, true);
        folder.Rules.Add(rule); return rule;
    }
    public ConfigurationSection DuplicateRule(ConfigurationSection folder, ConfigurationSection original)
    {
        ConfigurationSection copy = MakeSection(original.Location, false, true);
        copy.Start = original.Start; copy.End = original.End;
        foreach (string key in copy.Fields.Keys) copy.Fields[key].Text = original.Fields[key].Text;
        copy.Fields["name"].Text += " (copy)";
        folder.Rules.Insert(folder.Rules.IndexOf(original) + 1, copy); return copy;
    }
    public string Render() => RenderContainer(0, source.Length, originalFolders, Folders, General);
    public Configuration Validate() => ConfigurationLoader.Parse(Render());
    private string RenderSection(ConfigurationSection section, bool folder)
    {
        if (section.Start < 0)
        {
            StringBuilder text = new(newline + (folder ? "[[folders]]" : "[[folders.rules]]") + newline);
            foreach (ConfigurationField field in section.Fields.Values) text.Append(field.Key).Append(" = ").Append(field.Literal()).Append(newline);
            if (folder) foreach (ConfigurationSection rule in section.Rules) text.Append(RenderSection(rule, false));
            return text.ToString();
        }
        return folder ? RenderContainer(section.Start, section.End, section.OriginalRules, section.Rules, section)
            : Apply(section.Start, section.End, Edits(section));
    }
    private string RenderContainer(int start, int end, IReadOnlyList<ConfigurationSection> slots,
        IList<ConfigurationSection> desired, ConfigurationSection owner)
    {
        List<Edit> edits = Edits(owner);
        for (int i = 0; i < slots.Count; i++)
            edits.Add(new Edit(slots[i].Start, slots[i].End - slots[i].Start, i < desired.Count ? RenderSection(desired[i], owner == General) : ""));
        if (desired.Count > slots.Count)
        {
            int position = slots.Count > 0 ? slots[^1].End : end;
            edits.Add(new Edit(position, 0, string.Concat(desired.Skip(slots.Count).Select(s => RenderSection(s, owner == General)))));
        }
        return Apply(start, end, edits);
    }
    private List<Edit> Edits(ConfigurationSection section)
    {
        List<Edit> edits = [];
        Dictionary<int, StringBuilder> insertions = [];
        foreach (ConfigurationField field in section.Fields.Values.Where(f => f.Changed))
        {
            if (field.Node is not null) { edits.Add(new Edit(field.Node.Span.Offset, field.Node.Span.Length, field.Literal())); continue; }
            string full = Join(section.Location, field.Key.Replace('.', '/'));
            string parent = full[..full.LastIndexOf('/')];
            string tail = full[(full.LastIndexOf('/') + 1)..];
            while (!values.ContainsKey(parent) && !tables.Any(t => t.Location == parent) && parent.Contains('/'))
            { tail = parent[(parent.LastIndexOf('/') + 1)..] + "." + tail; parent = parent[..parent.LastIndexOf('/')]; }
            int position;
            string insertion;
            if (values.TryGetValue(parent, out ValueSyntax? value) && value is InlineTableSyntax inline)
            {
                position = inline.CloseBrace!.Span.Offset;
                insertion = (inline.Items.Any() || insertions.ContainsKey(position) ? ", " : "") + tail + " = " + field.Literal();
            }
            else
            {
                Table? table = tables.FirstOrDefault(t => t.Location == parent);
                if (table is null) { table = tables[0]; tail = parent.Replace('/', '.') + "." + tail; }
                position = table.BodyEnd;
                insertion = newline + tail + " = " + field.Literal() + newline;
            }
            if (!insertions.TryGetValue(position, out StringBuilder? builder)) insertions[position] = builder = new();
            builder.Append(insertion);
        }
        edits.AddRange(insertions.Select(p => new Edit(p.Key, 0, p.Value.ToString())));
        return edits;
    }
    private string Apply(int start, int end, List<Edit> edits)
    {
        StringBuilder text = new(source[start..end]);
        foreach (Edit edit in edits.OrderByDescending(e => e.Start).ThenByDescending(e => e.Length).ThenByDescending(e => edits.IndexOf(e)))
        {
            if (edit.Start < start || edit.Start + edit.Length > end) throw new ConfigException("Unsupported document layout; use the TOML tab to edit safely");
            text.Remove(edit.Start - start, edit.Length).Insert(edit.Start - start, edit.Text);
        }
        return text.ToString();
    }
    private void IndexValues(string parent, IEnumerable<KeyValueSyntax> items)
    {
        foreach (KeyValueSyntax item in items)
        {
            string path = Join(parent, string.Join('/', Keys(item.Key!)));
            values[path] = item.Value!;
            if (item.Value is InlineTableSyntax inline) IndexValues(path, inline.Items.Select(i => i.KeyValue!));
        }
    }
    private int BlockStart(int offset)
    {
        int start = source.LastIndexOf('\n', Math.Max(0, offset - 1)); start = start < 0 ? 0 : start + 1;
        while (start > 0)
        {
            int previous = source.LastIndexOf('\n', Math.Max(0, start - 2)); previous = previous < 0 ? 0 : previous + 1;
            string line = source[previous..start].Trim();
            if (line.Length != 0 && !line.StartsWith('#')) break;
            start = previous;
        }
        return start;
    }
    private static string Join(string parent, string key) => parent.Length == 0 ? key : parent + "/" + key;
    private static string[] Keys(KeySyntax key) => new[] { Key(key.Key!) }.Concat(key.DotKeys.Select(k => Key(k.Key!))).ToArray();
    private static string Key(BareKeyOrStringValueSyntax key) => key is StringValueSyntax s ? s.Value! : ((BareKeySyntax)key).Key!.Text!;
}
