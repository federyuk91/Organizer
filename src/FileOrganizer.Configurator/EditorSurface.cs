using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using FileOrganizer.Windows;

namespace FileOrganizer.Configurator;

public enum EditorPage { Folders, General, Toml, Paths, History }

public abstract class EditorSurface : UserControl
{
    protected abstract TabControl Navigation { get; }
    protected abstract TextBox RawEditor { get; }
    protected abstract DataGrid MovementGrid { get; }
    private bool restoring = true;
    protected EditorViewModel? Editor => DataContext as EditorViewModel;
    public EditorPage SelectedPage => Navigation.SelectedItem is TabItem { Tag: string tag } && Enum.TryParse(tag, out EditorPage page) ? page : EditorPage.Folders;
    public void InitializeModel(EditorViewModel editor) { DataContext = editor; restoring = false; }
    public void DetachModel() { restoring = true; DataContext = null; }
    public async Task AttachModelAsync(EditorViewModel editor, EditorPage page, int caret, int selectionLength)
    {
        restoring = true;
        DataContext = editor;
        Navigation.SelectedItem = Navigation.Items.Cast<TabItem>().First(t => Equals(t.Tag, page.ToString()));
        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Loaded);
        RawEditor.Select(Math.Min(caret, RawEditor.Text.Length), Math.Min(selectionLength, Math.Max(0, RawEditor.Text.Length - caret)));
        foreach (MoveRecord entry in editor.HistoryEntries.Where(e => editor.SelectedHistoryIds.Contains(e.Id))) MovementGrid.SelectedItems.Add(entry);
        restoring = false;
    }
    public (int Caret, int Length) CaptureRawSelection() => (RawEditor.SelectionStart, RawEditor.SelectionLength);
    protected async void OnStartupClick(object sender, RoutedEventArgs e)
    { if (Editor is not null && sender is CheckBox toggle) await Editor.ChangeStartupAsync(toggle.IsChecked == true); }
    protected void OnTabChanged(object sender, SelectionChangedEventArgs e)
    {
        if (restoring || e.OriginalSource != sender || Editor is not EditorViewModel editor || editor.Busy) return;
        if (SelectedPage == EditorPage.Toml) editor.PrepareRaw();
        if (SelectedPage == EditorPage.History && editor.RefreshHistoryCommand.CanExecute(null)) editor.RefreshHistoryCommand.Execute(null);
        if (SelectedPage == EditorPage.Paths && editor.LoadPathsCommand.CanExecute(null)) editor.LoadPathsCommand.Execute(null);
    }
    protected async void OnPathExpanded(object sender, RoutedEventArgs e)
    { if (e.OriginalSource is TreeViewItem { DataContext: PathTreeNode node }) await node.LoadChildrenAsync(); }
    protected async void OnPathLeftClick(object sender, RoutedEventArgs e) => await CyclePathAsync(sender, false);
    protected async void OnPathRightClick(object sender, MouseButtonEventArgs e)
    { e.Handled = true; await CyclePathAsync(sender, true); }
    private async Task CyclePathAsync(object sender, bool reverse)
    {
        try { if (sender is FrameworkElement { DataContext: PathTreeNode node } && Editor is not null) await Editor.CyclePathAsync(node, reverse); }
        catch (Exception exception) when (exception is FileOrganizer.Core.ConfigException or System.IO.IOException or UnauthorizedAccessException)
        { MessageBox.Show(Window.GetWindow(this), exception.Message, "Path selection", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }
    protected void OnHistorySelectionChanged(object sender, SelectionChangedEventArgs e)
    { if (!restoring && Editor is not null && sender is DataGrid grid) Editor.SelectHistory(grid.SelectedItems.Cast<MoveRecord>()); }
}
