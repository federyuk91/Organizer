using System.Windows;
using System.Windows.Controls;
using System.ComponentModel;

namespace FileOrganizer.Configurator.Views;

public partial class ModernView : EditorSurface
{
    public ModernView()
    {
        InitializeComponent();
        DataContextChanged += OnHistoryContextChanged;
        if (SystemParameters.HighContrast)
        {
            Resources["CanvasBrush"] = SystemColors.ControlBrush; Resources["SurfaceBrush"] = SystemColors.WindowBrush;
            Resources["TextBrush"] = SystemColors.WindowTextBrush; Resources["MutedBrush"] = SystemColors.WindowTextBrush;
            Resources["LineBrush"] = SystemColors.WindowTextBrush; Resources["AccentBrush"] = SystemColors.HighlightBrush;
            Resources["HoverBrush"] = SystemColors.ControlBrush; Resources["AccentTextBrush"] = SystemColors.HighlightTextBrush;
            Resources["SuccessBrush"] = SystemColors.WindowTextBrush; Resources["DisabledIconBrush"] = SystemColors.GrayTextBrush;
            Resources["ErrorBrush"] = SystemColors.WindowTextBrush;
            foreach (string key in new[] { "HistoryNeutralBackground", "HistoryMovedBackground", "HistoryRestoredBackground", "HistoryWarningBackground" }) Resources[key] = SystemColors.WindowBrush;
            foreach (string key in new[] { "HistoryMovedText", "HistoryRestoredText", "HistoryWarningText" }) Resources[key] = SystemColors.WindowTextBrush;
        }
    }
    protected override TabControl Navigation => MainTabs;
    protected override TextBox RawEditor => TomlEditor;
    protected override DataGrid MovementGrid => HistoryGrid;
    private void OnHistoryContextChanged(object sender, DependencyPropertyChangedEventArgs args)
    {
        if (args.OldValue is EditorViewModel previous) previous.PropertyChanged -= OnHistoryModelChanged;
        if (args.NewValue is EditorViewModel current) current.PropertyChanged += OnHistoryModelChanged;
        UpdateHistoryColumns();
    }
    private void OnHistoryModelChanged(object? sender, PropertyChangedEventArgs args)
    { if (args.PropertyName == nameof(EditorViewModel.ShowAllHistory)) UpdateHistoryColumns(); }
    private void UpdateHistoryColumns() => HistoryConfigColumn.Visibility = Editor?.ShowAllHistory == true ? Visibility.Visible : Visibility.Collapsed;
    private async void OnManageSubfoldersClick(object sender, RoutedEventArgs args)
    {
        if (sender is not CheckBox toggle || Editor is null) return;
        try { await Editor.ChangeManagedSubfoldersAsync(toggle.IsChecked == true); }
        finally { toggle.GetBindingExpression(CheckBox.IsCheckedProperty)?.UpdateTarget(); }
    }
}
