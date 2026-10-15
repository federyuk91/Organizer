using FileOrganizer.Core;
using FileOrganizer.Windows;
using Xunit;

namespace FileOrganizer.Tests;

public sealed class DirectoryScopeTests
{
    private static string Put(string directory, string name = "file.txt")
    { Directory.CreateDirectory(directory); string path = Path.Combine(directory, name); File.WriteAllText(path, "data"); return path; }
    private static string Text(Fixture f, string selections = "", bool recursive = false) => f.Text(recursive: recursive)
        .Replace("[folders.duplicates]", selections + "\n[folders.duplicates]") +
        $"\n[[folders.rules]]\nname='nested'\npatterns=['*.txt']\ndestination='{f.Destination}\\AI'\n";

    [WindowsFact]
    public async Task NonRecursiveSourceAndEachExplicitDestinationProtectNestedProjects()
    {
        using Fixture f = new();
        string source = Put(f.Source, "top.txt"); string unmanaged = Put(Path.Combine(f.Source, "spesasmart"));
        string image = Put(f.Destination, "old.txt"); string ai = Put(Path.Combine(f.Destination, "AI"), "ai.txt");
        string project = Put(Path.Combine(f.Destination, "AI", "GameBoy", "animations", "south"));
        Configuration config = ConfigurationLoader.Parse(Text(f)); ManagedDirectories scope = new(config);
        Assert.Equal(new[] { ai, image, source }.Order(), scope.Snapshot(config.Folders[0]).Order());
        Assert.Null(scope.Owner(unmanaged)); Assert.Null(scope.Owner(project));
        await Assert.ThrowsAsync<IOException>(() => f.Move(config, project));
        Assert.True(File.Exists(project));
    }
    [WindowsFact]
    public void FullRecursiveSelectionIncludesFutureChildrenAndCanExcludeSpecificBranches()
    {
        using Fixture f = new();
        string included = Put(Path.Combine(f.Source, "project", "deep"));
        string excludedRoot = Path.Combine(f.Destination, "AI", "GameBoy"); string excluded = Put(Path.Combine(excludedRoot, "deep"));
        Configuration config = ConfigurationLoader.Parse(Text(f, $"excluded_paths=['{excludedRoot}']", true));
        ManagedDirectories scope = new(config);
        Assert.Same(config.Folders[0], scope.Owner(included)); Assert.Null(scope.Owner(excluded));
        Assert.Contains(included, scope.Snapshot(config.Folders[0])); Assert.DoesNotContain(excluded, scope.Snapshot(config.Folders[0]));
        Assert.False(scope.Folders[0].Selection.Complete(f.Destination));
    }
    [WindowsFact]
    public void ExplicitDirectBranchRevealsNoImplicitChildSelectionsAndIndividualPathsCanBeEnabled()
    {
        using Fixture f = new(); string ai = Path.Combine(f.Destination, "AI");
        string aiFile = Put(ai); string direct = Put(f.Destination, "direct.txt");
        string child = Path.Combine(ai, "chosen"); string chosen = Put(child); string deeper = Put(Path.Combine(child, "excluded"));
        Configuration config = ConfigurationLoader.Parse(Text(f, $"direct_paths=['{f.Destination}','{child}']"));
        ManagedDirectories scope = new(config);
        Assert.Equal(DirectoryMode.Off, scope.Folders[0].Selection.Mode(ai));
        Assert.Equal(new[] { direct, chosen }.Order(), scope.Snapshot(config.Folders[0]).Order());
        Assert.Null(scope.Owner(aiFile)); Assert.Null(scope.Owner(deeper));
    }
    [WindowsTheory]
    [InlineData("direct_paths=['x']\nrecursive_paths=['x']")]
    [InlineData("excluded_paths=['x']\nrecursive_paths=['x/child']")]
    [InlineData("direct_paths=['../escape']")]
    [InlineData("recursive_paths='wrong'")]
    public void InvalidPathSelectionsFailClosed(string selections)
    { using Fixture f = new(); Assert.Throws<ConfigException>(() => ConfigurationLoader.Parse(Text(f, selections))); }

    [WindowsFact]
    public void DocumentPreservesCommentsAndSelectionsAcrossOtherEditsAndReordering()
    {
        using Fixture f = new(); string child = Path.Combine(f.Destination, "50% bonus");
        ConfigurationDocument d = new(Text(f, $"# selected branch\ndirect_paths=['{child}']"));
        d.Folders[0].Fields["name"].Text = "renamed"; d.Folders[0].Rules.Move(1, 0);
        string rendered = d.Render(); Assert.Contains("# selected branch", rendered);
        PathOverride selection = Assert.Single(ConfigurationLoader.Parse(rendered).Folders[0].PathOverrides);
        Assert.Equal(child, selection.Path); Assert.Equal(DirectoryMode.Direct, selection.Mode);
        d.Folders[0].Fields["direct_paths"].Text = ""; d.Folders[0].Fields["excluded_paths"].Text = child;
        Assert.Equal(DirectoryMode.Off, Assert.Single(d.Validate().Folders[0].PathOverrides).Mode);
    }
    [WindowsFact]
    public void PathSelectionsCannotAcquireAnotherMonitorsSourceDestinationOrSelection()
    {
        using Fixture f = new(); string second = Path.Combine(f.Source, "second"); Directory.CreateDirectory(second);
        string selected = Path.Combine(second, "selected"); string output = Path.Combine(f.Root, "other-output");
        string secondMonitor = $"\n[[folders]]\nname='other'\npath='{second}'\ndirect_paths=['{selected}']\n[[folders.rules]]\nname='other'\npatterns=['*.txt']\ndestination='{output}'\n";
        Assert.Throws<ConfigException>(() => ConfigurationLoader.Parse(Text(f, $"direct_paths=['{selected}']") + secondMonitor));
        Assert.Throws<ConfigException>(() => ConfigurationLoader.Parse(Text(f, $"recursive_paths=['{f.Source}']") + secondMonitor));
        string destinationMonitor = secondMonitor.Replace(output, Path.Combine(f.Source, "other-owned"));
        Assert.Throws<ConfigException>(() => ConfigurationLoader.Parse(Text(f, "direct_paths=['other-owned']") + destinationMonitor));
    }
    [WindowsFact]
    public async Task WatcherAndReloadHonorSelectedDirectBranchesAndDisabledSubtrees()
    {
        using Fixture f = new(); string chosen = Path.Combine(f.Source, "chosen"); Directory.CreateDirectory(chosen);
        string project = Path.Combine(f.Destination, "AI", "project"); Directory.CreateDirectory(project);
        string text = Text(f, $"direct_paths=['{chosen}']").Replace("[general]", "[general]\nscan_on_startup=false");
        File.WriteAllText(f.ConfigPath, text);
        await using WorkerHost host = new(ConfigurationLoader.Parse(text), f.ConfigPath, f.Log());
        using CancellationTokenSource stop = new(); Task run = host.RunAsync(stop.Token);
        try
        {
            Put(chosen, "selected.txt"); string protectedFile = Put(project, "protected.txt");
            await Fixture.Eventually(() => File.Exists(Path.Combine(f.Destination, "selected.txt")));
            Assert.True(File.Exists(protectedFile));
            ConfigurationFile file = new(f.ConfigPath); ConfigurationDocument d = file.Open();
            d.Folders[0].Fields["recursive_paths"].Text = project; file.Save(d.Render());
            await Fixture.Eventually(() => File.Exists(Path.Combine(f.Destination, "protected.txt")));
            Assert.Equal(0, host.Failures);
        }
        finally { stop.Cancel(); await run; }
    }
}
