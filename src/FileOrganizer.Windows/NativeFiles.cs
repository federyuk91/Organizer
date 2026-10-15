using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace FileOrganizer.Windows;

internal static class NativeFiles
{
    [StructLayout(LayoutKind.Sequential)]
    internal struct Information
    {
        public uint Attributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME Creation, Access, Write;
        public uint Volume, SizeHigh, SizeLow, Links, IndexHigh, IndexLow;
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string path, uint access, uint share, nint security, uint creation, uint flags, nint template);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out Information information);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetFileInformationByHandle(SafeFileHandle handle, int informationClass, nint information, uint size);

    internal static SafeFileHandle CreateNew(string path, bool writeSecurity = false)
    {
        SafeFileHandle handle = CreateFileW(Extended(path), 0xC0010000u | (writeSecurity ? 0x40000u : 0), 1, 0, 1, 0x00200000, 0);
        if (!handle.IsInvalid) return handle;
        handle.Dispose();
        throw Error(path);
    }

    internal static SafeFileHandle OpenJournal(string path)
    {
        SafeFileHandle handle = CreateFileW(Extended(path), 0xC0000000u, 1, 0, 4, 0x00200000, 0);
        if (handle.IsInvalid) { handle.Dispose(); throw Error(path); }
        try
        {
            Information info = Info(handle);
            if ((info.Attributes & 0x410) != 0 || info.Links != 1) throw new IOException("Unsafe journal file: " + path);
            return handle;
        }
        catch { handle.Dispose(); throw; }
    }

    internal static SafeFileHandle Open(string path, bool directory, bool deleteAccess = false)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Safe transfers require Windows.");
        SafeFileHandle handle = CreateFileW(Extended(path), 0x80000000u | (deleteAccess ? 0x10000u : 0),
            directory ? 3u : 1u, 0, 3, 0x00200000u | (directory ? 0x02000000u : 0), 0);
        if (handle.IsInvalid) { handle.Dispose(); throw Error(path); }
        try
        {
            Information information = Info(handle);
            if ((information.Attributes & 0x400) != 0 || ((information.Attributes & 0x10) != 0) != directory)
                throw new IOException("Unsafe filesystem object: " + path);
            if (!directory && information.Links != 1) throw new IOException("Hard-linked files are not supported: " + path);
            return handle;
        }
        catch { handle.Dispose(); throw; }
    }
    internal static Information Info(SafeFileHandle handle)
    {
        if (!GetFileInformationByHandle(handle, out Information result)) throw Error("GetFileInformationByHandle");
        return result;
    }
    internal static void Delete(SafeFileHandle handle)
    {
        nint data = Marshal.AllocHGlobal(4);
        try
        {
            Marshal.WriteInt32(data, 1);
            if (!SetFileInformationByHandle(handle, 4, data, 4)) throw Error("Delete verified source");
        }
        finally { Marshal.FreeHGlobal(data); }
    }
    internal static void Rename(SafeFileHandle handle, string target, bool replace = false)
    {
        // FILE_RENAME_INFO: BOOLEAN, aligned HANDLE, DWORD length, UTF-16 name.
        string name = Extended(target);
        int offset = IntPtr.Size == 8 ? 20 : 12;
        int length = checked(name.Length * 2);
        nint data = Marshal.AllocHGlobal(offset + length + 2);
        try
        {
            for (int i = 0; i < offset + length + 2; i++) Marshal.WriteByte(data, i, 0);
            Marshal.WriteByte(data, 0, replace ? (byte)1 : (byte)0);
            Marshal.WriteInt32(data, offset - 4, length);
            Marshal.Copy(name.ToCharArray(), 0, data + offset, name.Length);
            if (!SetFileInformationByHandle(handle, 3, data, (uint)(offset + length))) throw Error(target);
        }
        finally { Marshal.FreeHGlobal(data); }
    }
    internal static string Extended(string path) => path.StartsWith(@"\\?\") ? path : @"\\?\" + path;
    private static IOException Error(string operation)
    {
        int error = Marshal.GetLastWin32Error();
        return new IOException(operation + ": " + new Win32Exception(error).Message, unchecked((int)(0x80070000u | (uint)error)));
    }
}

internal sealed class DirectoryGuard : IDisposable
{
    private readonly List<SafeFileHandle> handles = [];
    private readonly HashSet<string> paths = new(StringComparer.OrdinalIgnoreCase);
    internal uint Volume { get; private set; }
    internal void Hold(string directory, bool create = false)
    {
        string root = Path.GetPathRoot(directory) ?? throw new IOException("No local root");
        HoldOne(root, false);
        string current = root;
        foreach (string segment in directory[root.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            HoldOne(current, create);
        }
    }
    private void HoldOne(string directory, bool create)
    {
        if (paths.Contains(directory)) return;
        if (create && !Directory.Exists(directory)) Directory.CreateDirectory(directory);
        SafeFileHandle handle = NativeFiles.Open(directory, true);
        handles.Add(handle);
        paths.Add(directory);
        Volume = NativeFiles.Info(handle).Volume;
    }
    public void Dispose()
    {
        for (int i = handles.Count - 1; i >= 0; i--) handles[i].Dispose();
    }
}
