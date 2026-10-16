using System.Windows;
using System.Windows.Controls;

namespace FileOrganizer.Configurator.Views;

public partial class ModernView : EditorSurface
{
    public ModernView()
    {
        InitializeComponent();
        if (SystemParameters.HighContrast)
        {
            Resources["CanvasBrush"] = SystemColors.ControlBrush; Resources["SurfaceBrush"] = SystemColors.WindowBrush;
            Resources["TextBrush"] = SystemColors.WindowTextBrush; Resources["MutedBrush"] = SystemColors.WindowTextBrush;
            Resources["LineBrush"] = SystemColors.WindowTextBrush; Resources["AccentBrush"] = SystemColors.HighlightBrush;
            Resources["HoverBrush"] = SystemColors.ControlBrush; Resources["AccentTextBrush"] = SystemColors.HighlightTextBrush;
            Resources["SuccessBrush"] = SystemColors.WindowTextBrush; Resources["DisabledIconBrush"] = SystemColors.GrayTextBrush;
            Resources["ErrorBrush"] = SystemColors.WindowTextBrush;
        }
    }
    protected override TabControl Navigation => MainTabs;
    protected override TextBox RawEditor => TomlEditor;
    protected override DataGrid MovementGrid => HistoryGrid;
    private async void OnManageSubfoldersClick(object sender, RoutedEventArgs args)
    {
        if (sender is not CheckBox toggle || Editor is null) return;
        try { await Editor.ChangeManagedSubfoldersAsync(toggle.IsChecked == true); }
        finally { toggle.GetBindingExpression(CheckBox.IsCheckedProperty)?.UpdateTarget(); }
    }
}
