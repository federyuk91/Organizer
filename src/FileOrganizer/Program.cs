using FileOrganizer.Core;
using FileOrganizer.Windows;

namespace FileOrganizer;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        bool background = false;
        string configPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FileOrganizer", "config.toml");
        try
        {
            string command = "run";
            bool commandSeen = false;
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] is "--help" or "-h") { Help(); return 0; }
                if (args[i] == "--config" && i + 1 < args.Length) configPath = args[++i];
                else if (args[i] == "--background") background = true;
                else if (!commandSeen && args[i] is "run" or "scan" or "validate" or "install" or "uninstall" or "stop") { command = args[i]; commandSeen = true; }
                else throw new ArgumentException("Unknown/incomplete argument: " + args[i]);
            }
            if (background && command != "run") throw new ArgumentException("--background is supported only with run");
            if (background) BackgroundConsole.Detach();
            configPath = Paths.NormalizeLiteral(Path.GetFullPath(configPath));
            if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("FileOrganizer requires Windows 10/11 x64.");
            if (command == "stop") { Console.WriteLine(InstanceLease.RequestStop() ? "Stop requested; the worker will finish cleanup safely." : "No worker is running."); return 0; }
            if (command == "uninstall") { StartupRegistration.Uninstall(new RegistryStartupStore(), configPath); return 0; }
            if (!File.Exists(configPath)) throw new ConfigException("Config not found: " + configPath + ". Copy Config.Example.toml here and edit source paths first.");
            string configText = ConfigurationFile.ReadSharedText(configPath);
            Configuration config = ConfigurationLoader.Parse(configText);
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
            using CancellationTokenRegistration requestedStop = instanceLock.StopRequested.Register(stop.Cancel);
            Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Cancel(); };
            MoveHistory history = new(MoveHistory.DefaultDirectory);
            EngineStateStore states = new(data);
            EngineSnapshot snapshot = states.Create(configPath, configText, config);
            bool reportState = !config.General.DryRun;
            if (reportState) states.Write(snapshot);
            try
            {
                WorkerHost? activeHost = null;
                await using WorkerHost host = new(config, configPath, log, history,
                    (text, next) =>
                    {
                        if (reportState && next.General.DryRun) states.Clear(snapshot);
                        reportState = !next.General.DryRun; snapshot = states.Create(configPath, text, next, activeHost?.Managed);
                        if (reportState) states.Write(snapshot);
                    },
                    error => { snapshot = snapshot with { ReloadError = error }; if (reportState) states.Write(snapshot); });
                activeHost = host;
                if (command == "scan") return await host.ScanAsync(stop.Token);
                try { await host.RunAsync(stop.Token); }
                catch (OperationCanceledException) when (host.HistoryFailed) { return 3; }
                return host.HistoryFailed ? 3 : 0;
            }
            finally { if (reportState) states.Clear(snapshot); }
        }
        catch (OperationCanceledException) { return 130; }
        catch (Exception exception) when (exception is ConfigException or IOException or UnauthorizedAccessException or ArgumentException or PlatformNotSupportedException or InvalidOperationException)
        {
            if (background)
            {
                string directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FileOrganizer", "logs", "startup");
                new RotatingLog(directory, LogLevel.Error).Write(LogLevel.Error, exception.Message);
            }
            else Console.Error.WriteLine(exception.Message);
            return exception is ConfigException or ArgumentException ? 2 : 3;
        }
    }
    private static void Help() => Console.WriteLine("""
        FileOrganizer [run|scan|validate|install|uninstall|stop] [--config <path>] [--background]
        run        Watch continuously (default); Ctrl+C stops safely.
        scan       Process one snapshot and exit.
        validate   Validate without modifying files.
        install    Register this executable at per-user login (HKCU Run).
        uninstall  Remove only this config's startup entry.
        stop       Request safe shutdown of this user's worker, regardless of config.
        --background Detach the console for run; normal diagnostics remain in rotating logs.
        --help     Show help.
        Default config: %LOCALAPPDATA%\FileOrganizer\config.toml
        Exit codes: 0 success, 2 configuration/usage, 3 runtime failure, 130 cancelled.
        """);
}
