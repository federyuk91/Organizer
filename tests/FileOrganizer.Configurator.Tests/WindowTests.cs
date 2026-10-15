using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Input;
using System.Windows.Threading;
using FileOrganizer.Configurator;
using FileOrganizer.Core;
using FileOrganizer.Windows;
using Xunit;

[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace FileOrganizer.Configurator.Tests;

public sealed class WindowTests
{
    [Fact]
    public async Task WindowBindingsEditingOrderingAndAllTabsRender() => await OnSta(async () =>
    {
        using TestFixture f = new();
        MainWindow window = new(f.Config, f.History, f.States) { Left = -20000, Top = -20000, ShowInTaskbar = false };
        window.Show();
        Assert.NotNull(window.Icon);
        EditorViewModel vm = (EditorViewModel)window.DataContext;
        try
        {
            await Eventually(() => vm.Document is not null && !vm.Busy);
            Assert.True(vm.CanManageStartup);
            TextBox name = Descendants<TextBox>(window).First(t => BindingOperations.GetBinding(t, TextBox.TextProperty)?.Path.Path == "SelectedFolder.Fields[name].Text");
            name.Text = "Edited through WPF";
            name.GetBindingExpression(TextBox.TextProperty)!.UpdateSource();
            Assert.Equal("Edited through WPF", vm.SelectedFolder!.DisplayName);
            vm.AddRuleCommand.Execute(null);
            vm.SelectedRule!.Fields["name"].Text = "AI";
            vm.SelectedRule.Fields["destination"].Text = "Images\\ai";
            vm.SelectedRule.Fields["extensions"].Text = ".png\n.jpg";
            vm.SelectedRule.Fields["patterns"].Text = "*chatgpt*\n*gemini*";
            vm.RuleUpCommand.Execute(null);
            Configuration result = vm.Document!.Validate();
            Assert.Equal("AI", result.Folders[0].Rules[0].Name);
            Assert.Equal("AI", Matching.First(result.Folders[0], "ChatGPT.png")!.Name);
            Assert.Null(Matching.First(result.Folders[0], "Gemini.exe"));
            TabControl tabs = Descendants<TabControl>(window).Single();
            Assert.Equal(5, tabs.Items.Count);
            for (int index = 0; index < tabs.Items.Count; index++)
            {
                tabs.SelectedIndex = index;
                await Eventually(() => !vm.Busy);
                await window.Dispatcher.InvokeAsync(window.UpdateLayout, DispatcherPriority.ApplicationIdle);
                AssertNoBindingErrors(window);
                string output = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../artifacts/gui-preview"));
                Directory.CreateDirectory(output);
                FrameworkElement content = (FrameworkElement)window.Content;
                RenderTargetBitmap bitmap = new((int)content.ActualWidth, (int)content.ActualHeight, 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(content);
                PngBitmapEncoder encoder = new(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using FileStream image = File.Create(Path.Combine(output, "tab-" + index + ".png")); encoder.Save(image);
            }
        }
        finally
        {
            vm.SaveCommand.Execute(null); await Eventually(() => !vm.Busy);
            Assert.False(vm.IsDirty); window.Close();
        }
    });

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public async Task SavingFromEveryTabPreservesSelectionsAndEnablesEditing(int tabIndex) => await OnSta(async () =>
    {
        using TestFixture f = new();
        MainWindow window = new(f.Config, f.History, f.States) { Left = -20000, Top = -20000, ShowInTaskbar = false };
        window.Show(); EditorViewModel vm = (EditorViewModel)window.DataContext;
        try
        {
            await Eventually(() => vm.Document is not null && !vm.Busy);
            string secondSource = Path.Combine(f.Root, "second"); Directory.CreateDirectory(secondSource);
            vm.AddFolderCommand.Execute(null);
            vm.SelectedFolder!.Fields["name"].Text = "Second folder";
            vm.SelectedFolder.Fields["path"].Text = secondSource;
            vm.SelectedRule!.Fields["patterns"].Text = "*.txt";
            vm.SelectedRule.Fields["destination"].Text = "Text";
            vm.AddRuleCommand.Execute(null);
            vm.SelectedRule!.Fields["name"].Text = "Second rule";
            vm.SelectedRule.Fields["extensions"].Text = ".png";
            vm.SelectedRule.Fields["destination"].Text = "Images";
            TabControl tabs = Descendants<TabControl>(window).Single(); tabs.SelectedIndex = tabIndex;
            await Eventually(() => !vm.Busy);
            vm.SaveCommand.Execute(null); await Eventually(() => !vm.Busy && !vm.IsDirty);
            await window.Dispatcher.InvokeAsync(window.UpdateLayout, DispatcherPriority.ApplicationIdle);
            Assert.Equal("Second folder", vm.SelectedFolder?.DisplayName);
            Assert.Equal("Second rule", vm.SelectedRule?.DisplayName);
            Assert.True(vm.HasFolder); Assert.True(vm.HasRule); Assert.True(vm.CanEditForm);
            Assert.True(tabs.IsEnabled); Assert.True(vm.SaveCommand.CanExecute(null));
            tabs.SelectedIndex = 0;
            await Eventually(() => Descendants<TextBox>(window).Any(t => t.IsEnabled &&
                BindingOperations.GetBinding(t, TextBox.TextProperty)?.Path.Path == "SelectedFolder.Fields[name].Text"));
            vm.SelectedRule!.Fields["name"].Text = "Edited after saving";
            vm.SaveCommand.Execute(null); await Eventually(() => !vm.Busy && !vm.IsDirty);
            Assert.Equal("Edited after saving", vm.SelectedRule?.DisplayName);
            Assert.Equal("Edited after saving", ConfigurationLoader.Load(f.Config).Folders[1].Rules[1].Name);
            AssertNoBindingErrors(window);
        }
        finally { await Eventually(() => !vm.Busy); window.Close(); }
    });

    [Fact]
    public async Task RulePresetsPopulateNewRulesOnlyAndPersistEngineAcceptedExtensions() => await OnSta(async () =>
    {
        using TestFixture f = new(); using EditorViewModel vm = new(new Window(), new FakeStore(), f.History, f.States);
        await vm.InitializeAsync(f.Config); Assert.Equal("New rules", vm.SelectedRulePreset.Name);
        vm.AddRuleCommand.Execute(null); ConfigurationSection empty = vm.SelectedRule!;
        Assert.Equal("New rule", empty.Fields["name"].Text);
        foreach (string field in new[] { "destination", "extensions", "patterns", "exclude_patterns" }) Assert.Empty(empty.Fields[field].Text);
        vm.SelectedFolder!.Rules.Remove(empty);
        ConfigurationSection original = vm.SelectedFolder.Rules[0]; string originalText = original.Fields["patterns"].Text;
        foreach (RulePreset preset in EditorViewModel.RulePresets.Skip(1))
        {
            int count = vm.SelectedFolder.Rules.Count; vm.SelectedRulePreset = preset;
            Assert.Equal(count, vm.SelectedFolder.Rules.Count);
            vm.AddRuleCommand.Execute(null); ConfigurationSection added = vm.SelectedRule!;
            Assert.Equal(preset.Name, added.DisplayName); Assert.Empty(added.Fields["destination"].Text);
            Assert.Empty(added.Fields["patterns"].Text); Assert.Empty(added.Fields["exclude_patterns"].Text);
            Assert.True(added.Fields["enabled"].Flag); Assert.False(added.Fields["case_sensitive"].Flag);
            HashSet<string> all = new(StringComparer.OrdinalIgnoreCase);
            foreach (string extension in preset.Extensions) Assert.True(all.Add(extension), "Duplicate extension: " + extension);
            added.Fields["destination"].Text = "sorted/" + preset.Name;
            Configuration config = vm.Document!.Validate(); Rule rule = config.Folders[0].Rules[^1];
            Assert.Equal(preset.Extensions.ToArray(), rule.Extensions.ToArray());
            foreach (string extension in preset.Extensions) Assert.Same(rule, Matching.First(config.Folders[0] with { Rules = [rule] }, "EXAMPLE" + extension.ToUpperInvariant()));
            Assert.Equal(originalText, original.Fields["patterns"].Text);
        }
        Assert.Equal(f.Valid, File.ReadAllText(f.Config));
        vm.SaveCommand.Execute(null); await Eventually(() => !vm.Busy); Assert.False(vm.IsDirty);
        Configuration saved = ConfigurationLoader.Load(f.Config); Assert.Equal(15, saved.Folders[0].Rules.Length);
        Assert.Equal("text", saved.Folders[0].Rules[0].Name);
    });

    [Fact]
    public async Task SpecializedPresetsFollowCurrentRulePriority() => await OnSta(async () =>
    {
        using TestFixture f = new(); using EditorViewModel vm = new(new Window(), new FakeStore(), f.History, f.States);
        await vm.InitializeAsync(f.Config);
        foreach ((string generic, string specialized, string filename) in new[]
        {
            ("Documents", "E-books", "book.epub"),
            ("Images", "Graphic Projects", "drawing.psd")
        })
        {
            foreach (string name in new[] { generic, specialized })
            {
                vm.SelectedRulePreset = EditorViewModel.RulePresets.Single(p => p.Name == name);
                vm.AddRuleCommand.Execute(null);
                vm.SelectedRule!.Fields["destination"].Text = "sorted/" + name;
            }
            Assert.Equal(generic, Matching.First(vm.Document!.Validate().Folders[0], filename)!.Name);
            vm.RuleUpCommand.Execute(null);
            Assert.Equal(specialized, Matching.First(vm.Document.Validate().Folders[0], filename)!.Name);
        }
        vm.SaveCommand.Execute(null); await Eventually(() => !vm.Busy);
        Configuration saved = ConfigurationLoader.Load(f.Config);
        Assert.Equal("E-books", Matching.First(saved.Folders[0], "book.epub")!.Name);
        Assert.Equal("Graphic Projects", Matching.First(saved.Folders[0], "drawing.psd")!.Name);
    });

    [Fact]
    public async Task RulePresetDropdownAndAddButtonWorkThroughWpfBindings() => await OnSta(async () =>
    {
        using TestFixture f = new(); MainWindow window = new(f.Config, f.History, f.States) { Left = -20000, Top = -20000, ShowInTaskbar = false }; window.Show();
        EditorViewModel vm = (EditorViewModel)window.DataContext;
        try
        {
            await Eventually(() => vm.Document is not null && !vm.Busy);
            ComboBox picker = Descendants<ComboBox>(window).Single(c => BindingOperations.GetBinding(c, ComboBox.SelectedItemProperty)?.Path.Path == "SelectedRulePreset");
            Assert.Equal("New rules", ((RulePreset)picker.SelectedItem).Name); Assert.Equal(15, picker.Items.Count);
            picker.SelectedIndex = 1; Assert.Equal("Images", vm.SelectedRulePreset.Name); Assert.Single(vm.SelectedFolder!.Rules);
            Button add = Descendants<Button>(window).Single(b => ReferenceEquals(b.Command, vm.AddRuleCommand));
            await Eventually(() => add.IsEnabled);
            System.Windows.Automation.Peers.ButtonAutomationPeer peer = new(add);
            ((System.Windows.Automation.Provider.IInvokeProvider)peer.GetPattern(System.Windows.Automation.Peers.PatternInterface.Invoke)).Invoke();
            await Eventually(() => vm.SelectedFolder.Rules.Count == 2);
            Assert.Equal("Images", vm.SelectedRule!.DisplayName); Assert.Contains(".png", vm.SelectedRule.Fields["extensions"].Text);
            vm.SelectedRule.Fields["destination"].Text = "Images";
        }
        finally { vm.SaveCommand.Execute(null); await Eventually(() => !vm.Busy); window.Close(); }
    });

    [Fact]
    public async Task GuiSaveProducesAnEngineAcceptedFileAndDetectsExternalEdits() => await OnSta(async () =>
    {
        using TestFixture f = new();
        Window owner = new(); using EditorViewModel vm = new(owner, new FakeStore(), f.History, f.States);
        await vm.InitializeAsync(f.Config);
        vm.Document!.General.Fields["dry_run"].Flag = true;
        vm.SaveCommand.Execute(null);
        await Eventually(() => !vm.Busy);
        Assert.False(vm.IsDirty); Assert.True(ConfigurationLoader.Load(f.Config).General.DryRun);
        vm.Document.General.Fields["log_level"].Text = "debug";
        File.AppendAllText(f.Config, "\n# external change\n");
        await Eventually(() => vm.ExternalChanged);
        vm.SaveCommand.Execute(null); await Eventually(() => !vm.Busy);
        Assert.Contains("Save failed", vm.Status); Assert.True(vm.IsDirty);
        Assert.Contains("# external change", File.ReadAllText(f.Config));
        Assert.Equal(LogLevel.Warning, ConfigurationLoader.Load(f.Config).General.LogLevel);
    });

    [Fact]
    public async Task AutorunUsesEngineAndReportsFailuresAndActualState() => await OnSta(async () =>
    {
        using TestFixture f = new(); FakeStore store = new();
        using EditorViewModel vm = new(new Window(), store, f.History, f.States);
        await vm.InitializeAsync(f.Config);
        vm.EnginePath = Path.Combine(f.Root, "FileOrganizer.exe"); File.WriteAllText(vm.EnginePath, "test engine");
        await vm.ChangeStartupAsync(true);
        Assert.True(vm.StartWithWindows);
        Assert.Contains("FileOrganizer.exe\" run --background --config", store.Value);
        Assert.DoesNotContain("Configurator", store.Value);
        store.FailWrite = true;
        await vm.ChangeStartupAsync(false);
        Assert.True(vm.StartWithWindows); Assert.Contains("failed", vm.Status);
        store.FailWrite = false; await vm.ChangeStartupAsync(false);
        Assert.False(vm.StartWithWindows); Assert.Null(store.Value);
        store.FailRead = true; vm.RefreshStartup(); Assert.Null(vm.StartWithWindows);
        Assert.Contains("denied", vm.StartupMessage);
    });

    [Fact]
    public async Task RawTomlRecoveryAndUnknownValuesRemainAvailable() => await OnSta(async () =>
    {
        using TestFixture f = new();
        File.WriteAllText(f.Config, "version=[broken");
        using EditorViewModel vm = new(new Window(), new FakeStore(), f.History, f.States);
        await vm.InitializeAsync(f.Config); Assert.Null(vm.Document); Assert.Contains("broken", vm.RawText);
        vm.RawText = f.Valid + "\n[future]\nunknown=3.14\n";
        vm.ApplyRawCommand.Execute(null);
        Assert.Contains("unknown=3.14", vm.Document!.Render());
        vm.SaveCommand.Execute(null); await Eventually(() => !vm.Busy);
        Assert.Contains("Save failed", vm.Status); Assert.Equal("version=[broken", File.ReadAllText(f.Config));
        vm.RawText = f.Valid; vm.SaveCommand.Execute(null); await Eventually(() => !vm.Busy);
        Assert.False(vm.IsDirty); Assert.NotNull(ConfigurationLoader.Load(f.Config));
    });

    [Fact]
    public async Task RecursionCheckboxDiskAndExternalChangesRemainExplicitlySynchronized() => await OnSta(async () =>
    {
        using TestFixture f = new();
        ConfigurationFile initial = new(f.Config); ConfigurationDocument initialDocument = initial.Open();
        initialDocument.Folders[0].Fields["recursive"].Flag = true; initial.Save(initialDocument.Render());
        MainWindow window = new(f.Config, f.History, f.States) { Left = -20000, Top = -20000, ShowInTaskbar = false };
        window.Show(); EditorViewModel vm = (EditorViewModel)window.DataContext;
        try
        {
            await Eventually(() => vm.Document is not null && !vm.Busy);
            CheckBox recursion = Descendants<CheckBox>(window).Single(c => BindingOperations.GetBinding(c, CheckBox.IsCheckedProperty)?.Path.Path == "SelectedFolder.Fields[recursive].Flag");
            Assert.True(recursion.IsChecked); Assert.Contains("Include subfolders=True", vm.SavedSummary);
            recursion.IsChecked = false; recursion.GetBindingExpression(CheckBox.IsCheckedProperty)!.UpdateSource();
            Assert.True(ConfigurationLoader.Load(f.Config).Folders[0].Recursive);
            Assert.Contains("UNSAVED DRAFT", vm.SyncMessage); Assert.Contains("Include subfolders=True", vm.SavedSummary);
            vm.SaveCommand.Execute(null); await Eventually(() => !vm.Busy);
            Assert.False(ConfigurationLoader.Load(f.Config).Folders[0].Recursive); Assert.Contains("Include subfolders=False", vm.SavedSummary);
            ConfigurationFile external = new(f.Config); ConfigurationDocument changed = external.Open();
            changed.Folders[0].Fields["recursive"].Flag = true; external.Save(changed.Render());
            await Eventually(() => vm.Document!.Folders[0].Fields["recursive"].Flag && !vm.Busy);
            Assert.True(recursion.IsChecked); Assert.False(vm.IsDirty); Assert.False(vm.ExternalChanged);
            vm.Document!.General.Fields["log_level"].Text = "debug";
            ConfigurationFile other = new(f.Config); ConfigurationDocument otherDocument = other.Open();
            otherDocument.Folders[0].Fields["recursive"].Flag = false; other.Save(otherDocument.Render());
            await Eventually(() => vm.ExternalChanged && vm.SavedSummary.Contains("Include subfolders=False"));
            Assert.True(vm.Document.Folders[0].Fields["recursive"].Flag); Assert.False(vm.CanEditForm);
            Assert.Contains("CONFLICT", vm.SyncMessage); Assert.Equal("debug", vm.Document.General.Fields["log_level"].Text);
        }
        finally { await vm.InitializeAsync(f.Config); window.Close(); }
    });

    [Fact]
    public async Task EngineUsingAnotherFileOrRejectedSnapshotCannotAppearApplied() => await OnSta(async () =>
    {
        using TestFixture f = new(); using EditorViewModel vm = new(new Window(), new FakeStore(), f.History, f.States);
        await vm.InitializeAsync(f.Config);
        string other = Path.Combine(f.Root, "other.toml");
        ConfigurationDocument d = new(f.Valid); d.Folders[0].Fields["recursive"].Flag = true;
        File.WriteAllText(other, d.Render());
        EngineSnapshot snapshot = f.States.Create(other, d.Render(), d.Validate()); f.States.Write(snapshot);
        await Eventually(() => vm.RuntimeMessage.Contains("ANOTHER TOML"));
        Assert.Contains("Include subfolders=False", vm.SavedSummary); Assert.Contains("Include subfolders=True", vm.RuntimeMessage);
        vm.OpenEngineConfigCommand.Execute(null); await Eventually(() => !vm.Busy && vm.ConfigPath == other);
        Assert.True(vm.Document!.Folders[0].Fields["recursive"].Flag); Assert.Contains("matches this file", vm.RuntimeMessage);
        ConfigurationFile changed = new(other); ConfigurationDocument next = changed.Open(); next.Folders[0].Fields["recursive"].Flag = false; changed.Save(next.Render());
        f.States.Write(snapshot with { ReloadError = "injected rejection" });
        await Eventually(() => vm.RuntimeMessage.Contains("NOT applied") && vm.RuntimeMessage.Contains("injected rejection"));
        Assert.Contains("Include subfolders=True", vm.RuntimeMessage);
    });

    [Fact]
    public async Task HistoryTabLoadsPersistedRecordsForOpenConfiguration() => await OnSta(async () =>
    {
        using TestFixture f = new(); Configuration config = ConfigurationLoader.Load(f.Config);
        string source = Path.Combine(f.Root, "in", "history.txt"); File.WriteAllText(source, "history data");
        await new SafeTransfer((_, _) => { }, f.History, f.Config).ExecuteAsync(config, config.Folders[0], config.Folders[0].Rules[0], source);
        using EditorViewModel vm = new(new Window(), new FakeStore(), f.History, f.States);
        await vm.InitializeAsync(f.Config); await vm.RefreshHistoryAsync();
        MoveRecord entry = Assert.Single(vm.HistoryEntries); Assert.Equal(source, entry.Source); Assert.Equal("Committed", entry.State);
        vm.SelectHistory([entry]); Assert.True(vm.UndoHistoryCommand.CanExecute(null));
        string other = Path.Combine(f.Root, "renamed.toml"); File.WriteAllText(other, f.Valid);
        await vm.InitializeAsync(other); await vm.RefreshHistoryAsync(); Assert.Empty(vm.HistoryEntries);
        vm.ShowAllHistory = true; await Eventually(() => !vm.Busy); Assert.Single(vm.HistoryEntries);
    });

    [Fact]
    public async Task PathTreeCyclesBothDirectionsLazilyAndPersistsSelections() => await OnSta(async () =>
    {
        using TestFixture f = new(); string source = Path.Combine(f.Root, "in");
        string child = Path.Combine(source, "project"); string deep = Path.Combine(child, "animations", "south"); Directory.CreateDirectory(deep);
        using EditorViewModel vm = new(new Window(), new FakeStore(), f.History, f.States);
        await vm.InitializeAsync(f.Config); await vm.LoadPathsAsync();
        PathTreeNode root = vm.PathRoots.First(p => p.Path == source);
        Assert.Equal("■", root.Symbol); await root.LoadChildrenAsync();
        PathTreeNode project = root.Children.Single(p => p.Path == child); Assert.Equal("□", project.Symbol); Assert.Empty(project.Children);
        await project.LoadChildrenAsync(); Assert.Empty(project.Children); // No enumeration of inactive branches.
        await vm.CyclePathAsync(project, false); Assert.Equal("■", project.Symbol);
        PathTreeNode animations = Assert.Single(project.Children); Assert.Equal("□", animations.Symbol); Assert.Empty(animations.Children);
        await vm.CyclePathAsync(project, false); Assert.Equal("✓", project.Symbol); Assert.Equal("✓", animations.Symbol);
        await vm.CyclePathAsync(project, false); Assert.Equal("□", project.Symbol); Assert.Empty(project.Children);
        await vm.CyclePathAsync(project, true); Assert.Equal("✓", project.Symbol);
        await vm.CyclePathAsync(project, true); Assert.Equal("■", project.Symbol); Assert.Equal("□", Assert.Single(project.Children).Symbol);
        await vm.CyclePathAsync(project, true); Assert.Equal("□", project.Symbol); Assert.Empty(project.Children);
        await vm.CyclePathAsync(project, false); await vm.CyclePathAsync(project.Children.Single(), false);
        Assert.True(vm.IsDirty); Assert.Empty(ConfigurationLoader.Load(f.Config).Folders[0].PathOverrides);
        vm.SaveCommand.Execute(null); await Eventually(() => !vm.Busy);
        Assert.False(vm.IsDirty); Assert.Equal(2, ConfigurationLoader.Load(f.Config).Folders[0].PathOverrides.Length);
        await vm.LoadPathsAsync(); root = vm.PathRoots.First(p => p.Path == source); await root.LoadChildrenAsync();
        project = root.Children.Single(p => p.Path == child); Assert.Equal("■", project.Symbol); await project.LoadChildrenAsync(); Assert.Equal("■", project.Children.Single().Symbol);
        await vm.CyclePathAsync(root, false); Assert.Equal("✓", root.Symbol);
        Assert.Single(vm.Document!.Validate().Folders[0].PathOverrides); // Whole subtree selection replaces exceptions.
        await root.LoadChildrenAsync(); project = root.Children.Single(p => p.Path == child);
        await vm.CyclePathAsync(project, false); Assert.Equal("■", root.Symbol); Assert.Equal("□", project.Symbol);
        await vm.CyclePathAsync(root, false); Assert.Equal("✓", root.Symbol); Assert.Equal("✓", project.Symbol);
        vm.Document!.Folders[0].Fields["enabled"].Flag = false; await vm.LoadPathsAsync();
        root = vm.PathRoots[0]; Assert.Equal("□", root.Symbol); Assert.False(root.CanCycle);
        await vm.CyclePathAsync(root, false); Assert.False(vm.Document.Folders[0].Fields["enabled"].Flag);
    });

    [Fact]
    public async Task PathTreeSeparatesDraftDiskAndAppliedEngineAndBlocksStaleEditing() => await OnSta(async () =>
    {
        using TestFixture f = new(); Configuration original = ConfigurationLoader.Load(f.Config);
        f.States.Write(f.States.Create(f.Config, f.Valid, original));
        using EditorViewModel vm = new(new Window(), new FakeStore(), f.History, f.States);
        await vm.InitializeAsync(f.Config); vm.Document!.Folders[0].Fields["recursive"].Flag = true;
        await vm.LoadPathsAsync(); Assert.Equal("✓", vm.PathRoots[0].Symbol); Assert.True(vm.CanEditTree);
        vm.PathView = "Saved file"; Assert.False(vm.CanEditTree); await vm.LoadPathsAsync(); Assert.Equal("■", vm.PathRoots[0].Symbol);
        vm.PathView = "Applied engine"; await vm.LoadPathsAsync(); Assert.Equal("■", vm.PathRoots[0].Symbol); Assert.False(vm.CanEditTree);
        await vm.CyclePathAsync(vm.PathRoots[0], false); Assert.Equal("■", vm.PathRoots[0].Symbol);
        vm.PathView = "Draft"; await vm.LoadPathsAsync();
        File.AppendAllText(f.Config, "\n# external edit\n"); await Eventually(() => vm.ExternalChanged); Assert.False(vm.CanEditTree);
        f.States.Clear(f.States.Read()!);
    });

    [Fact]
    public async Task EmptyDirectoriesUseOnlyOffAndCompleteWithEitherMouseCycle() => await OnSta(async () =>
    {
        using TestFixture f = new(); string leafPath = Path.Combine(f.Root, "in", "empty"); Directory.CreateDirectory(leafPath);
        using EditorViewModel vm = new(new Window(), new FakeStore(), f.History, f.States);
        await vm.InitializeAsync(f.Config); await vm.LoadPathsAsync();
        PathTreeNode leaf = vm.PathRoots[0].Children.Single(p => p.Path == leafPath);
        Assert.Equal("□", leaf.Symbol); Assert.Empty(leaf.Children);
        foreach (bool reverse in new[] { false, false, true, true })
        {
            string expected = leaf.IsOff ? "✓" : "□";
            await vm.CyclePathAsync(leaf, reverse); Assert.Equal(expected, leaf.Symbol); Assert.Empty(leaf.Children);
        }
        Directory.CreateDirectory(Path.Combine(leafPath, "new-child"));
        await vm.CyclePathAsync(leaf, false); Assert.Equal("■", leaf.Symbol); Assert.Equal("□", Assert.Single(leaf.Children).Symbol);
    });

    [Fact]
    public async Task ConfiguredPathsFormOneExpandedChainWithDirectParentsAndExcludedChildren() => await OnSta(async () =>
    {
        using TestFixture f = new(); string source = Path.Combine(f.Root, "in");
        string images = Path.Combine(source, "Images"), ai = Path.Combine(images, "AI"), documents = Path.Combine(source, "Documents"), pdf = Path.Combine(documents, "PDF");
        Directory.CreateDirectory(ai); Directory.CreateDirectory(pdf); Directory.CreateDirectory(Path.Combine(documents, "private"));
        ConfigurationDocument d = new(f.Valid); d.Folders[0].Rules[0].Fields["destination"].Text = images;
        ConfigurationSection a = d.AddRule(d.Folders[0]); a.Fields["name"].Text = "AI"; a.Fields["destination"].Text = ai; a.Fields["patterns"].Text = "*ai*.txt";
        ConfigurationSection p = d.AddRule(d.Folders[0]); p.Fields["name"].Text = "PDF"; p.Fields["destination"].Text = pdf; p.Fields["extensions"].Text = ".pdf";
        File.WriteAllText(f.Config, d.Render());
        using EditorViewModel vm = new(new Window(), new FakeStore(), f.History, f.States);
        await vm.InitializeAsync(f.Config); await vm.LoadPathsAsync();
        PathTreeNode root = Assert.Single(vm.PathRoots); Assert.Equal(source, root.Path); Assert.True(root.Expanded);
        PathTreeNode imageNode = root.Children.Single(n => n.Path == images); Assert.True(imageNode.Expanded);
        Assert.Equal(ai, Assert.Single(imageNode.Children).Path);
        PathTreeNode documentNode = root.Children.Single(n => n.Path == documents); Assert.Equal("■", documentNode.Symbol); Assert.Contains("direct files", documentNode.Description);
        Assert.Equal(pdf, documentNode.Children.Single(n => n.Path == pdf).Path);
        PathTreeNode excludedChild = documentNode.Children.Single(n => n.Path == Path.Combine(documents, "private"));
        Assert.Equal("□", excludedChild.Symbol); Assert.Empty(excludedChild.Children);
        List<PathTreeNode> visible = WalkPaths(vm.PathRoots).ToList();
        Assert.Equal(visible.Count, visible.Select(n => n.Path).Distinct(Paths.Comparer).Count());
        Assert.Equal("✓", imageNode.Children.Single().Symbol); // Existing direct destination without subfolders is complete.
        Assert.Equal("✓", imageNode.Symbol);
        await vm.CyclePathAsync(imageNode, false); Assert.Equal("□", imageNode.Symbol); Assert.Empty(imageNode.Children);
        await vm.CyclePathAsync(imageNode, true); Assert.Equal("✓", imageNode.Symbol);
    });

    [Fact]
    public async Task DirectParentShowsCheckWhenEveryCurrentChildIsCompleteAndSquareOtherwise() => await OnSta(async () =>
    {
        using TestFixture f = new(); string documents = Path.Combine(f.Root, "in", "Documents");
        string editables = Path.Combine(documents, "Editables"), pdf = Path.Combine(documents, "PDF");
        Directory.CreateDirectory(editables); Directory.CreateDirectory(pdf);
        ConfigurationDocument d = new(f.Valid); d.Folders[0].Rules[0].Fields["destination"].Text = editables;
        d.Folders[0].Fields["recursive_paths"].Text = editables;
        ConfigurationSection rule = d.AddRule(d.Folders[0]); rule.Fields["name"].Text = "PDF";
        rule.Fields["destination"].Text = pdf; rule.Fields["extensions"].Text = ".pdf";
        File.WriteAllText(f.Config, d.Render());
        using EditorViewModel vm = new(new Window(), new FakeStore(), f.History, f.States);
        await vm.InitializeAsync(f.Config); await vm.LoadPathsAsync();
        PathTreeNode parent = WalkPaths(vm.PathRoots).Single(n => n.Path == documents);
        Assert.Equal(DirectoryMode.Direct, parent.Layout.Mode(documents)); Assert.Equal("✓", parent.Symbol);
        Assert.All(parent.Children, c => Assert.Equal("✓", c.Symbol));
        PathTreeNode pdfNode = parent.Children.Single(n => n.Path == pdf);
        await vm.CyclePathAsync(pdfNode, false); Assert.Equal("□", pdfNode.Symbol); Assert.Equal("■", parent.Symbol);
        await vm.CyclePathAsync(pdfNode, true); Assert.Equal("✓", parent.Symbol);
        string project = Path.Combine(documents, "Unselected", "deep"); Directory.CreateDirectory(project);
        await vm.LoadPathsAsync(); parent = WalkPaths(vm.PathRoots).Single(n => n.Path == documents);
        Assert.Equal("■", parent.Symbol);
        PathTreeNode excluded = parent.Children.Single(n => n.Path == Path.GetDirectoryName(project));
        Assert.Equal("□", excluded.Symbol); Assert.Empty(excluded.Children);
        Configuration saved = vm.Document!.Validate(); Assert.Equal(DirectoryMode.Off, new DirectoryScope(saved.Folders[0]).Mode(project));
    });

    [Fact]
    public async Task CommonRootsAcrossMonitorsAppearOnceAndRetainTheCorrectOwner() => await OnSta(async () =>
    {
        using TestFixture f = new(); string parent = Path.Combine(f.Root, "in"), nested = Path.Combine(parent, "nested"); Directory.CreateDirectory(nested);
        string text = f.Valid + $"\n[[folders]]\nname='nested monitor'\npath='{nested}'\n[[folders.rules]]\nname='other'\npatterns=['*.bin']\ndestination='{f.Root}\\out'\n";
        File.WriteAllText(f.Config, text);
        using EditorViewModel vm = new(new Window(), new FakeStore(), f.History, f.States);
        await vm.InitializeAsync(f.Config); await vm.LoadPathsAsync();
        Assert.Equal(2, vm.PathRoots.Count); // Parent source and the unrelated external destination.
        PathTreeNode nestedNode = WalkPaths(vm.PathRoots).Single(n => Paths.Comparer.Equals(n.Path, nested)); Assert.Equal(1, nestedNode.FolderIndex);
        await vm.CyclePathAsync(nestedNode, false);
        Assert.Empty(vm.Document!.Validate().Folders[0].PathOverrides); Assert.Single(vm.Document.Validate().Folders[1].PathOverrides);
        ConfigurationDocument changed = new(vm.Document.Render()); string excluded = Path.Combine(f.Root, "excluded-external");
        changed.Folders[1].Rules[0].Fields["enabled"].Flag = false; changed.Folders[1].Rules[0].Fields["destination"].Text = excluded;
        changed.Folders[1].Fields["excluded_paths"].Text = excluded;
        PathTreeLayout layout = new(changed.Validate()); Assert.Equal(1, layout.Owner(excluded)); Assert.Equal(DirectoryMode.Off, layout.Mode(excluded));
    });

    private static IEnumerable<PathTreeNode> WalkPaths(IEnumerable<PathTreeNode> roots)
    {
        foreach (PathTreeNode node in roots.Where(n => !n.Placeholder))
        { yield return node; foreach (PathTreeNode child in WalkPaths(node.Children)) yield return child; }
    }

    [Fact]
    public async Task ScanButtonsRunSavedConfigurationAndCancelWithoutConcurrentLaunches() => await OnSta(async () =>
    {
        using TestFixture f = new(); string engine = Path.Combine(f.Root, "FileOrganizer.exe"); File.WriteAllText(engine, "test engine");
        string? launchedEngine = null, launchedConfig = null; CancellationToken received = default;
        TaskCompletionSource<ScanResult> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using EditorViewModel vm = new(new Window(), new FakeStore(), f.History, f.States, scanRunner: (exe, config, token) =>
        { launchedEngine = exe; launchedConfig = config; received = token; token.Register(() => completion.TrySetResult(new(130, "", ""))); return completion.Task; }, workerRunning: () => false);
        await vm.InitializeAsync(f.Config); vm.EnginePath = engine;
        Assert.True(vm.StartScanCommand.CanExecute(null)); Task run = vm.StartScanAsync(); await Eventually(() => launchedConfig is not null);
        Assert.Equal(engine, launchedEngine); Assert.Equal(f.Config, launchedConfig); Assert.True(vm.Busy);
        Assert.False(vm.StartScanCommand.CanExecute(null)); Assert.True(vm.StopScanCommand.CanExecute(null));
        await vm.StopScanAsync(); await run; Assert.True(received.IsCancellationRequested); Assert.False(vm.Busy);
        Assert.Contains("arrestata in sicurezza", vm.Status); Assert.True(vm.StartScanCommand.CanExecute(null)); Assert.False(vm.StopScanCommand.CanExecute(null));
    });
    [Fact]
    public async Task ScanCancelsUnsavedDraftsAndRefusesExternalConflictsAndAnActiveBackgroundEngine() => await OnSta(async () =>
    {
        using TestFixture f = new(); int launches = 0, stops = 0; bool running = false;
        using EditorViewModel vm = new(new Window(), new FakeStore(), f.History, f.States,
            scanRunner: (_, _, _) => { launches++; return Task.FromResult(new ScanResult(0, "", "")); },
            workerRunning: () => running, stopWorker: () => { stops++; running = false; return true; }, confirmScanSave: () => false);
        await vm.InitializeAsync(f.Config); vm.Document!.General.Fields["dry_run"].Flag = true;
        Assert.True(vm.StartScanCommand.CanExecute(null)); Assert.False(vm.StopScanCommand.CanExecute(null));
        await vm.StartScanAsync(); Assert.Equal(0, launches); Assert.Contains("annullata", vm.Status);
        vm.SaveCommand.Execute(null); await Eventually(() => !vm.Busy); running = true;
        Assert.False(vm.StartScanCommand.CanExecute(null)); Assert.True(vm.StopScanCommand.CanExecute(null));
        await vm.StartScanAsync(); Assert.Equal(0, launches); Assert.Contains("già attivo", vm.Status);
        await vm.StopScanAsync(); Assert.Equal(1, stops); Assert.Contains("Arresto richiesto", vm.Status);
        Assert.True(vm.StartScanCommand.CanExecute(null)); Assert.False(vm.StopScanCommand.CanExecute(null));
        File.AppendAllText(f.Config, "\n# changed externally\n"); await vm.StartScanAsync(); Assert.Equal(0, launches);
        Assert.Contains("TOML è cambiato", vm.Status);
    });
    [Fact]
    public async Task ScanSavesConfirmedDraftBeforeLaunchingAndBlocksReentrantStart() => await OnSta(async () =>
    {
        using TestFixture f = new(); string engine = Path.Combine(f.Root, "FileOrganizer.exe"); File.WriteAllText(engine, "test engine");
        int confirmations = 0, launches = 0;
        EditorViewModel? editor = null;
        using EditorViewModel vm = new(new Window(), new FakeStore(), f.History, f.States,
            workerRunning: () => false, confirmScanSave: () =>
            {
                confirmations++; Assert.False(editor!.StartScanCommand.CanExecute(null));
                Assert.False(editor.StopScanCommand.CanExecute(null));
                editor.StartScanAsync().GetAwaiter().GetResult(); return true;
            }, scanRunner: (_, config, _) =>
            {
                launches++; Assert.True(ConfigurationLoader.Load(config).General.DryRun);
                return Task.FromResult(new ScanResult(0, "", ""));
            });
        editor = vm; await vm.InitializeAsync(f.Config); vm.EnginePath = engine;
        vm.RawText = f.Valid.Replace("version=1", "version=1\n[general]\ndry_run = true");
        Assert.True(vm.StartScanCommand.CanExecute(null)); await vm.StartScanAsync();
        Assert.Equal(1, confirmations); Assert.Equal(1, launches); Assert.False(vm.IsDirty);
        Assert.Contains("Simulazione completata", vm.Status); Assert.False(vm.StopScanCommand.CanExecute(null));
    });

    [Fact]
    public async Task ScanFailedSaveKeepsDraftAndDiskAndDoesNotLaunch() => await OnSta(async () =>
    {
        using TestFixture f = new(); int launches = 0;
        using EditorViewModel vm = new(new Window(), new FakeStore(), f.History, f.States,
            workerRunning: () => false, confirmScanSave: () => true,
            scanRunner: (_, _, _) => { launches++; return Task.FromResult(new ScanResult(0, "", "")); });
        await vm.InitializeAsync(f.Config); vm.SelectedRule!.Fields["destination"].Text = "";
        await vm.StartScanAsync(); Assert.Equal(0, launches); Assert.True(vm.IsDirty);
        Assert.Equal(f.Valid, File.ReadAllText(f.Config)); Assert.Contains("Save failed", vm.Status);
        Assert.False(vm.Busy); Assert.False(vm.StopScanCommand.CanExecute(null));
    });

    [Fact]
    public async Task ScanRechecksWorkerAfterSavingConfirmedDraft() => await OnSta(async () =>
    {
        using TestFixture f = new(); bool running = false; int launches = 0;
        using EditorViewModel vm = new(new Window(), new FakeStore(), f.History, f.States,
            workerRunning: () => running, confirmScanSave: () => { running = true; return true; },
            scanRunner: (_, _, _) => { launches++; return Task.FromResult(new ScanResult(0, "", "")); });
        await vm.InitializeAsync(f.Config); vm.Document!.General.Fields["dry_run"].Flag = true;
        await vm.StartScanAsync(); Assert.Equal(0, launches); Assert.Contains("già attivo", vm.Status);
        Assert.False(vm.StartScanCommand.CanExecute(null)); Assert.True(vm.StopScanCommand.CanExecute(null));
    });

    [Fact]
    public async Task UnverifiableWorkerStateDisablesBothScanCommandsAndReportsFailure() => await OnSta(async () =>
    {
        using TestFixture f = new(); int launches = 0, stops = 0;
        using EditorViewModel vm = new(new Window(), new FakeStore(), f.History, f.States,
            workerRunning: () => throw new UnauthorizedAccessException("test denied"),
            stopWorker: () => { stops++; return true; },
            scanRunner: (_, _, _) => { launches++; return Task.FromResult(new ScanResult(0, "", "")); });
        await vm.InitializeAsync(f.Config);
        Assert.False(vm.StartScanCommand.CanExecute(null)); Assert.False(vm.StopScanCommand.CanExecute(null));
        await vm.StartScanAsync(); Assert.Contains("test denied", vm.Status);
        await vm.StopScanAsync(); Assert.Contains("test denied", vm.Status);
        Assert.Equal(0, launches); Assert.Equal(0, stops);
    });

    [Fact]
    public async Task WpfScanButtonsReflectBackgroundWorkerTransitions() => await OnSta(async () =>
    {
        using TestFixture f = new(); bool running = false;
        MainWindow window = new(f.Config, f.History, f.States) { Left = -20000, Top = -20000, ShowInTaskbar = false };
        window.Show(); EditorViewModel original = (EditorViewModel)window.DataContext;
        await Eventually(() => original.Document is not null && !original.Busy);
        using EditorViewModel vm = new(window, new FakeStore(), f.History, f.States,
            workerRunning: () => running, stopWorker: () => { running = false; return true; }, confirmScanSave: () => false);
        try
        {
            await vm.InitializeAsync(f.Config); window.DataContext = vm;
            Button start = Descendants<Button>(window).Single(b => Equals(b.Content, "Avvia scansione"));
            Button stop = Descendants<Button>(window).Single(b => Equals(b.Content, "Arresta scansione"));
            await Eventually(() => start.IsEnabled && !stop.IsEnabled);
            running = true; CommandManager.InvalidateRequerySuggested();
            await Eventually(() => !start.IsEnabled && stop.IsEnabled);
            System.Windows.Automation.Peers.ButtonAutomationPeer peer = new(stop);
            ((System.Windows.Automation.Provider.IInvokeProvider)peer.GetPattern(System.Windows.Automation.Peers.PatternInterface.Invoke)).Invoke();
            await Eventually(() => start.IsEnabled && !stop.IsEnabled);
            vm.Document!.General.Fields["dry_run"].Flag = true;
            await Eventually(() => start.IsEnabled);
            await vm.StartScanAsync(); Assert.True(vm.IsDirty); Assert.Contains("annullata", vm.Status);
            Assert.Equal(f.Valid, File.ReadAllText(f.Config));
        }
        finally { window.DataContext = original; window.Close(); }
    });

    [Fact]
    public async Task ScanShowsSuccessDryRunAndFailureResults() => await OnSta(async () =>
    {
        using TestFixture f = new(); string engine = Path.Combine(f.Root, "FileOrganizer.exe"); File.WriteAllText(engine, "test engine");
        ScanResult result = new(0, "", "");
        using EditorViewModel vm = new(new Window(), new FakeStore(), f.History, f.States,
            scanRunner: (_, _, _) => Task.FromResult(result), workerRunning: () => false);
        await vm.InitializeAsync(f.Config); vm.EnginePath = engine; await vm.StartScanAsync(); Assert.Contains("Scansione completata", vm.Status);
        vm.Document!.General.Fields["dry_run"].Flag = true; vm.SaveCommand.Execute(null); await Eventually(() => !vm.Busy);
        await vm.StartScanAsync(); Assert.Contains("Simulazione completata", vm.Status);
        result = new(3, "", "test diagnostic"); await vm.StartScanAsync(); Assert.Contains("test diagnostic", vm.Status); Assert.False(vm.Busy);
    });
    [Fact]
    public async Task ProtectedEnginePidDoesNotCrashConfigurationLoading() => await OnSta(async () =>
    {
        using TestFixture f = new(); Configuration config = ConfigurationLoader.Load(f.Config);
        f.States.Write(f.States.Create(f.Config, f.Valid, config) with { Pid = 4 });
        using EditorViewModel vm = new(new Window(), new FakeStore(), f.History, f.States);
        await vm.InitializeAsync(f.Config);
        Assert.NotNull(vm.Document); Assert.False(vm.Busy);
        Assert.True(vm.RuntimeMessage.Contains("UNVERIFIED") || vm.RuntimeMessage.Contains("not verified"));
        vm.PathView = "Applied engine"; await vm.LoadPathsAsync(); Assert.Empty(vm.PathRoots);
        Assert.Contains("unavailable", vm.TreeStatus, StringComparison.OrdinalIgnoreCase);
    });
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task OlderWorkerSnapshotsCannotClaimTheNewDirectoryPolicy(int version) => await OnSta(async () =>
    {
        using TestFixture f = new(); Configuration config = ConfigurationLoader.Load(f.Config);
        f.States.Write(f.States.Create(f.Config, f.Valid, config) with { Version = version });
        using EditorViewModel vm = new(new Window(), new FakeStore(), f.History, f.States);
        await vm.InitializeAsync(f.Config); Assert.Contains("ENGINE PID", vm.RuntimeMessage);
        vm.PathView = "Applied engine"; await vm.LoadPathsAsync();
        Assert.Empty(vm.PathRoots); Assert.False(vm.CanEditTree);
        Assert.Contains("1.2.6", vm.TreeStatus);
    });
    [Fact]
    public async Task PathTreeMouseButtonsAndGrayExcludedRowsWorkInTheRealWindow() => await OnSta(async () =>
    {
        using TestFixture f = new(); Directory.CreateDirectory(Path.Combine(f.Root, "in", "project", "animations"));
        MainWindow window = new(f.Config, f.History, f.States) { Left = -20000, Top = -20000, ShowInTaskbar = false }; window.Show();
        EditorViewModel vm = (EditorViewModel)window.DataContext;
        try
        {
            await Eventually(() => vm.Document is not null && !vm.Busy);
            TabControl tabs = Descendants<TabControl>(window).Single(); tabs.SelectedIndex = 3; await Eventually(() => !vm.Busy);
            PathTreeNode root = vm.PathRoots[0]; await root.LoadChildrenAsync(); root.Expanded = true;
            await window.Dispatcher.InvokeAsync(window.UpdateLayout, DispatcherPriority.ApplicationIdle);
            PathTreeNode project = root.Children.Single(p => p.Path == Path.Combine(f.Root, "in", "project"));
            TreeViewItem row = Descendants<TreeViewItem>(window).Single(i => ReferenceEquals(i.DataContext, project));
            Assert.Equal(Colors.Gray, ((SolidColorBrush)row.Foreground).Color); Assert.Empty(project.Children);
            Button button = Descendants<Button>(row).First(b => ReferenceEquals(b.DataContext, project)); Assert.True(button.IsEnabled);
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Eventually(() => project.Symbol == "■" && project.Children.Any(c => !c.Placeholder));
            Assert.Equal("□", project.Children.Single().Symbol);
            MouseButtonEventArgs right = new(Mouse.PrimaryDevice, 0, MouseButton.Right) { RoutedEvent = UIElement.PreviewMouseRightButtonUpEvent };
            button.RaiseEvent(right); await Eventually(() => project.Symbol == "□");
            Assert.True(right.Handled); Assert.Empty(project.Children);
            await window.Dispatcher.InvokeAsync(window.UpdateLayout, DispatcherPriority.ApplicationIdle);
            Assert.Equal(Colors.Gray, ((SolidColorBrush)row.Foreground).Color);
        }
        finally { vm.SaveCommand.Execute(null); await Eventually(() => !vm.Busy); window.Close(); }
    });

    private static async Task Eventually(Func<bool> predicate)
    {
        Stopwatch clock = Stopwatch.StartNew();
        while (!predicate()) { Assert.True(clock.Elapsed < TimeSpan.FromSeconds(10), "UI operation timed out"); await Task.Delay(20); }
    }
    private static async Task OnSta(Func<Task> test)
    {
        TaskCompletionSource complete = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Thread thread = new(() =>
        {
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
            Dispatcher dispatcher = Dispatcher.CurrentDispatcher;
            dispatcher.BeginInvoke(async () =>
            {
                try { await test(); complete.SetResult(); }
                catch (Exception exception) { complete.SetException(exception); }
                finally { dispatcher.BeginInvokeShutdown(DispatcherPriority.Background); }
            });
            Dispatcher.Run();
        });
        thread.SetApartmentState(ApartmentState.STA); thread.IsBackground = true; thread.Start();
        await complete.Task.WaitAsync(TimeSpan.FromSeconds(30));
    }
    private static IEnumerable<T> Descendants<T>(DependencyObject parent) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(parent, i);
            if (child is T typed) yield return typed;
            foreach (T nested in Descendants<T>(child)) yield return nested;
        }
    }
    private static void AssertNoBindingErrors(DependencyObject root)
    {
        foreach (TextBox box in Descendants<TextBox>(root))
        {
            BindingExpression? expression = box.GetBindingExpression(TextBox.TextProperty);
            if (expression is not null && box.IsEnabled) Assert.NotEqual(BindingStatus.PathError, expression.Status);
        }
        foreach (CheckBox box in Descendants<CheckBox>(root))
        {
            BindingExpression? expression = box.GetBindingExpression(CheckBox.IsCheckedProperty);
            if (expression is not null && box.IsEnabled) Assert.NotEqual(BindingStatus.PathError, expression.Status);
        }
    }
    private sealed class TestFixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "FileOrganizer.GuiTests", Guid.NewGuid().ToString("N"));
        public string Config => Path.Combine(Root, "config.toml");
        public MoveHistory History => new(Path.Combine(Root, "history"));
        public EngineStateStore States => new(Path.Combine(Root, "state"));
        public string Valid => $"version=1\n[[folders]]\nname='Test'\npath='{Root}\\in'\n[[folders.rules]]\nname='text'\npatterns=['*.txt']\ndestination='sorted'\n";
        public TestFixture() { Directory.CreateDirectory(Path.Combine(Root, "in")); File.WriteAllText(Config, Valid); }
        public void Dispose() { if (Directory.Exists(Root)) Directory.Delete(Root, true); }
    }
    private sealed class FakeStore : IStartupStore
    {
        public string? Value { get; private set; }
        public bool FailRead { get; set; }
        public bool FailWrite { get; set; }
        public string? Get(string name) => FailRead ? throw new UnauthorizedAccessException("read denied") : Value;
        public void Set(string name, string command) { if (FailWrite) throw new UnauthorizedAccessException("write denied"); Value = command; }
        public void Remove(string name) { if (FailWrite) throw new UnauthorizedAccessException("write denied"); Value = null; }
    }
}
