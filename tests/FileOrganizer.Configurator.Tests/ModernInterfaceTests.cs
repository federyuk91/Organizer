using System.IO;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using FileOrganizer.Configurator;
using FileOrganizer.Core;
using FileOrganizer.Windows;
using Xunit;

namespace FileOrganizer.Configurator.Tests;

public sealed partial class WindowTests
{
    [Fact]
    public async Task ManageSubfoldersConfirmsReplacesOnlySelectedBranchAndPersists()
        => await OnSta(async () =>
    {
        using TestFixture f = new(); using EditorViewModel vm = new(new Window(), new FakeStore(), f.History, f.States);
        await vm.InitializeAsync(f.Config);
        ConfigurationSection section = vm.SelectedFolder!;
        string source = vm.Document!.Validate().Folders[0].Path;
        string child = Path.Combine(source, "project"); Directory.CreateDirectory(Path.Combine(child, "animations"));
        string external = Path.Combine(f.Root, "external"); Directory.CreateDirectory(external);
        string excludedExternal = Path.Combine(external, "keep"); Directory.CreateDirectory(excludedExternal);
        vm.AddRuleCommand.Execute(null); vm.SelectedRule!.Fields["patterns"].Text = "*.pdf";
        vm.SelectedRule.Fields["destination"].Text = external;
        section.Fields["excluded_paths"].Text = child + "\n" + excludedExternal;
        Assert.False(vm.ManageSubfolders);
        string before = vm.Document.Render();
        await vm.ChangeManagedSubfoldersAsync(true, message => { Assert.Contains("Managed paths", message); Assert.Contains(source, message); return false; });
        Assert.Equal(before, vm.Document.Render());
        await vm.LoadPathsAsync();
        await vm.ChangeManagedSubfoldersAsync(true, _ => true);
        Assert.True(vm.ManageSubfolders); Assert.True(vm.CanEditTree);
        Folder folder = vm.Document.Validate().Folders[0]; DirectoryScope scope = new(folder);
        Assert.True(scope.Complete(source)); Assert.Equal(DirectoryMode.Recursive, scope.Mode(Path.Combine(child, "future", "nested")));
        Assert.DoesNotContain(folder.PathOverrides, p => p.Path == child);
        Assert.Equal(DirectoryMode.Direct, scope.Mode(external)); Assert.Equal(DirectoryMode.Off, scope.Mode(excludedExternal));
        Assert.Equal(f.Valid, File.ReadAllText(f.Config));
        vm.SaveCommand.Execute(null); await Eventually(() => !vm.Busy && !vm.IsDirty);
        Assert.True(vm.ManageSubfolders); Assert.True(new DirectoryScope(ConfigurationLoader.Load(f.Config).Folders[0]).Complete(source));
        await vm.ChangeManagedSubfoldersAsync(false, _ => true);
        Assert.False(vm.ManageSubfolders); scope = new(vm.Document!.Validate().Folders[0]);
        Assert.Equal(DirectoryMode.Direct, scope.Mode(source)); Assert.Equal(DirectoryMode.Off, scope.Mode(child));
        Assert.Equal(DirectoryMode.Off, scope.Mode(excludedExternal));
    });

    [Fact]
    public async Task ManageSubfoldersDoesNotApplyAfterDraftChangesDuringConfirmation() => await OnSta(async () =>
    {
        using TestFixture f = new(); using EditorViewModel vm = new(new Window(), new FakeStore(), f.History, f.States);
        await vm.InitializeAsync(f.Config);
        await vm.ChangeManagedSubfoldersAsync(true, _ => { vm.SelectedFolder!.Fields["name"].Text = "Changed while confirming"; return true; });
        Assert.Empty(vm.Document!.Validate().Folders[0].PathOverrides); Assert.Contains("bozza è cambiata", vm.Status);
        Assert.Equal(f.Valid, File.ReadAllText(f.Config));
    });

    [Fact]
    public async Task ReloadAndValidateReportProgressSuccessAndErrorsWithoutWriting() => await OnSta(async () =>
    {
        using TestFixture f = new(); using EditorViewModel vm = new(new Window(), new FakeStore(), f.History, f.States);
        await vm.InitializeAsync(f.Config);
        List<EditorFeedbackState> feedback = [];
        vm.PropertyChanged += (_, args) => { if (args.PropertyName == nameof(vm.ActionFeedbackState)) feedback.Add(vm.ActionFeedbackState); };
        vm.ValidateCommand.Execute(null); await Eventually(() => !vm.Busy);
        Assert.Contains(EditorFeedbackState.Running, feedback); Assert.Equal(EditorFeedbackState.Success, vm.ActionFeedbackState);
        Assert.Contains("Configurazione valida", vm.ActionFeedback); Assert.Equal(f.Valid, File.ReadAllText(f.Config));
        vm.ReloadCommand.Execute(null); await Eventually(() => !vm.Busy);
        Assert.Equal(EditorFeedbackState.Success, vm.ActionFeedbackState); Assert.Contains("ricaricata", vm.ActionFeedback);
        vm.RawText = "invalid = ["; Assert.Empty(vm.ActionFeedback);
        vm.ValidateCommand.Execute(null); await Eventually(() => !vm.Busy);
        Assert.Equal(EditorFeedbackState.Error, vm.ActionFeedbackState); Assert.Contains("non valida", vm.ActionFeedback);
        Assert.Equal(f.Valid, File.ReadAllText(f.Config));
        vm.RawText = f.Valid; vm.ApplyRawCommand.Execute(null); Assert.False(vm.IsDirty);
        File.WriteAllText(f.Config, "invalid = [");
        vm.ReloadCommand.Execute(null); await Eventually(() => !vm.Busy);
        Assert.Equal(EditorFeedbackState.Error, vm.ActionFeedbackState); Assert.Contains("TOML non valido", vm.ActionFeedback);
    });

    [Fact]
    public async Task ModernHistoryCancelRemainsClickableDuringUndoAndFolderControlsAreUnique() => await OnSta(async () =>
    {
        using TestFixture f = new(); InterfacePreferenceStore store = new(Path.Combine(f.Root, "interface.txt")); store.Save(EditorInterface.Modern);
        MainWindow window = TestWindow(f, store); window.Show(); EditorViewModel vm = (EditorViewModel)window.DataContext;
        using CancellationTokenSource cancellation = new();
        System.Reflection.FieldInfo undoField = typeof(EditorViewModel).GetField("undoCancellation", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        try
        {
            await Eventually(() => vm.Document is not null && !vm.Busy);
            Assert.DoesNotContain(Descendants<Button>(window), b => ReferenceEquals(b.Command, vm.CancelUndoCommand));
            Assert.Single(Descendants<CheckBox>(window), c => System.Windows.Data.BindingOperations.GetBinding(c, CheckBox.IsCheckedProperty)?.Path.Path == "ManageSubfolders");
            Assert.DoesNotContain(Descendants<CheckBox>(window), c => System.Windows.Data.BindingOperations.GetBinding(c, CheckBox.IsCheckedProperty)?.Path.Path == "SelectedFolder.Fields[enabled].Flag");
            CheckBox subfolders = Descendants<CheckBox>(window).Single(c => Equals(c.Content, "Manage subfolder"));
            Assert.Contains("Managed paths", subfolders.ToolTip.ToString());
            Navigate(window, EditorPage.History); await Eventually(() => !vm.Busy);
            await window.Dispatcher.InvokeAsync(window.UpdateLayout, DispatcherPriority.ApplicationIdle);
            Button cancel = Descendants<Button>(window).Single(b => ReferenceEquals(b.Command, vm.CancelUndoCommand)); Assert.False(cancel.IsEnabled);
            // Model the active cancellation boundary without showing an undo confirmation or moving files.
            undoField.SetValue(vm, cancellation); typeof(EditorViewModel).GetProperty(nameof(vm.Busy))!.SetValue(vm, true);
            await Eventually(() => cancel.IsEnabled);
            Assert.True(vm.CanNavigate); Assert.False(vm.CanEditForm);
            ButtonAutomationPeer peer = new(cancel); ((IInvokeProvider)peer.GetPattern(PatternInterface.Invoke)).Invoke();
            await Eventually(() => cancellation.IsCancellationRequested);
            AssertNoBindingErrors(window);
        }
        finally { undoField.SetValue(vm, null); typeof(EditorViewModel).GetProperty(nameof(vm.Busy))!.SetValue(vm, false); window.Close(); }
    });

    [Fact]
    public async Task ModernStatusCheckboxIconsAndFileActionsReflectTheSharedModel() => await OnSta(async () =>
    {
        using TestFixture f = new();
        InterfacePreferenceStore store = new(Path.Combine(f.Root, "interface.txt")); store.Save(EditorInterface.Modern);
        MainWindow window = TestWindow(f, store); window.Show(); EditorViewModel vm = (EditorViewModel)window.DataContext;
        try
        {
            await Eventually(() => vm.Document is not null && !vm.Busy);
            Assert.True(vm.IsFormSynchronized); Assert.False(vm.IsEngineSynchronized);
            Assert.Equal(f.Config, vm.SavedStatusRows[0].Value);
            Assert.Equal("ON DISK:", vm.SavedStatusRows[0].Label);
            Expander status = Descendants<Expander>(window).Single(e => Equals(e.Header, "File and engine status")); status.IsExpanded = true;
            await window.Dispatcher.InvokeAsync(window.UpdateLayout, DispatcherPriority.ApplicationIdle);
            ContentControl[] indicators = Descendants<ContentControl>(window).Where(c => ReferenceEquals(c.Style, window.ActiveSurface.FindResource("StatusIndicator"))).ToArray();
            Assert.Equal(2, indicators.Length); Assert.Equal(true, indicators[0].Tag); Assert.Equal(false, indicators[1].Tag);
            Assert.Same(window.ActiveSurface.FindResource("SuccessBrush"), ((Border)indicators[0].Template.FindName("Indicator", indicators[0])).Background);
            Assert.Same(window.ActiveSurface.FindResource("ErrorBrush"), ((Border)indicators[1].Template.FindName("Indicator", indicators[1])).Background);
            TextBlock diskLabel = Descendants<TextBlock>(status).Single(t => t.Text == "ON DISK:");
            Assert.Same(window.ActiveSurface.FindResource("AccentBrush"), diskLabel.Foreground);
            string output = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../artifacts/gui-preview")); Directory.CreateDirectory(output);
            FrameworkElement content = (FrameworkElement)window.Content;
            RenderTargetBitmap bitmap = new((int)content.ActualWidth, (int)content.ActualHeight, 96, 96, PixelFormats.Pbgra32); bitmap.Render(content);
            PngBitmapEncoder encoder = new(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using (FileStream image = File.Create(Path.Combine(output, "modern-status.png"))) encoder.Save(image);
            foreach (Button button in Descendants<Button>(window).Where(b => b.Tag is Geometry))
            {
                System.Windows.Shapes.Path icon = (System.Windows.Shapes.Path)button.Template.FindName("ActionIcon", button);
                Assert.Equal(8, icon.Margin.Right); Assert.Equal(14, icon.Width);
            }
            Button open = Descendants<Button>(window).Single(b => ReferenceEquals(b.Command, vm.OpenCommand));
            TextBox config = Descendants<TextBox>(window).Single(t => System.Windows.Data.BindingOperations.GetBinding(t, TextBox.TextProperty)?.Path.Path == "ConfigPath");
            Assert.Same(config.Parent, open.Parent);
            Button start = Descendants<Button>(window).Single(b => ReferenceEquals(b.Command, vm.StartScanCommand));
            Assert.True(start.TranslatePoint(new Point(0, 0), window).Y > window.ActualHeight * 0.8);
            CheckBox recursive = Descendants<CheckBox>(window).Single(c => Equals(c.Content, "Manage subfolder"));
            await vm.ChangeManagedSubfoldersAsync(true, _ => true);
            await Eventually(() => !vm.IsFormSynchronized);
            Assert.True(vm.ManageSubfolders);
            Assert.Equal(Visibility.Visible, ((System.Windows.Shapes.Path)recursive.Template.FindName("Tick", recursive)).Visibility);
            vm.SaveCommand.Execute(null); await Eventually(() => !vm.Busy && vm.IsFormSynchronized);
            Configuration configOnDisk = ConfigurationLoader.Load(f.Config);
            EngineSnapshot snapshot = f.States.Create(f.Config, File.ReadAllText(f.Config), configOnDisk);
            f.States.Write(snapshot); await Eventually(() => vm.IsEngineSynchronized);
            Assert.Equal("TOML:", vm.RuntimeStatusRows[1].Label); Assert.Equal(f.Config, vm.RuntimeStatusRows[1].Value);
            f.States.Write(snapshot with { ReloadError = "test rejection" });
            await Eventually(() => !vm.IsEngineSynchronized && vm.EngineStatusMessage.Contains("test rejection"));
            Navigate(window, EditorPage.Toml); await Eventually(() => !vm.Busy);
            await window.Dispatcher.InvokeAsync(window.UpdateLayout, DispatcherPriority.ApplicationIdle);
            Assert.Single(Descendants<Button>(window), b => ReferenceEquals(b.Command, vm.OpenCurrentTomlCommand));
            Assert.True(vm.OpenCurrentTomlCommand.CanExecute(null));
            AssertNoBindingErrors(window);
        }
        finally { window.Close(); }
    });

    [Theory]
    [InlineData(EditorInterface.Classic)]
    [InlineData(EditorInterface.Modern)]
    public async Task SaveRequiresChangesWhileSaveAsRemainsAvailable(EditorInterface mode) => await OnSta(async () =>
    {
        using TestFixture f = new();
        InterfacePreferenceStore store = new(Path.Combine(f.Root, "interface.txt")); store.Save(mode);
        MainWindow window = TestWindow(f, store); window.Show(); EditorViewModel vm = (EditorViewModel)window.DataContext;
        try
        {
            await Eventually(() => vm.Document is not null && !vm.Busy);
            Button save = Descendants<Button>(window).Single(b => ReferenceEquals(b.Command, vm.SaveCommand));
            Button saveAs = Descendants<Button>(window).Single(b => ReferenceEquals(b.Command, vm.SaveAsCommand));
            await Eventually(() => !save.IsEnabled && saveAs.IsEnabled);
            vm.SelectedFolder!.Fields["name"].Text = "Changed";
            await Eventually(() => save.IsEnabled && saveAs.IsEnabled);
            vm.SaveCommand.Execute(null);
            await Eventually(() => !vm.Busy && !vm.IsDirty && !save.IsEnabled && saveAs.IsEnabled);
            vm.RawText += "\n# Edited TOML\n";
            await Eventually(() => save.IsEnabled && saveAs.IsEnabled);
            vm.SaveCommand.Execute(null);
            await Eventually(() => !vm.Busy && !vm.IsDirty && !save.IsEnabled && saveAs.IsEnabled);
            Assert.Contains("# Edited TOML", File.ReadAllText(f.Config));
        }
        finally { window.Close(); }
    });

    private static MainWindow TestWindow(TestFixture f, InterfacePreferenceStore store) =>
        new(f.Config, f.History, f.States, store) { Left = -20000, Top = -20000, ShowInTaskbar = false };
    private static void Navigate(MainWindow window, EditorPage page)
    {
        TabControl tabs = Descendants<TabControl>(window).Single();
        tabs.SelectedItem = tabs.Items.Cast<TabItem>().Single(t => Equals(t.Tag, page.ToString()));
    }
    [Fact]
    public void InterfacePreferenceDefaultsAndAtomicPersistenceDoNotTouchToml()
    {
        using TestFixture f = new(); InterfacePreferenceStore store = new(Path.Combine(f.Root, "interface.txt"));
        Assert.Equal(EditorInterface.Classic, store.Read(out string? warning)); Assert.Null(warning);
        store.Save(EditorInterface.Modern); Assert.Equal(EditorInterface.Modern, store.Read(out warning)); Assert.Null(warning);
        store.Save(EditorInterface.Classic); Assert.Equal(EditorInterface.Classic, store.Read(out warning));
        File.WriteAllText(store.FilePath, "unexpected"); Assert.Equal(EditorInterface.Classic, store.Read(out warning)); Assert.NotNull(warning);
        Assert.Equal(f.Valid, File.ReadAllText(f.Config)); Assert.Empty(Directory.GetFiles(f.Root, ".interface-*.tmp"));
    }
    [Fact]
    public async Task ModernPresetPopupAndAddInvokeTheSharedRuleCommand() => await OnSta(async () =>
    {
        using TestFixture f = new(); InterfacePreferenceStore store = new(Path.Combine(f.Root, "interface.txt")); store.Save(EditorInterface.Modern);
        MainWindow window = TestWindow(f, store); window.Show(); EditorViewModel vm = (EditorViewModel)window.DataContext;
        try
        {
            await Eventually(() => vm.Document is not null && !vm.Busy);
            ComboBox picker = Descendants<ComboBox>(window).Single(c => c.Items.Count > 0 && c.Items[0] is RulePreset);
            ComboBoxAutomationPeer pickerPeer = new(picker); ((IExpandCollapseProvider)pickerPeer.GetPattern(PatternInterface.ExpandCollapse)).Expand();
            await window.Dispatcher.InvokeAsync(window.UpdateLayout, DispatcherPriority.ApplicationIdle);
            picker.SelectedIndex = 1; Assert.Equal("Images", vm.SelectedRulePreset.Name);
            ((IExpandCollapseProvider)pickerPeer.GetPattern(PatternInterface.ExpandCollapse)).Collapse();
            Button add = Descendants<Button>(window).Single(b => ReferenceEquals(b.Command, vm.AddRuleCommand)); await Eventually(() => add.IsEnabled);
            ButtonAutomationPeer peer = new(add); ((IInvokeProvider)peer.GetPattern(PatternInterface.Invoke)).Invoke();
            await Eventually(() => vm.SelectedFolder!.Rules.Count == 2);
            Assert.Equal("Images", vm.SelectedRule?.DisplayName); Assert.Contains(".png", vm.SelectedRule!.Fields["extensions"].Text);
            vm.SelectedRule.Fields["destination"].Text = "Images";
            vm.SaveCommand.Execute(null); await Eventually(() => !vm.Busy && !vm.IsDirty);
            Assert.Equal("Images", ConfigurationLoader.Load(f.Config).Folders[0].Rules[1].Name);
        }
        finally { window.Close(); }
    });
    [Fact]
    public async Task InterfaceSwitchButtonChangesViewsAndPersistsClassicAgain() => await OnSta(async () =>
    {
        using TestFixture f = new(); InterfacePreferenceStore store = new(Path.Combine(f.Root, "interface.txt"));
        MainWindow window = TestWindow(f, store); window.Show(); EditorViewModel vm = (EditorViewModel)window.DataContext;
        try
        {
            await Eventually(() => vm.Document is not null && !vm.Busy);
            Button button = (Button)window.FindName("InterfaceSwitch");
            Image icon = Assert.IsType<Image>(button.Content);
            TextBlock label = (TextBlock)window.FindName("InterfaceLabel");
            Assert.Equal(button.ActualWidth, button.ActualHeight);
            Assert.Equal(Dock.Left, DockPanel.GetDock(button));
            Assert.Equal("Classic", label.Text); Assert.Contains("passa a Modern", button.ToolTip.ToString());
            Assert.Same(window.FindResource("ClassicInterfaceIcon"), icon.Source);
            Assert.True(button.TranslatePoint(new Point(), window).X < label.TranslatePoint(new Point(), window).X);
            ButtonAutomationPeer peer = new(button);
            ((IInvokeProvider)peer.GetPattern(PatternInterface.Invoke)).Invoke();
            await Eventually(() => window.ActiveInterface == EditorInterface.Modern && button.IsEnabled);
            Assert.Equal(EditorInterface.Modern, store.Read(out _));
            Assert.Equal("Modern", label.Text); Assert.Contains("passa a Classic", button.ToolTip.ToString());
            Assert.Same(window.FindResource("ModernInterfaceIcon"), icon.Source);
            ((IInvokeProvider)peer.GetPattern(PatternInterface.Invoke)).Invoke();
            await Eventually(() => window.ActiveInterface == EditorInterface.Classic && button.IsEnabled);
            Assert.Equal(EditorInterface.Classic, store.Read(out _)); Assert.Equal(f.Valid, File.ReadAllText(f.Config));
            Assert.Same(window.FindResource("ClassicInterfaceIcon"), icon.Source); Assert.Equal("Classic", label.Text);
        }
        finally { window.Close(); }
    });
    [Fact]
    public async Task ModernStartupToggleUsesTheSharedActualRegistrationCommandsWithAnIsolatedStore() => await OnSta(async () =>
    {
        using TestFixture f = new(); FakeStore registry = new();
        MainWindow window = TestWindow(f, new(Path.Combine(f.Root, "interface.txt"))); window.Show();
        EditorViewModel original = (EditorViewModel)window.DataContext; await Eventually(() => original.Document is not null && !original.Busy);
        using EditorViewModel vm = new(window, registry, f.History, f.States);
        await vm.InitializeAsync(f.Config); string engine = Path.Combine(f.Root, "FileOrganizer.exe"); File.WriteAllText(engine, "fixture"); vm.EnginePath = engine;
        window.DataContext = vm;
        try
        {
            await window.SwitchInterfaceAsync(); Navigate(window, EditorPage.General);
            await window.Dispatcher.InvokeAsync(window.UpdateLayout, DispatcherPriority.ApplicationIdle);
            CheckBox toggle = Descendants<CheckBox>(window).Single(c => Equals(c.Content, "Start with Windows"));
            toggle.IsChecked = true; toggle.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent, toggle));
            await Eventually(() => !vm.Busy && vm.StartWithWindows == true);
            Assert.Contains(engine, registry.Value); Assert.Contains("run --background", registry.Value);
            toggle.IsChecked = false; toggle.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent, toggle));
            await Eventually(() => !vm.Busy && vm.StartWithWindows == false);
            Assert.Null(registry.Value); Assert.Equal(f.Valid, File.ReadAllText(f.Config));
        }
        finally { window.DataContext = original; window.Close(); }
    });
    [Fact]
    public async Task SwitchingKeepsTheSameDraftRawTextSelectionsAndRestoresPreference() => await OnSta(async () =>
    {
        using TestFixture f = new(); InterfacePreferenceStore store = new(Path.Combine(f.Root, "interface.txt"));
        MainWindow window = TestWindow(f, store); window.Show(); EditorViewModel vm = (EditorViewModel)window.DataContext;
        try
        {
            await Eventually(() => vm.Document is not null && !vm.Busy);
            Assert.Equal(EditorInterface.Classic, window.ActiveInterface);
            vm.AddRuleCommand.Execute(null); vm.SelectedRule!.Fields["name"].Text = "Unsaved rule";
            vm.SelectedRule.Fields["extensions"].Text = ".png"; vm.SelectedRule.Fields["destination"].Text = "Images";
            ConfigurationDocument draft = vm.Document!; ConfigurationSection folder = vm.SelectedFolder!, rule = vm.SelectedRule;
            Navigate(window, EditorPage.Toml); vm.RawText = vm.Document!.Render() + "\n# unapplied draft\n";
            string raw = vm.RawText;
            await window.SwitchInterfaceAsync();
            Assert.Equal(EditorInterface.Modern, window.ActiveInterface); Assert.Same(vm, window.ActiveSurface.DataContext);
            Assert.Equal(EditorPage.Toml, window.ActiveSurface.SelectedPage); Assert.Same(draft, vm.Document);
            Assert.Same(folder, vm.SelectedFolder); Assert.Same(rule, vm.SelectedRule); Assert.Equal(raw, vm.RawText); Assert.True(vm.IsDirty);
            Assert.Equal(f.Valid, File.ReadAllText(f.Config));
            await window.SwitchInterfaceAsync(); Assert.Equal(EditorInterface.Classic, window.ActiveInterface);
            Assert.Same(draft, vm.Document); Assert.Equal(raw, vm.RawText); Assert.Same(rule, vm.SelectedRule);
            await window.SwitchInterfaceAsync();
            vm.SaveCommand.Execute(null); await Eventually(() => !vm.Busy && !vm.IsDirty);
            Assert.Contains("# unapplied draft", File.ReadAllText(f.Config)); Assert.Equal(2, ConfigurationLoader.Load(f.Config).Folders[0].Rules.Length);
        }
        finally { window.Close(); }
        MainWindow reopened = TestWindow(f, store); reopened.Show();
        try { Assert.Equal(EditorInterface.Modern, reopened.ActiveInterface); await Eventually(() => ((EditorViewModel)reopened.DataContext).Document is not null && !((EditorViewModel)reopened.DataContext).Busy); }
        finally { reopened.Close(); }
    });
    [Fact]
    public async Task BothInterfacesExposeTheSameCommandsAndModernPagesRenderAndSave() => await OnSta(async () =>
    {
        using TestFixture f = new(); InterfacePreferenceStore store = new(Path.Combine(f.Root, "interface.txt"));
        MainWindow window = TestWindow(f, store); window.Width = 1320; window.Show(); EditorViewModel vm = (EditorViewModel)window.DataContext;
        Dictionary<EditorInterface, HashSet<ICommand>> commands = [];
        try
        {
            await Eventually(() => vm.Document is not null && !vm.Busy);
            foreach (EditorInterface mode in new[] { EditorInterface.Classic, EditorInterface.Modern })
            {
                if (window.ActiveInterface != mode) await window.SwitchInterfaceAsync();
                commands[mode] = [];
                foreach (EditorPage page in Enum.GetValues<EditorPage>())
                {
                    Navigate(window, page); await Eventually(() => !vm.Busy);
                    await window.Dispatcher.InvokeAsync(window.UpdateLayout, DispatcherPriority.ApplicationIdle);
                    (Expander Control, bool Expanded)[] disclosures = Descendants<Expander>(window).Select(e => (e, e.IsExpanded)).ToArray();
                    foreach ((Expander control, _) in disclosures) control.IsExpanded = true;
                    await window.Dispatcher.InvokeAsync(window.UpdateLayout, DispatcherPriority.ApplicationIdle);
                    foreach (Button button in Descendants<Button>(window)) if (button.Command is not null) commands[mode].Add(button.Command);
                    foreach ((Expander control, bool expanded) in disclosures) control.IsExpanded = expanded;
                    await window.Dispatcher.InvokeAsync(window.UpdateLayout, DispatcherPriority.ApplicationIdle);
                    AssertNoBindingErrors(window);
                    if (mode == EditorInterface.Modern)
                    {
                        string output = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../artifacts/gui-preview")); Directory.CreateDirectory(output);
                        FrameworkElement content = (FrameworkElement)window.Content;
                        RenderTargetBitmap bitmap = new((int)content.ActualWidth, (int)content.ActualHeight, 96, 96, PixelFormats.Pbgra32); bitmap.Render(content);
                        PngBitmapEncoder encoder = new(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                        using FileStream image = File.Create(Path.Combine(output, "modern-" + page + ".png")); encoder.Save(image);
                        vm.Document!.General.Fields["log_level"].Text = page == EditorPage.History ? "info" : "debug";
                        vm.SaveCommand.Execute(null); await Eventually(() => !vm.Busy && !vm.IsDirty);
                        Assert.True(vm.HasFolder); Assert.True(vm.HasRule); Assert.True(vm.CanEditForm);
                    }
                }
            }
            Assert.Contains(vm.OpenCurrentTomlCommand, commands[EditorInterface.Modern]);
            Assert.True(commands[EditorInterface.Classic].IsSubsetOf(commands[EditorInterface.Modern]),
                "Missing in Modern: " + string.Join(", ", typeof(EditorViewModel).GetProperties().Where(p => typeof(ICommand).IsAssignableFrom(p.PropertyType) &&
                    commands[EditorInterface.Classic].Contains((ICommand)p.GetValue(vm)!) && !commands[EditorInterface.Modern].Contains((ICommand)p.GetValue(vm)!)).Select(p => p.Name)));
            Assert.NotNull(ConfigurationLoader.Load(f.Config));
            Navigate(window, EditorPage.Folders);
            vm.AddRuleCommand.Execute(null); vm.SelectedRule!.Fields["name"].Text = "Images";
            vm.SelectedRule.Fields["extensions"].Text = ".png"; vm.SelectedRule.Fields["destination"].Text = "Images";
            vm.RuleUpCommand.Execute(null); vm.PreviewName = "example.PNG"; vm.PreviewCommand.Execute(null); await Eventually(() => !vm.Busy);
            Assert.Contains("Images", vm.PreviewResult);
            vm.SaveCommand.Execute(null); await Eventually(() => !vm.Busy && !vm.IsDirty);
            Assert.Equal("Images", ConfigurationLoader.Load(f.Config).Folders[0].Rules[0].Name);
        }
        finally { if (vm.IsDirty) { vm.SaveCommand.Execute(null); await Eventually(() => !vm.Busy); } window.Close(); }
    });
    [Fact]
    public async Task SwitchDuringScanKeepsTheOperationAndStopCommand() => await OnSta(async () =>
    {
        using TestFixture f = new(); MainWindow window = TestWindow(f, new(Path.Combine(f.Root, "interface.txt"))); window.Show();
        EditorViewModel original = (EditorViewModel)window.DataContext; await Eventually(() => original.Document is not null && !original.Busy);
        TaskCompletionSource<ScanResult> completion = new(TaskCreationOptions.RunContinuationsAsynchronously); int launches = 0;
        using EditorViewModel vm = new(window, new FakeStore(), f.History, f.States,
            workerRunning: () => false, scanRunner: (_, _, token) => { launches++; token.Register(() => completion.TrySetResult(new(130, "", ""))); return completion.Task; });
        await vm.InitializeAsync(f.Config); string engine = Path.Combine(f.Root, "FileOrganizer.exe"); File.WriteAllText(engine, "fixture"); vm.EnginePath = engine; window.DataContext = vm;
        try
        {
            Task run = vm.StartScanAsync(); await Eventually(() => launches == 1);
            await window.SwitchInterfaceAsync(); Assert.Same(vm, window.ActiveSurface.DataContext); Assert.True(vm.Busy);
            Button stop = Descendants<Button>(window).Single(b => ReferenceEquals(b.Command, vm.StopScanCommand)); await Eventually(() => stop.IsEnabled);
            ButtonAutomationPeer peer = new(stop); ((IInvokeProvider)peer.GetPattern(PatternInterface.Invoke)).Invoke();
            await run; Assert.False(vm.Busy); Assert.Equal(1, launches);
            await window.SwitchInterfaceAsync(); Assert.Same(vm, window.ActiveSurface.DataContext); Assert.Equal(f.Valid, File.ReadAllText(f.Config));
        }
        finally { window.DataContext = original; window.Close(); }
    });
    [Fact]
    public async Task FailedPreferenceSaveKeepsModernUsableAndDisplaysFailure() => await OnSta(async () =>
    {
        using TestFixture f = new(); string blocked = Path.Combine(f.Root, "blocked"); Directory.CreateDirectory(blocked);
        MainWindow window = TestWindow(f, new(blocked)); window.Show(); EditorViewModel vm = (EditorViewModel)window.DataContext;
        try
        {
            await Eventually(() => vm.Document is not null && !vm.Busy); await window.SwitchInterfaceAsync();
            Assert.Equal(EditorInterface.Modern, window.ActiveInterface); Assert.True(vm.CanEditForm);
            Assert.Contains(Descendants<TextBlock>(window), t => t.Text.Contains("preferenza non salvata"));
            Assert.Equal(f.Valid, File.ReadAllText(f.Config)); Assert.Empty(Directory.GetFiles(f.Root, ".interface-*.tmp"));
        }
        finally { window.Close(); }
    });
    [Fact]
    public async Task HistorySelectionAndPathDraftSurviveBothSwitches() => await OnSta(async () =>
    {
        using TestFixture f = new(); Configuration config = ConfigurationLoader.Load(f.Config); string input = Path.Combine(f.Root, "in", "history.txt"); File.WriteAllText(input, "data");
        MoveHistory history = f.History; await new SafeTransfer((_, _) => { }, history, f.Config).ExecuteAsync(config, config.Folders[0], config.Folders[0].Rules[0], input);
        MainWindow window = TestWindow(f, new(Path.Combine(f.Root, "interface.txt"))); window.Show(); EditorViewModel vm = (EditorViewModel)window.DataContext;
        try
        {
            await Eventually(() => vm.Document is not null && !vm.Busy); Navigate(window, EditorPage.History); await Eventually(() => !vm.Busy && vm.HistoryEntries.Count == 1);
            DataGrid grid = Descendants<DataGrid>(window).Single(); grid.SelectedItems.Add(vm.HistoryEntries[0]);
            string selected = Assert.Single(vm.SelectedHistoryIds);
            await window.SwitchInterfaceAsync(); Assert.Equal(selected, Assert.Single(vm.SelectedHistoryIds));
            Assert.Single(Descendants<DataGrid>(window).Single().SelectedItems);
            Navigate(window, EditorPage.Paths); await Eventually(() => !vm.Busy && vm.PathRoots.Count > 0);
            PathTreeNode root = vm.PathRoots[0]; await vm.CyclePathAsync(root, false);
            string draft = vm.Document!.Render(); await window.SwitchInterfaceAsync(); Assert.Equal(EditorPage.Paths, window.ActiveSurface.SelectedPage);
            Assert.Same(root, vm.PathRoots[0]); Assert.Equal(draft, vm.Document.Render());
            vm.SaveCommand.Execute(null); await Eventually(() => !vm.Busy && !vm.IsDirty);
        }
        finally { window.Close(); }
    });
}
