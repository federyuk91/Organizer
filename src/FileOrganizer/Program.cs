using FileOrganizer.Core;
using FileOrganizer.Windows;

namespace FileOrganizer;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        try
        {
            string command = "run";
            bool commandSeen = false;
            string configPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FileOrganizer", "config.toml");
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] is "--help" or "-h") { Help(); return 0; }
                if (args[i] == "--config" && i + 1 < args.Length) configPath = args[++i];
                else if (!commandSeen && args[i] is "run" or "scan" or "validate" or "install" or "uninstall") { command = args[i]; commandSeen = true; }
                else throw new ArgumentException("Unknown/incomplete argument: " + args[i]);
            }
            configPath = Paths.Normalize(Path.GetFullPath(configPath));
            if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("FileOrganizer requires Windows 10/11 x64.");
            if (command == "uninstall") { StartupRegistration.Uninstall(new RegistryStartupStore(), configPath); return 0; }
            if (!File.Exists(configPath)) throw new ConfigException("Config not found: " + configPath + ". Copy Config.Example.toml here and edit source paths first.");
            Configuration config = ConfigurationLoader.Load(configPath);
            if (command == "validate") { Console.WriteLine("Configuration valid."); return 0; }
            if (command == "install")
            {
                StartupRegistration.Install(new RegistryStartupStore(), Environment.ProcessPath!, configPath);
                Console.WriteLine("Per-user login startup installed."); return 0;
            }
            string data = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FileOrganizer");
            using InstanceLease instanceLock = new(configPath);
            RotatingLog log = new(Path.Combine(data, "logs", StartupRegistration.Identity(configPath)), config.General.LogLevel)
            { ConsoleOnly = config.General.DryRun };
            using CancellationTokenSource stop = new();
            Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Cancel(); };
            await using WorkerHost host = new(config, configPath, log);
            if (command == "scan") return await host.ScanAsync(stop.Token);
            await host.RunAsync(stop.Token);
            return 0;
        }
        catch (OperationCanceledException) { return 130; }
        catch (Exception exception) when (exception is ConfigException or IOException or UnauthorizedAccessException or ArgumentException or PlatformNotSupportedException or InvalidOperationException)
        { Console.Error.WriteLine(exception.Message); return exception is ConfigException or ArgumentException ? 2 : 3; }
    }
    private static void Help() => Console.WriteLine("""
        FileOrganizer [run|scan|validate|install|uninstall] [--config <path>]
        run        Watch continuously (default); Ctrl+C stops safely.
        scan       Process one snapshot and exit.
        validate   Validate without modifying files.
        install    Register this executable at per-user login (HKCU Run).
        uninstall  Remove only this config's startup entry.
        --help     Show help.
        Default config: %LOCALAPPDATA%\FileOrganizer\config.toml
        Exit codes: 0 success, 2 configuration/usage, 3 runtime failure, 130 cancelled.
        """);
}
