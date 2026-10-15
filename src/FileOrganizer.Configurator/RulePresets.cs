using System.Collections.Immutable;
using FileOrganizer.Core;

namespace FileOrganizer.Configurator;

public sealed record RulePreset(string Name, ImmutableArray<string> Extensions)
{
    public string Description => Extensions.IsEmpty ? "Create an empty rule; choose its name, matching criteria and destination." :
        "Extensions: " + string.Join(", ", Extensions) + ". Choose the destination before saving.";
}

public sealed partial class EditorViewModel
{
    public static ImmutableArray<RulePreset> RulePresets { get; } =
    [
        new("New rules", []),
        new("Images", [".jpg", ".jpeg", ".jpe", ".jfif", ".png", ".gif", ".bmp", ".dib", ".tif", ".tiff", ".webp", ".avif", ".heic", ".heif", ".ico", ".svg", ".svgz", ".psd", ".psb", ".tga", ".dds", ".exr", ".hdr", ".dng", ".cr2", ".cr3", ".nef", ".arw", ".orf", ".rw2", ".ase", ".aseprite"]),
        new("Documents", [".pdf", ".txt", ".md", ".rtf", ".doc", ".docx", ".docm", ".dot", ".dotx", ".dotm", ".odt", ".ott", ".pages", ".xls", ".xlsx", ".xlsm", ".xlsb", ".xlt", ".xltx", ".xltm", ".ods", ".csv", ".tsv", ".numbers", ".ppt", ".pptx", ".pptm", ".pps", ".ppsx", ".ppsm", ".pot", ".potx", ".odp", ".key", ".epub", ".mobi", ".djvu", ".xps", ".oxps", ".tex"]),
        new("Archives", [".zip", ".7z", ".rar", ".tar", ".gz", ".gzip", ".tgz", ".bz2", ".tbz", ".tbz2", ".xz", ".txz", ".zst", ".tzst", ".lz", ".lzma", ".lz4", ".cab", ".iso", ".dmg"]),
        new("Videos", [".mp4", ".m4v", ".mkv", ".avi", ".mov", ".wmv", ".webm", ".mpg", ".mpeg", ".mpe", ".m2v", ".3gp", ".3g2", ".mts", ".m2ts", ".vob", ".ogv", ".flv", ".f4v", ".asf", ".mxf"]),
        new("Audio", [".mp3", ".wav", ".flac", ".aac", ".m4a", ".m4b", ".ogg", ".oga", ".opus", ".wma", ".aif", ".aiff", ".aifc", ".alac", ".amr", ".mid", ".midi", ".au", ".mka", ".ape", ".ac3", ".dts", ".caf"]),
        new("3D", [".fbx", ".obj", ".mtl", ".gltf", ".glb", ".blend", ".stl", ".3ds", ".dae", ".ply", ".usd", ".usda", ".usdc", ".usdz", ".abc", ".max", ".ma", ".mb", ".c4d", ".lwo", ".lws", ".3mf", ".step", ".stp", ".iges", ".igs", ".skp", ".x3d", ".wrl"]),
        new("Installers", [".exe", ".msi", ".msix", ".msixbundle", ".appx", ".appxbundle"]),
        new("Fonts", [".ttf", ".otf", ".ttc", ".woff", ".woff2"]),
        new("E-books", [".epub", ".mobi", ".azw", ".azw3", ".fb2"]),
        new("Subtitles", [".srt", ".ass", ".ssa", ".vtt", ".sub"]),
        new("Torrents", [".torrent"]),
        new("Unity Packages", [".unitypackage"]),
        new("Source Code", [".cs", ".py", ".js", ".ts", ".cpp", ".h", ".java", ".shader", ".hlsl"]),
        new("Graphic Projects", [".ase", ".aseprite", ".psd", ".psb", ".kra", ".xcf", ".ora"])
    ];
    private RulePreset selectedRulePreset = RulePresets[0];
    public RulePreset SelectedRulePreset
    {
        get => selectedRulePreset;
        set { selectedRulePreset = value ?? RulePresets[0]; Notify(); }
    }
    private void AddRuleFromPreset()
    {
        ConfigurationSection rule = document!.AddRule(selectedFolder!);
        if (!selectedRulePreset.Extensions.IsEmpty)
        {
            rule.Fields["name"].Text = selectedRulePreset.Name;
            rule.Fields["extensions"].Text = string.Join("\n", selectedRulePreset.Extensions);
        }
        SelectedRule = rule;
    }
}
