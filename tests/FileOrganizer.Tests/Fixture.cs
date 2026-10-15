using FileOrganizer.Core;
using FileOrganizer.Windows;

namespace FileOrganizer.Tests;

public sealed class Fixture : IDisposable
{
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "FileOrganizer.Tests", Guid.NewGuid().ToString("N"));
    public string Source => Path.Combine(Root, "in");
    public string Destination => Path.Combine(Root, "out");
    public string ConfigPath => Path.Combine(Root, "config.toml");
    public Fixture() { Directory.CreateDirectory(Source); }
    public string Text(string duplicates = "", string general = "", string rule = "", bool recursive = false) => $$"""
        version = 1
        [general]
        file_ready_timeout_seconds = 2
        {{general}}
        [[folders]]
        name = "test"
        path = '{{Source}}'
        recursive = {{recursive.ToString().ToLowerInvariant()}}
        [folders.duplicates]
        {{duplicates}}
        [[folders.rules]]
        name = "text"
        destination = '{{Destination}}'
        patterns = ["*.txt"]
        {{rule}}
        """;
    public Configuration Config(string duplicates = "", string general = "", string rule = "", bool recursive = false) =>
        ConfigurationLoader.Parse(Text(duplicates, general, rule, recursive));
    public string File(string name = "hello.txt", string contents = "data")
    { string path = Path.Combine(Source, name); System.IO.File.WriteAllText(path, contents); return path; }
    public Task<TransferOutcome> Move(Configuration config, string source, bool forceCopy = false, Action? hook = null) =>
        new SafeTransfer((_, _) => { }).ExecuteAsync(config, config.Folders[0], config.Folders[0].Rules[0], source, default, forceCopy, hook);
    public RotatingLog Log() => new(Path.Combine(Root, "logs"), LogLevel.Error);
    public void Dispose() { if (Directory.Exists(Root)) Directory.Delete(Root, true); }
    public static async Task Eventually(Func<bool> condition, int milliseconds = 10000)
    {
        using CancellationTokenSource timeout = new(milliseconds);
        while (!condition()) await Task.Delay(25, timeout.Token);
    }
}
