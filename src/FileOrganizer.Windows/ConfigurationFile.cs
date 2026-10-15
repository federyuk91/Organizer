using System.Security.Cryptography;
using System.Text;
using System.Security.AccessControl;
using FileOrganizer.Core;
using Microsoft.Win32.SafeHandles;

namespace FileOrganizer.Windows;

public sealed class ConfigurationConflictException(string message) : IOException(message);

public sealed class ConfigurationFile
{
    private byte[]? baseline;
    private Encoding encoding = new UTF8Encoding(false, true);
    private byte[] preamble = [];
    public string Path { get; }
    public string Text { get; private set; } = "";
    public ConfigurationFile(string path) { Path = System.IO.Path.GetFullPath(path); }
    public ConfigurationDocument Open()
    {
        Paths.NoReparse(Path);
        byte[] bytes = File.ReadAllBytes(Path);
        using MemoryStream memory = new(bytes);
        using StreamReader reader = new(memory, new UTF8Encoding(false, true), true);
        Text = reader.ReadToEnd(); encoding = reader.CurrentEncoding;
        byte[] candidate = encoding.GetPreamble();
        preamble = bytes.AsSpan().StartsWith(candidate) ? candidate : [];
        baseline = bytes;
        return new ConfigurationDocument(Text);
    }
    public bool HasExternalChanges()
    {
        Paths.NoReparse(Path);
        if (baseline is null) return File.Exists(Path);
        return !File.Exists(Path) || !CryptographicOperations.FixedTimeEquals(SHA256.HashData(baseline), SHA256.HashData(File.ReadAllBytes(Path)));
    }
    public void Save(string text, Action? beforeCommit = null)
    {
        ConfigurationLoader.Parse(text);
        Paths.NoReparse(Path);
        string identity = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.ToUpperInvariant())));
        using Mutex mutex = new(false, "FileOrganizer.ConfigSave-" + identity);
        bool acquired;
        try { acquired = mutex.WaitOne(0); } catch (AbandonedMutexException) { acquired = true; }
        if (!acquired) throw new ConfigurationConflictException("Another editor is saving this file. Retry after reloading.");
        try
        {
            using DirectoryGuard directory = new();
            directory.Hold(System.IO.Path.GetDirectoryName(Path)!);
            if (HasExternalChanges()) throw new ConfigurationConflictException("The configuration changed on disk. Reload or save to a different file.");
            if (baseline is not null && text == Text) return;
            byte[] bytes = [.. preamble, .. encoding.GetBytes(text)];
            string temporary = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(Path)!, ".fileorganizer-config-" + Guid.NewGuid().ToString("N") + ".tmp");
            using SafeFileHandle handle = NativeFiles.CreateNew(temporary, true);
            using FileStream output = new(handle, FileAccess.ReadWrite);
            bool owned = true;
            try
            {
                CopyPermissions(output);
                output.Write(bytes); output.Flush(true);
                beforeCommit?.Invoke();
                Paths.NoReparse(Path);
                if (HasExternalChanges()) throw new ConfigurationConflictException("An external edit arrived during saving. Original preserved.");
                if (baseline is not null) WriteBackup(baseline);
                if (HasExternalChanges()) throw new ConfigurationConflictException("An external edit arrived before commit. Original preserved.");
                NativeFiles.Rename(handle, Path, baseline is not null);
                owned = false; baseline = bytes; Text = text;
            }
            finally { if (owned) NativeFiles.Delete(handle); }
        }
        finally { mutex.ReleaseMutex(); }
    }
    private void WriteBackup(byte[] bytes)
    {
        Paths.NoReparse(Path + ".bak");
        string name = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(Path)!, ".fileorganizer-config-" + Guid.NewGuid().ToString("N") + ".tmp");
        using SafeFileHandle handle = NativeFiles.CreateNew(name, true);
        using FileStream stream = new(handle, FileAccess.ReadWrite);
        bool owned = true;
        try { CopyPermissions(stream); stream.Write(bytes); stream.Flush(true); NativeFiles.Rename(handle, Path + ".bak", true); owned = false; }
        finally { if (owned) NativeFiles.Delete(handle); }
    }
    private void CopyPermissions(FileStream stream)
    {
        if (baseline is null || !OperatingSystem.IsWindows()) return;
        FileSecurity original = new FileInfo(Path).GetAccessControl(AccessControlSections.Access);
        FileSecurity permissions = new();
        permissions.SetSecurityDescriptorBinaryForm(original.GetSecurityDescriptorBinaryForm(), AccessControlSections.Access);
        stream.SetAccessControl(permissions);
    }
}
