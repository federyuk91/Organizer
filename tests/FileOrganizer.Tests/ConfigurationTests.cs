using FileOrganizer.Core;
using Xunit;

namespace FileOrganizer.Tests;

public sealed class ConfigurationTests
{
    [WindowsTheory]
    [InlineData("version = 1", "version = 2")]
    [InlineData("patterns = [\"*.txt\"]", "patterns = []")]
    [InlineData("[general]", "[general]\nlog_level = 'verbose'")]
    [InlineData("[general]", "[general]\nmax_parallel_moves = 0")]
    [InlineData("[general]", "[general]\ndry_run = 'true'")]
    [InlineData("[general]", "[general]\nunknown = true")]
    [InlineData("destination = ", "missing_destination = ")]
    [InlineData("name = \"text\"", "name = \"text\"\nexclude_patterns = ['foo/bar']")]
    public void T01_InvalidSchemaFails(string oldText, string newText)
    {
        using Fixture f = new();
        Assert.Throws<ConfigException>(() => ConfigurationLoader.Parse(f.Text().Replace(oldText, newText)));
    }
    [WindowsFact]
    public void T01_SampleParsesWithSafeSources()
    {
        using Fixture f = new();
        string repository = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../"));
        string text = File.ReadAllText(Path.Combine(repository, "Config.Example.toml"));
        text = text.Replace("%USERPROFILE%\\Downloads", f.Source);
        Configuration config = ConfigurationLoader.Parse(text);
        Assert.Equal(2, config.Folders.Length);
        Assert.False(config.Folders[1].Enabled);
        Assert.Equal(Path.Combine(f.Source, "Images", "Aseprite"), Matching.First(config.Folders[0], "art.ase")!.Destination);
        Assert.Equal("Images", Matching.First(config.Folders[0], "ART.PNG")!.Name);
        Assert.Null(Matching.First(config.Folders[0], "notes.cs"));
    }
    [WindowsTheory]
    [InlineData("INVOICE.pdf", "*invoice*.pdf", false, true)]
    [InlineData("INVOICE.pdf", "*invoice*.pdf", true, false)]
    [InlineData("a.pdf", "?.pdf", false, true)]
    [InlineData("ab.pdf", "?.pdf", false, false)]
    [InlineData("prefix.pdf.exe", "*.pdf", false, false)]
    [InlineData("", "*", false, true)]
    public void T03_GlobAnchored(string name, string glob, bool sensitive, bool expected) =>
        Assert.Equal(expected, Matching.Glob(name, glob, sensitive));
    [WindowsFact]
    public void T04_AndExclusionAndFirstRule()
    {
        using Fixture f = new();
        Configuration config = f.Config(rule: "extensions = ['txt']\nexclude_patterns = ['draft*']");
        Folder folder = config.Folders[0];
        Assert.NotNull(Matching.First(folder, "foo.txt"));
        Assert.Null(Matching.First(folder, "draft.txt"));
        Assert.Null(Matching.First(folder, "foo.pdf"));
        Assert.Null(Matching.First(folder with { Enabled = false }, "foo.txt"));
        Rule first = folder.Rules[0];
        Assert.Equal("first", Matching.First(folder with { Rules = [first with { Name = "first" }, first] }, "a.txt")!.Name);
        Assert.Null(Matching.First(folder with { Rules = [first with { Enabled = false }] }, "a.txt"));
    }
    [WindowsFact]
    public void T05_RelativeUnicodeAndEnvironment()
    {
        using Fixture f = new();
        Environment.SetEnvironmentVariable("FILEORGANIZER_TEST_SOURCE", f.Source);
        try
        {
            string text = f.Text().Replace(f.Source, "%FILEORGANIZER_TEST_SOURCE%").Replace(f.Destination, "日本語");
            Assert.Equal(Path.Combine(f.Source, "日本語"), ConfigurationLoader.Parse(text).Folders[0].Rules[0].Destination);
        }
        finally { Environment.SetEnvironmentVariable("FILEORGANIZER_TEST_SOURCE", null); }
        Assert.Throws<ConfigException>(() => ConfigurationLoader.Parse(f.Text().Replace(f.Source, "%FILEORGANIZER_MISSING%")));
    }
    [WindowsTheory]
    [InlineData("")]
    [InlineData("\\.")]
    [InlineData("\\child\\..")]
    public void T07_T09_SourceDestinationAliasesRejected(string suffix)
    {
        using Fixture f = new();
        Assert.Throws<ConfigException>(() => ConfigurationLoader.Parse(f.Text().Replace(f.Destination, f.Source.ToUpperInvariant() + suffix)));
    }
    [WindowsFact]
    public void T08_T11_GlobalSourcesAndDuplicateMonitors()
    {
        using Fixture f = new();
        Directory.CreateDirectory(f.Destination);
        string extra = $"\n[[folders]]\nname='second'\npath='{f.Destination}'\n[[folders.rules]]\nname='r'\npatterns=['*']\ndestination='{Path.Combine(f.Root, "third")}'";
        Assert.Throws<ConfigException>(() => ConfigurationLoader.Parse(f.Text() + extra));
        Assert.Throws<ConfigException>(() => ConfigurationLoader.Parse(f.Text() + extra.Replace(f.Destination, f.Source)));
    }
    [WindowsFact]
    public void T10_RecursiveDestinationExcludedAndOverlapRejected()
    {
        using Fixture f = new();
        string output = Path.Combine(f.Source, "sorted");
        Directory.CreateDirectory(output);
        File.WriteAllText(Path.Combine(output, "a.txt"), "x");
        f.File();
        Configuration config = ConfigurationLoader.Parse(f.Text(recursive: true).Replace(f.Destination, output));
        Assert.Single(Paths.Snapshot(config, config.Folders[0]));
        Assert.False(Paths.Eligible(config, config.Folders[0], Path.Combine(output, "a.txt")));
        string extra = $"\n[[folders]]\nname='second'\npath='{output}'\n[[folders.rules]]\nname='r'\npatterns=['*']\ndestination='{f.Destination}'";
        Assert.Throws<ConfigException>(() => ConfigurationLoader.Parse(f.Text(recursive: true) + extra));
    }
    [WindowsFact]
    public void T01_TomlInlineTablesDottedKeysAndEscapes()
    {
        using Fixture f = new();
        string text = $$"""
            version = 1
            general.dry_run = true
            [[folders]]
            name = "quoted\tname"
            path = '{{f.Source}}'
            duplicates = {name_collision='skip', identical_content='skip'}
            [[folders.rules]]
            name = 'r'
            destination = '{{f.Destination}}'
            patterns = [
                "*.txt", # multiline array
            ]
            """;
        Configuration config = ConfigurationLoader.Parse(text);
        Assert.True(config.General.DryRun);
        Assert.Equal("quoted\tname", config.Folders[0].Name);
        Assert.Equal(NameCollision.Skip, config.Folders[0].Duplicates.NameCollision);
    }
    [WindowsFact]
    public void T09_T11_JunctionRejected()
    {
        using Fixture f = new();
        string junction = Path.Combine(f.Root, "alias");
        System.Diagnostics.ProcessStartInfo start = new("cmd.exe") { CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add("/c"); start.ArgumentList.Add("mklink"); start.ArgumentList.Add("/J"); start.ArgumentList.Add(junction); start.ArgumentList.Add(f.Source);
        using System.Diagnostics.Process process = System.Diagnostics.Process.Start(start)!;
        process.WaitForExit();
        Assert.Equal(0, process.ExitCode);
        try { Assert.Throws<IOException>(() => ConfigurationLoader.Parse(f.Text().Replace(f.Destination, junction))); }
        finally { Directory.Delete(junction); }
    }
}
