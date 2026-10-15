using System.Security.Principal;

namespace FileOrganizer.Windows;

public sealed class InstanceLease : IDisposable
{
    private readonly ManualResetEventSlim ready = new();
    private readonly ManualResetEventSlim release = new();
    private readonly Thread owner;
    private Exception? error;
    public InstanceLease(string configPath)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        string sid = WindowsIdentity.GetCurrent().User?.Value ?? throw new InvalidOperationException("No user SID");
        string name = @"Local\FileOrganizer-" + sid + "-" + StartupRegistration.Identity(configPath);
        // Mutex ownership is thread-affine; keep it off async continuation threads.
        owner = new Thread(() => Hold(name)) { IsBackground = true, Name = "FileOrganizer instance lease" };
        owner.Start(); ready.Wait();
        if (error is not null) { Dispose(); throw new IOException("Another worker is active or instance lock is unavailable", error); }
    }
    private void Hold(string name)
    {
        bool acquired = false;
        try
        {
            using Mutex mutex = new(false, name);
            try
            {
                try { acquired = mutex.WaitOne(0); }
                catch (AbandonedMutexException) { acquired = true; }
                if (!acquired) throw new IOException("Worker already running for this config");
                ready.Set(); release.Wait();
            }
            finally { if (acquired) mutex.ReleaseMutex(); }
        }
        catch (Exception exception) { error = exception; ready.Set(); }
    }
    public void Dispose() { release.Set(); owner.Join(); ready.Dispose(); release.Dispose(); }
}
