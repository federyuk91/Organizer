using System.Security.Principal;

namespace FileOrganizer.Windows;

public sealed class InstanceLease : IDisposable
{
    private readonly ManualResetEventSlim ready = new();
    private readonly ManualResetEventSlim release = new();
    private readonly Thread owner;
    private readonly CancellationTokenSource requestedStop = new();
    private Exception? error;
    private int disposed;
    public CancellationToken StopRequested => requestedStop.Token;
    private static string UserName
    {
        get
        {
            if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
            using WindowsIdentity identity = WindowsIdentity.GetCurrent();
            return @"Global\FileOrganizer-" + (identity.User?.Value ?? throw new InvalidOperationException("No user SID"));
        }
    }
    public InstanceLease(string configPath, TimeSpan? timeout = null)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        using WindowsIdentity identity = WindowsIdentity.GetCurrent();
        string sid = identity.User?.Value ?? throw new InvalidOperationException("No user SID");
        string legacy = @"Local\FileOrganizer-" + sid + "-" + StartupRegistration.Identity(configPath);
        // Mutex ownership is thread-affine; keep it off async continuation threads.
        string userName = UserName;
        owner = new Thread(() => Hold(userName, legacy, timeout ?? TimeSpan.Zero)) { IsBackground = true, Name = "FileOrganizer instance lease" };
        owner.Start(); ready.Wait();
        if (error is not null) { Dispose(); throw new IOException("Another File Organizer worker is active for this Windows user, or its lock is unavailable", error); }
    }
    public static bool RequestStop()
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        try { using EventWaitHandle stop = EventWaitHandle.OpenExisting(UserName + "-stop"); return stop.Set(); }
        catch (WaitHandleCannotBeOpenedException) { return false; }
    }
    private void Hold(string name, string legacy, TimeSpan timeout)
    {
        bool acquired = false;
        try
        {
            using Mutex mutex = new(false, name);
            try
            {
                try { acquired = mutex.WaitOne(timeout); }
                catch (AbandonedMutexException) { acquired = true; }
                if (!acquired) throw new IOException("Worker already running for this config");
                using Mutex legacyMutex = new(false, legacy);
                bool legacyAcquired;
                try { legacyAcquired = legacyMutex.WaitOne(0); }
                catch (AbandonedMutexException) { legacyAcquired = true; }
                if (!legacyAcquired) throw new IOException("An older worker is active for this config");
                try
                {
                    using EventWaitHandle stop = new(false, EventResetMode.ManualReset, name + "-stop");
                    stop.Reset(); ready.Set();
                    if (WaitHandle.WaitAny([release.WaitHandle, stop]) == 1) requestedStop.Cancel();
                    release.Wait();
                }
                finally { legacyMutex.ReleaseMutex(); }
            }
            finally { if (acquired) mutex.ReleaseMutex(); }
        }
        catch (Exception exception) { error = exception; ready.Set(); }
    }
    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        release.Set(); owner.Join(); ready.Dispose(); release.Dispose(); requestedStop.Dispose();
    }
}
