using Tomlyn.Model;
using Tomlyn.Syntax;

namespace FileOrganizer.Core;

// Read the validated syntax tree directly; Tomlyn's object mapper requires reflection under AOT.
internal static class TomlData
{
    public static TomlTable Read(DocumentSyntax document)
    {
        TomlTable root = new();
        Fill(root, document.KeyValues);
        foreach (TableSyntaxBase table in document.Tables)
        {
            string[] keys = Keys(table.Name!);
            TomlTable parent = Parent(root, keys);
            string last = keys[^1];
            TomlTable target;
            if (table is TableArraySyntax)
            {
                if (!parent.TryGetValue(last, out object? existing)) parent[last] = existing = new TomlTableArray();
                if (existing is not TomlTableArray array) throw new ConfigException("Expected table array: " + last);
                target = new TomlTable();
                array.Add(target);
            }
            else
            {
                if (!parent.TryGetValue(last, out object? existing)) parent[last] = existing = new TomlTable();
                target = existing as TomlTable ?? throw new ConfigException("Expected table: " + last);
            }
            Fill(target, table.Items);
        }
        return root;
    }
    private static void Fill(TomlTable table, IEnumerable<KeyValueSyntax> items)
    {
        foreach (KeyValueSyntax item in items)
        {
            string[] keys = Keys(item.Key!);
            TomlTable parent = Parent(table, keys);
            if (parent.ContainsKey(keys[^1])) throw new ConfigException("Duplicate key: " + keys[^1]);
            parent.Add(keys[^1], Value(item.Value!));
        }
    }
    private static TomlTable Parent(TomlTable root, string[] keys)
    {
        TomlTable current = root;
        for (int i = 0; i < keys.Length - 1; i++)
        {
            if (!current.TryGetValue(keys[i], out object? child)) current[keys[i]] = child = new TomlTable();
            current = child switch
            {
                TomlTable table => table,
                TomlTableArray array when array.Count > 0 => array[^1],
                _ => throw new ConfigException("Invalid table path: " + keys[i])
            };
        }
        return current;
    }
    private static string[] Keys(KeySyntax key)
    {
        List<string> keys = [Key(key.Key!)];
        foreach (DottedKeyItemSyntax dotted in key.DotKeys) keys.Add(Key(dotted.Key!));
        return keys.ToArray();
    }
    private static string Key(BareKeyOrStringValueSyntax key) => key switch
    {
        StringValueSyntax text => text.Value!,
        BareKeySyntax bare => bare.Key!.Text!,
        _ => throw new ConfigException("Invalid TOML key")
    };
    private static object Value(ValueSyntax value)
    {
        switch (value)
        {
            case StringValueSyntax text: return text.Value!;
            case IntegerValueSyntax integer: return integer.Value;
            case BooleanValueSyntax boolean: return boolean.Value;
            case ArraySyntax array:
                TomlArray values = new();
                foreach (ArrayItemSyntax item in array.Items) values.Add(Value(item.Value!));
                return values;
            case InlineTableSyntax inline:
                TomlTable table = new(true);
                Fill(table, inline.Items.Select(i => i.KeyValue!));
                return table;
            default: throw new ConfigException("Unsupported configuration value at " + value.Span);
        }
    }
}
