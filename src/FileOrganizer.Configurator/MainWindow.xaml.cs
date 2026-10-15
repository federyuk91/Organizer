using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace FileOrganizer.Configurator;

public partial class MainWindow : Window
{
    private readonly EditorViewModel editor;
    private readonly string? initialPath;
    public MainWindow(string? configPath = null, FileOrganizer.Windows.MoveHistory? history = null, FileOrganizer.Windows.EngineStateStore? states = null)
    {
        InitializeComponent(); initialPath = configPath;
        editor = new EditorViewModel(this, history: history, states: states); DataContext = editor;
    }
    private async void OnLoaded(object sender, RoutedEventArgs e) => await editor.InitializeAsync(initialPath);
    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (editor.Busy || !editor.ConfirmDiscard()) { e.Cancel = true; return; }
        editor.Dispose();
    }
    private async void OnStartupClick(object sender, RoutedEventArgs e) => await editor.ChangeStartupAsync(StartupToggle.IsChecked == true);
    private void OnTabChanged(object sender, SelectionChangedEventArgs e)
    {
        if (e.OriginalSource == sender && sender is TabControl tabs && tabs.SelectedItem is TabItem { Header: "TOML" } && DataContext is EditorViewModel model)
            model.PrepareRaw();
        if (e.OriginalSource == sender && sender is TabControl historyTabs && historyTabs.SelectedItem is TabItem { Header: "History" } && editor.RefreshHistoryCommand.CanExecute(null))
            editor.RefreshHistoryCommand.Execute(null);
        if (e.OriginalSource == sender && sender is TabControl pathTabs && pathTabs.SelectedItem is TabItem { Header: "Managed paths" } && editor.LoadPathsCommand.CanExecute(null))
            editor.LoadPathsCommand.Execute(null);
    }
    private async void OnPathExpanded(object sender, RoutedEventArgs e)
    { if (e.OriginalSource is TreeViewItem { DataContext: PathTreeNode node }) await node.LoadChildrenAsync(); }
    private async void OnPathLeftClick(object sender, RoutedEventArgs e) => await CyclePathAsync(sender, false);
    private async void OnPathRightClick(object sender, MouseButtonEventArgs e)
    { e.Handled = true; await CyclePathAsync(sender, true); }
    private async Task CyclePathAsync(object sender, bool reverse)
    {
        try { if (sender is FrameworkElement { DataContext: PathTreeNode node }) await editor.CyclePathAsync(node, reverse); }
        catch (Exception exception) when (exception is FileOrganizer.Core.ConfigException or System.IO.IOException or UnauthorizedAccessException)
        { MessageBox.Show(this, exception.Message, "Path selection", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }
    private void OnHistorySelectionChanged(object sender, SelectionChangedEventArgs e)
    { if (sender is DataGrid grid) editor.SelectHistory(grid.SelectedItems.Cast<FileOrganizer.Windows.MoveRecord>()); }
}
