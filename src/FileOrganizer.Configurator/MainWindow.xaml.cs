using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Media;
using FileOrganizer.Configurator.Views;

namespace FileOrganizer.Configurator;

public partial class MainWindow : Window
{
    private readonly EditorViewModel editor;
    private readonly string? initialPath;
    private readonly InterfacePreferenceStore preferences;
    private readonly Dictionary<EditorInterface, EditorSurface> surfaces = [];
    private bool switching, initialized;
    public EditorInterface ActiveInterface { get; private set; }
    public EditorSurface ActiveSurface => (EditorSurface)SurfaceHost.Content;
    public MainWindow(string? configPath = null, FileOrganizer.Windows.MoveHistory? history = null,
        FileOrganizer.Windows.EngineStateStore? states = null, InterfacePreferenceStore? preferences = null)
    {
        InitializeComponent(); initialPath = configPath;
        this.preferences = preferences ?? new InterfacePreferenceStore(InterfacePreferenceStore.DefaultPath);
        ActiveInterface = this.preferences.Read(out string? warning); PreferenceWarning.Text = warning ?? "";
        editor = new EditorViewModel(this, history: history, states: states); DataContext = editor;
        EditorSurface surface = GetSurface(ActiveInterface); SurfaceHost.Content = surface; surface.InitializeModel(editor);
        surface.ClearValue(DataContextProperty);
        UpdateInterfaceLabel();
    }
    private EditorSurface GetSurface(EditorInterface mode)
    {
        if (!surfaces.TryGetValue(mode, out EditorSurface? surface))
        { surface = mode == EditorInterface.Modern ? new ModernView() : new ClassicView(); surfaces.Add(mode, surface); }
        return surface;
    }
    private void UpdateInterfaceLabel()
    {
        InterfaceLabel.Text = ActiveInterface.ToString();
        InterfaceIcon.Source = (ImageSource)FindResource(ActiveInterface == EditorInterface.Classic ? "ClassicInterfaceIcon" : "ModernInterfaceIcon");
        string tooltip = ActiveInterface == EditorInterface.Classic ? "Interfaccia Classic — passa a Modern" : "Interfaccia Modern — passa a Classic";
        InterfaceSwitch.ToolTip = tooltip;
        AutomationProperties.SetName(InterfaceSwitch, tooltip);
    }
    private async void OnLoaded(object sender, RoutedEventArgs e)
    { if (!initialized) { initialized = true; await editor.InitializeAsync(initialPath); } }
    private async void OnInterfaceSwitch(object sender, RoutedEventArgs e) => await SwitchInterfaceAsync();
    public async Task SwitchInterfaceAsync()
    {
        if (switching) return;
        switching = true; InterfaceSwitch.IsEnabled = false;
        try
        {
            EditorViewModel model = (EditorViewModel)DataContext;
            EditorSurface previous = ActiveSurface;
            EditorPage page = previous.SelectedPage;
            (int caret, int length) = previous.CaptureRawSelection();
            FileOrganizer.Core.ConfigurationDocument? document = model.Document;
            FileOrganizer.Core.ConfigurationSection? folder = model.SelectedFolder, rule = model.SelectedRule;
            previous.DetachModel();
            ActiveInterface = ActiveInterface == EditorInterface.Classic ? EditorInterface.Modern : EditorInterface.Classic;
            EditorSurface next = GetSurface(ActiveInterface); SurfaceHost.Content = next;
            await next.AttachModelAsync(model, page, caret, length);
            if (ReferenceEquals(document, model.Document)) { model.SelectedFolder = folder; model.SelectedRule = rule; }
            UpdateInterfaceLabel();
            EditorInterface selected = ActiveInterface;
            try { await Task.Run(() => preferences.Save(selected)); PreferenceWarning.Text = ""; }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            { PreferenceWarning.Text = "Interfaccia cambiata; preferenza non salvata: " + exception.Message; }
        }
        finally { switching = false; InterfaceSwitch.IsEnabled = true; }
    }
    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (switching || editor.Busy || !editor.ConfirmDiscard()) { e.Cancel = true; return; }
        editor.Dispose();
    }
}
