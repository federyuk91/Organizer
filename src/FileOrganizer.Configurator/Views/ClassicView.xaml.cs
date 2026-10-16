using System.Windows.Controls;

namespace FileOrganizer.Configurator.Views;

public partial class ClassicView : EditorSurface
{
    public ClassicView() => InitializeComponent();
    protected override TabControl Navigation => MainTabs;
    protected override TextBox RawEditor => TomlEditor;
    protected override DataGrid MovementGrid => HistoryGrid;
}
