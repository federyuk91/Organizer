using System.Windows;

namespace FileOrganizer.Configurator;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        string? config = null;
        for (int i = 0; i < e.Args.Length; i++)
        {
            if (e.Args[i] == "--config" && i + 1 < e.Args.Length) config = e.Args[++i];
            else { MessageBox.Show("Usage: FileOrganizer.Configurator.exe [--config <path>]", "File Organizer"); Shutdown(2); return; }
        }
        MainWindow window = new(config); MainWindow = window; window.Show();
    }
}
