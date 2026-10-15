using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;

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
    }
    private void OnHistorySelectionChanged(object sender, SelectionChangedEventArgs e)
    { if (sender is DataGrid grid) editor.SelectHistory(grid.SelectedItems.Cast<FileOrganizer.Windows.MoveRecord>()); }
}
