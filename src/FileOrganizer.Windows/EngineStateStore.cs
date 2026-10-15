using System.Collections.Immutable;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FileOrganizer.Core;
using Microsoft.Win32.SafeHandles;

namespace FileOrganizer.Windows;

public sealed record RuntimeFolder(string Name, string Path, bool Enabled, bool Recursive,
    string[]? Destinations = null, PathOverride[]? PathOverrides = null);
public sealed record EngineSnapshot(int Pid, long StartedUtcTicks, string Executable, string ConfigPath, string ConfigHash,
    RuntimeFolder[] Folders, string? ReloadError = null, int Version = 1);

public sealed class EngineStateStore(string directory)
{
    public static string DefaultDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FileOrganizer");
    public string FilePath { get; } = Path.Combine(Path.GetFullPath(directory), "engine-state.json");
    public static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    public EngineSnapshot Create(string configPath, string text, Configuration config, ManagedDirectories? managed = null)
    {
        using Process process = Process.GetCurrentProcess();
        return new(process.Id, process.StartTime.ToUniversalTime().Ticks, Environment.ProcessPath ?? "", configPath, Hash(text),
            config.Folders.Select(f => new RuntimeFolder(f.Name, f.Path, f.Enabled, f.Recursive,
                (managed?.Folders.FirstOrDefault(m => ReferenceEquals(m.Folder, f))?.Destinations ??
                    f.Rules.Where(r => r.Enabled).Select(r => r.Destination).ToImmutableArray()).ToArray(),
                (f.PathOverrides.IsDefault ? [] : f.PathOverrides).ToArray())).ToArray(), Version: 3);
    }
    public void Write(EngineSnapshot snapshot)
    {
        using DirectoryGuard guard = new(); guard.Hold(Path.GetDirectoryName(FilePath)!, true);
        Paths.NoReparse(FilePath);
        string temp = FilePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        using SafeFileHandle handle = NativeFiles.CreateNew(temp);
        using FileStream stream = new(handle, FileAccess.ReadWrite);
        bool owned = true;
        try
        {
            byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(snapshot, OperationJson.Default.EngineSnapshot);
            stream.Write(bytes); stream.Flush(true);
            for (int attempt = 0; ; attempt++)
            {
                try { Paths.NoReparse(FilePath); NativeFiles.Rename(handle, FilePath, true); break; }
                catch (IOException exception) when ((exception.HResult & 0xffff) is 5 or 32 or 33 && attempt < 20)
                { Thread.Sleep(25); }
            }
            owned = false;
        }
        finally { if (owned) NativeFiles.Delete(handle); }
    }
    public EngineSnapshot? Read(bool verifyProcess = true)
    {
        if (!File.Exists(FilePath)) return null;
        Paths.NoReparse(FilePath);
        using FileStream stream = new(FilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        EngineSnapshot? snapshot = JsonSerializer.Deserialize(stream, OperationJson.Default.EngineSnapshot);
        if (snapshot is null) return null;
        if (snapshot.Version is not (1 or 2 or 3) || snapshot.Pid <= 0 || snapshot.StartedUtcTicks <= 0 || snapshot.Folders is null ||
            string.IsNullOrWhiteSpace(snapshot.Executable) || string.IsNullOrWhiteSpace(snapshot.ConfigPath) || snapshot.ConfigHash is null || snapshot.ConfigHash.Length != 64 ||
            snapshot.Folders.Any(f => f is null || string.IsNullOrWhiteSpace(f.Path) || string.IsNullOrWhiteSpace(f.Name))) throw new IOException("Invalid engine status");
        if (!verifyProcess) return snapshot;
        try
        {
            using Process process = Process.GetProcessById(snapshot.Pid);
            return !process.HasExited && process.StartTime.ToUniversalTime().Ticks == snapshot.StartedUtcTicks ? snapshot : null;
        }
        catch (ArgumentException) { return null; }
        catch (InvalidOperationException) { return null; }
        catch (System.ComponentModel.Win32Exception exception)
        { throw new IOException("Cannot verify the engine process identity; applied settings are unavailable.", exception); }
    }
    public void Clear(EngineSnapshot owner)
    {
        EngineSnapshot? current = Read(false);
        if (current?.Pid != owner.Pid || current.StartedUtcTicks != owner.StartedUtcTicks) return;
        using DirectoryGuard guard = new(); guard.Hold(Path.GetDirectoryName(FilePath)!);
        using SafeFileHandle handle = NativeFiles.Open(FilePath, false, true); NativeFiles.Delete(handle);
    }
}
