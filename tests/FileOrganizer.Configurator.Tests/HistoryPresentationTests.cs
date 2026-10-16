using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using FileOrganizer.Configurator;
using FileOrganizer.Windows;
using Xunit;

namespace FileOrganizer.Configurator.Tests;

public sealed partial class WindowTests
{
    private static MoveRecord[] WritePresentationHistory(TestFixture f)
    {
        DateTimeOffset today = new(DateTime.Today.AddHours(1));
        if (today > DateTimeOffset.Now) today = DateTimeOffset.Now.AddSeconds(-1);
        string source = Path.Combine(f.Root, "in"), destination = Path.Combine(source, "sorted");
        MoveRecord Entry(string name, string state, DateTimeOffset time, long bytes, string? config = null, string? folder = null) =>
            new(1, Guid.NewGuid().ToString("N"), time, config ?? f.Config, Path.Combine(folder ?? source, name), Path.Combine(destination, name), bytes, new string('A', 64), new(1, 0, 1, 0), state);
        MoveRecord[] entries = [Entry("Gattino.png", "Committed", today.AddDays(-1), 2048), Entry("Rapporto.pdf", "Undone", today, 1536),
            Entry("Progetto.zip", "Prepared", today.AddDays(-2), 42, folder: Path.Combine(source, "projects")), Entry("Foto.jpg", "UndoPrepared", today.AddMinutes(-1), 1048576),
            Entry("Altra-config.txt", "Committed", today.AddMinutes(-2), 100, Path.Combine(f.Root, "other.toml"))];
        Directory.CreateDirectory(f.History.DirectoryPath);
        File.WriteAllLines(Path.Combine(f.History.DirectoryPath, "history-presentation.jsonl"), entries.Select(e => JsonSerializer.Serialize(e)));
        return entries;
    }

    [Fact]
    public async Task HistoryFiltersCombineAndNeverRetainHiddenUndoSelections() => await OnSta(async () =>
    {
        using TestFixture f = new(); MoveRecord[] entries = WritePresentationHistory(f);
        using EditorViewModel vm = new(new Window(), new FakeStore(), f.History, f.States);
        await vm.InitializeAsync(f.Config); await vm.RefreshHistoryAsync();
        Assert.Equal(4, vm.HistoryView.Cast<MoveRecord>().Count());
        Assert.Equal(entries[1].Id, vm.HistoryView.Cast<MoveRecord>().First().Id);
        vm.HistorySearch = "gAtTiNo"; Assert.Equal(entries[0].Id, Assert.Single(vm.HistoryView.Cast<MoveRecord>()).Id);
        vm.SelectHistory([entries[0]]); Assert.True(vm.UndoHistoryCommand.CanExecute(null));
        vm.HistoryState = "Ripristinato"; Assert.Empty(vm.HistoryView.Cast<MoveRecord>());
        Assert.Empty(vm.SelectedHistoryIds); Assert.Null(vm.SelectedHistoryEntry); Assert.False(vm.UndoHistoryCommand.CanExecute(null));
        vm.HistorySearch = ""; Assert.Equal(entries[1].Id, Assert.Single(vm.HistoryView.Cast<MoveRecord>()).Id);
        vm.HistoryState = "Tutti gli stati"; vm.HistoryPeriod = "Ieri";
        Assert.Equal(entries[0].Id, Assert.Single(vm.HistoryView.Cast<MoveRecord>()).Id);
        vm.HistoryPeriod = "Ultimi 7 giorni"; vm.HistoryFolder = Path.Combine(f.Root, "in", "projects");
        Assert.Equal(entries[2].Id, Assert.Single(vm.HistoryView.Cast<MoveRecord>()).Id);
        vm.HistoryFolder = "Tutte le cartelle";
        vm.HistoryView.SortDescriptions.Clear(); vm.HistoryView.SortDescriptions.Add(new(nameof(MoveRecord.Size), ListSortDirection.Ascending));
        Assert.Equal(42, vm.HistoryView.Cast<MoveRecord>().First().Size);
        vm.ShowAllHistory = true; await Eventually(() => !vm.Busy);
        Assert.Equal(5, vm.HistoryView.Cast<MoveRecord>().Count());
        Assert.False(File.Exists(entries[0].Source)); Assert.False(File.Exists(entries[0].Destination));
    });

    [Theory]
    [InlineData(0, "0 B")]
    [InlineData(1024, "1 KB")]
    [InlineData(1536, "1,5 KB")]
    [InlineData(1048576, "1 MB")]
    public void HistoryPresentationUsesItalianReadableValues(long bytes, string expected)
    {
        Assert.Equal(expected, HistoryDisplayConverter.SizeLabel(bytes));
        Assert.Equal("! Da verificare", HistoryDisplayConverter.StateLabel("Prepared"));
        Assert.Equal("↶ Ripristinato", HistoryDisplayConverter.StateLabel("Undone"));
        string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        Assert.Equal(Path.Combine("Downloads", "Images"), HistoryDisplayConverter.ShortFolder(Path.Combine(profile, "Downloads", "Images")));
        HistoryDisplayConverter converter = new(); DateTimeOffset time = new(2026, 10, 16, 12, 57, 0, TimeSpan.Zero);
        Assert.Equal(time.ToLocalTime().ToString("dd/MM/yyyy HH:mm", CultureInfo.GetCultureInfo("it-IT")), converter.Convert(time, typeof(string), "Time", CultureInfo.InvariantCulture));
    }

    [Fact]
    public async Task ModernHistoryShowsColoredRowsDetailsAndConditionalTomlColumn() => await OnSta(async () =>
    {
        using TestFixture f = new(); WritePresentationHistory(f);
        InterfacePreferenceStore store = new(Path.Combine(f.Root, "interface.txt")); store.Save(EditorInterface.Modern);
        MainWindow window = TestWindow(f, store); window.Width = 1420; window.Show(); EditorViewModel vm = (EditorViewModel)window.DataContext;
        try
        {
            await Eventually(() => vm.Document is not null && !vm.Busy);
            Navigate(window, EditorPage.History); await Eventually(() => !vm.Busy);
            await window.Dispatcher.InvokeAsync(window.UpdateLayout, DispatcherPriority.ApplicationIdle);
            DataGrid grid = Descendants<DataGrid>(window).Single();
            Assert.Equal(40, grid.RowHeight); Assert.Equal(Visibility.Collapsed, grid.Columns.Single(c => Equals(c.Header, "TOML")).Visibility);
            grid.SelectedItems.Add(grid.Items[0]);
            await Eventually(() => vm.HasHistorySelection); Assert.NotNull(vm.SelectedHistoryEntry);
            Assert.True(vm.CopyHistorySourceCommand.CanExecute(null)); Assert.True(vm.OpenHistoryDestinationCommand.CanExecute(null));
            Expander details = (Expander)window.ActiveSurface.FindName("HistoryDetails");
            Assert.False(details.IsExpanded); details.IsExpanded = true;
            await window.Dispatcher.InvokeAsync(window.UpdateLayout, DispatcherPriority.ApplicationIdle);
            Assert.Contains(Descendants<TextBox>(window), box => box.IsReadOnly && box.Text == vm.SelectedHistoryEntry.Source);
            details.IsExpanded = false;
            string selected = Assert.Single(vm.SelectedHistoryIds);
            await window.SwitchInterfaceAsync(); await window.SwitchInterfaceAsync();
            Assert.Equal(selected, Assert.Single(vm.SelectedHistoryIds)); Assert.NotNull(vm.SelectedHistoryEntry);
            vm.ShowAllHistory = true; await Eventually(() => !vm.Busy);
            grid = Descendants<DataGrid>(window).Single(); Assert.Equal(Visibility.Visible, grid.Columns.Single(c => Equals(c.Header, "TOML")).Visibility);
            vm.ShowAllHistory = false; await Eventually(() => !vm.Busy);
            await window.Dispatcher.InvokeAsync(window.UpdateLayout, DispatcherPriority.ApplicationIdle);
            grid.SelectedItems.Add(grid.Items[0]);
            await Eventually(() => vm.HasHistorySelection);
            await window.Dispatcher.InvokeAsync(window.UpdateLayout, DispatcherPriority.ApplicationIdle);
            AssertNoBindingErrors(window);
            string output = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../artifacts/gui-preview")); Directory.CreateDirectory(output);
            FrameworkElement content = (FrameworkElement)window.Content;
            RenderTargetBitmap bitmap = new((int)content.ActualWidth, (int)content.ActualHeight, 96, 96, PixelFormats.Pbgra32); bitmap.Render(content);
            PngBitmapEncoder encoder = new(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using FileStream image = File.Create(Path.Combine(output, "modern-history-populated.png")); encoder.Save(image);
        }
        finally { window.Close(); }
    });
}
