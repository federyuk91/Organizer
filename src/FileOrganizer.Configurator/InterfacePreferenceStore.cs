using System.IO;
using System.Text;
using FileOrganizer.Core;

namespace FileOrganizer.Configurator;

public enum EditorInterface { Classic, Modern }

public sealed class InterfacePreferenceStore(string path)
{
    public static string DefaultPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FileOrganizer", "interface.txt");
    public string FilePath { get; } = Path.GetFullPath(path);
    public EditorInterface Read(out string? warning)
    {
        warning = null;
        try
        {
            Paths.NoReparse(FilePath);
            if (!File.Exists(FilePath)) return EditorInterface.Classic;
            if (new FileInfo(FilePath).Length > 64) throw new IOException("Preference file exceeds the supported size.");
            string value = File.ReadAllText(FilePath).Trim();
            if (value == nameof(EditorInterface.Modern)) return EditorInterface.Modern;
            if (value == nameof(EditorInterface.Classic)) return EditorInterface.Classic;
            warning = "Preferenza interfaccia non valida; utilizzata Classic.";
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        { warning = "Preferenza interfaccia non leggibile: " + exception.Message; }
        return EditorInterface.Classic;
    }
    public void Save(EditorInterface mode)
    {
        if (!Enum.IsDefined(mode)) throw new ArgumentOutOfRangeException(nameof(mode));
        Paths.NoReparse(FilePath);
        string directory = Path.GetDirectoryName(FilePath)!;
        Directory.CreateDirectory(directory);
        string temporary = Path.Combine(directory, ".interface-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (FileStream output = new(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { output.Write(Encoding.UTF8.GetBytes(mode.ToString())); output.Flush(true); }
            Paths.NoReparse(FilePath);
            File.Move(temporary, FilePath, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
