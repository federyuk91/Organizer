using System.Globalization;
using System.Text;

namespace FileOrganizer.Core;

public sealed class RotatingLog(string directory, LogLevel level = LogLevel.Warning, long maxBytes = 1024 * 1024, int archives = 3)
{
    private readonly object gate = new();
    public string DirectoryPath { get; } = Path.GetFullPath(directory);
    public LogLevel Level { get; set; } = level;
    public bool ConsoleOnly { get; set; }
    public void Write(LogLevel severity, string message)
    {
        if (severity > Level && !(ConsoleOnly && severity == LogLevel.Info)) return;
        string line = $"{DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture)} {severity}: {message.Replace('\r', ' ').Replace('\n', ' ')}";
        if (line.Length > 8192) line = line[..8192];
        if (ConsoleOnly) { Console.Error.WriteLine(line); return; }
        lock (gate)
        {
            try
            {
                Directory.CreateDirectory(directory);
                string current = Path.Combine(directory, "organizer.log");
                if (File.Exists(current) && new FileInfo(current).Length + Encoding.UTF8.GetByteCount(line) + 2 > maxBytes)
                {
                    for (int i = archives; i >= 1; i--)
                    {
                        string old = i == 1 ? current : current + "." + (i - 1);
                        string next = current + "." + i;
                        if (File.Exists(old)) File.Move(old, next, true);
                    }
                }
                File.AppendAllText(current, line + Environment.NewLine);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            { Console.Error.WriteLine("Log unavailable: " + exception.Message); }
        }
        if (severity <= LogLevel.Warning) Console.Error.WriteLine(line);
    }
}
