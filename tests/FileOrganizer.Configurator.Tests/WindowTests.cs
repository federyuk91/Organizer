using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;
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
            for (int index = 0; index < 4; index++)
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
            CheckBox recursion = Descendants<CheckBox>(window).Single(c => Equals(c.Content, "Include subfolders"));
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
