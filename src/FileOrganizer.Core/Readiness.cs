using System.Diagnostics;

namespace FileOrganizer.Core;

public static class Readiness
{
    public static async Task<bool> WaitAsync(string path, TimeSpan timeout, CancellationToken cancellationToken,
        TimeSpan? stability = null)
    {
        TimeSpan interval = stability ?? TimeSpan.FromMilliseconds(500);
        Stopwatch elapsed = Stopwatch.StartNew();
        (long Size, DateTime Modified)? previous = null;
        while (elapsed.Elapsed < timeout)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                Paths.NoReparse(path);
                FileInfo file = new(path);
                if (!file.Exists) return false;
                (long, DateTime) current = (file.Length, file.LastWriteTimeUtc);
                using FileStream check = new(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                if (previous == current) return true;
                previous = current;
            }
            catch (IOException) { previous = null; }
            catch (UnauthorizedAccessException) { return false; }
            TimeSpan remaining = timeout - elapsed.Elapsed;
            if (remaining > TimeSpan.Zero) await Task.Delay(remaining < interval ? remaining : interval, cancellationToken);
        }
        return false;
    }
}
