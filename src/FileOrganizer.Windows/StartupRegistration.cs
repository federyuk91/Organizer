using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32;

namespace FileOrganizer.Windows;

public interface IStartupStore
{
    string? Get(string name) => throw new NotSupportedException("Startup store does not support reading");
    void Set(string name, string command);
    void Remove(string name);
}

[SupportedOSPlatform("windows")]
public sealed class RegistryStartupStore(string keyPath = @"Software\Microsoft\Windows\CurrentVersion\Run") : IStartupStore
{
    public string? Get(string name)
    {
        using RegistryKey? key = Registry.CurrentUser.OpenSubKey(keyPath, false);
        object? value = key?.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
        if (value is null) return null;
        return value as string ?? throw new IOException("Autorun entry is not a string");
    }
    public void Set(string name, string command)
    {
        using RegistryKey key = Registry.CurrentUser.CreateSubKey(keyPath, true);
        key.SetValue(name, command, RegistryValueKind.String);
    }
    public void Remove(string name)
    {
        using RegistryKey? key = Registry.CurrentUser.OpenSubKey(keyPath, true);
        key?.DeleteValue(name, false);
    }
}

public static class StartupRegistration
{
    public static StartupState Read(IStartupStore store, string executable, string configPath)
    {
        try
        {
            string? actual = store.Get(EntryName(configPath));
            if (actual is null) return new(StartupStatus.Absent, null, "Not registered");
            string expected = $"\"{executable}\" run --config \"{configPath}\"";
            return string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase)
                ? new(StartupStatus.Registered, actual, "Registered for this engine and config")
                : new(StartupStatus.Mismatched, actual, "An entry exists, but its command differs from the selected engine/config");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Security.SecurityException or NotSupportedException)
        { return new(StartupStatus.Unreadable, null, exception.Message); }
    }
    public static string Identity(string configPath) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(configPath).ToUpperInvariant())))[..24];
    public static string EntryName(string configPath) => "FileOrganizer-" + Identity(configPath);
    public static void Install(IStartupStore store, string executable, string configPath)
    {
        if (!Path.IsPathFullyQualified(executable) || !Path.IsPathFullyQualified(configPath) || executable.Contains('"') || configPath.Contains('"'))
            throw new ArgumentException("Autostart requires absolute, quotable paths");
        if (Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Install from the published FileOrganizer.exe, not dotnet run.");
        store.Set(EntryName(configPath), $"\"{executable}\" run --config \"{configPath}\"");
    }
    public static void Uninstall(IStartupStore store, string configPath) => store.Remove(EntryName(configPath));
}

public enum StartupStatus { Absent, Registered, Mismatched, Unreadable }
public sealed record StartupState(StartupStatus Status, string? Command, string Message);
