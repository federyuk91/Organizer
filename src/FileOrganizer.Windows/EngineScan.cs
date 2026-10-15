using System.Diagnostics;
using System.Text;

namespace FileOrganizer.Windows;

public sealed record ScanResult(int ExitCode, string Output, string Error);

public static class EngineScan
{
    public static async Task<ScanResult> RunAsync(string executable, string configPath, CancellationToken token = default)
    {
        ProcessStartInfo start = new(executable)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true
        };
        start.ArgumentList.Add("scan"); start.ArgumentList.Add("--config"); start.ArgumentList.Add(configPath);
        token.ThrowIfCancellationRequested();
        using Process process = new() { StartInfo = start };
        try { if (!process.Start()) throw new IOException("Cannot start the engine"); }
        catch (System.ComponentModel.Win32Exception exception) { throw new IOException("Cannot start the engine: " + exception.Message, exception); }
        Task<string> output = ReadTailAsync(process.StandardOutput), error = ReadTailAsync(process.StandardError);
        Task exited = process.WaitForExitAsync();
        TaskCompletionSource cancelled = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using CancellationTokenRegistration registration = token.Register(() => cancelled.TrySetResult());
        string? stopError = null;
        if (await Task.WhenAny(exited, cancelled.Task) != exited)
        {
            // Cancellation may arrive before the child has created its stop event.
            // Retry only while a requested stop is pending, never during idle operation.
            while (!exited.IsCompleted)
            {
                try { if (InstanceLease.RequestStop()) break; }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                { stopError = exception.Message; break; }
                await Task.WhenAny(exited, Task.Delay(100));
            }
        }
        // Retain ownership until exit even if requesting cancellation fails. Never kill an active transfer.
        await exited;
        string stdout = await output, stderr = await error;
        if (stopError is not null) stderr += "\nStop could not be requested: " + stopError;
        return new(process.ExitCode, stdout, stderr);
    }
    private static async Task<string> ReadTailAsync(StreamReader reader)
    {
        const int limit = 4096;
        StringBuilder tail = new(); char[] buffer = new char[1024]; int count;
        while ((count = await reader.ReadAsync(buffer)) > 0)
        {
            tail.Append(buffer, 0, count);
            if (tail.Length > limit) tail.Remove(0, tail.Length - limit);
        }
        return tail.ToString().Trim();
    }
}
