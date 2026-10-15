using System.Runtime.InteropServices;

namespace FileOrganizer.Windows;

public static class BackgroundConsole
{
    [DllImport("kernel32.dll")]
    private static extern bool FreeConsole();
    public static void Detach()
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        // Detaching also releases a Windows Terminal console session; hiding its pseudo-window does not.
        FreeConsole();
        Console.SetOut(TextWriter.Null); Console.SetError(TextWriter.Null);
    }
}
