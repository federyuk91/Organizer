using System.Security.Cryptography;
using FileOrganizer.Core;
using Microsoft.Win32.SafeHandles;

namespace FileOrganizer.Windows;

public enum TransferOutcome { Moved, Skipped, DeletedIdentical, DryRun, AlreadyPlaced }

public sealed class SafeTransfer(Action<LogLevel, string> log)
{
    // Force-copy and the pre-commit hook provide deterministic transaction fault tests.
    public async Task<TransferOutcome> ExecuteAsync(Configuration config, Folder folder, Rule rule, string source,
        CancellationToken cancellationToken = default, bool forceCopy = false, Action? beforeCommit = null, ManagedDirectories? managed = null)
    {
        source = Paths.Normalize(source);
        managed ??= new ManagedDirectories(config);
        if (!ReferenceEquals(managed.Configuration, config) || !ReferenceEquals(managed.Owner(source), folder))
            throw new IOException("Source outside the monitor's managed scope: " + source);
        if (Matching.First(folder, source) != rule) throw new IOException("The requested rule is not the current first match");
        using DirectoryGuard guard = new();
        guard.Hold(Path.GetDirectoryName(source)!);
        Paths.Validate(config);
        if (ManagedDirectories.AlreadyPlaced(source, rule)) return TransferOutcome.AlreadyPlaced;
        if (config.General.DryRun)
        {
            Paths.NoReparse(rule.Destination);
            log(LogLevel.Info, $"Dry run: {source} -> {rule.Destination}");
            return TransferOutcome.DryRun;
        }
        guard.Hold(rule.Destination, true);
        uint destinationVolume = guard.Volume;
        Paths.Validate(config);
        using SafeFileHandle sourceHandle = NativeFiles.Open(source, false, true);
        using FileStream input = new(sourceHandle, FileAccess.Read, 65536, false);
        NativeFiles.Information sourceInfo = NativeFiles.Info(sourceHandle);
        string desired = Path.Combine(rule.Destination, Path.GetFileName(source));
        for (int attempt = 0; attempt < 10000; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string target = desired;
            if (Exists(target))
            {
                using SafeFileHandle existingHandle = await OpenCollisionAsync(target, cancellationToken);
                NativeFiles.Information existingInfo = NativeFiles.Info(existingHandle);
                if (sourceInfo.Volume == existingInfo.Volume && sourceInfo.IndexHigh == existingInfo.IndexHigh && sourceInfo.IndexLow == existingInfo.IndexLow)
                    throw new IOException("Source and target have the same filesystem identity");
                using FileStream existing = new(existingHandle, FileAccess.Read, 65536, false);
                bool identical = await IdenticalAsync(input, existing, cancellationToken);
                if (identical && folder.Duplicates.IdenticalContent == IdenticalContent.Skip ||
                    !identical && folder.Duplicates.NameCollision == NameCollision.Skip) return TransferOutcome.Skipped;
                if (identical && folder.Duplicates.IdenticalContent == IdenticalContent.Delete)
                {
                    // Both handles deny writes and deletion throughout independent hash verification.
                    beforeCommit?.Invoke();
                    if (!await IdenticalAsync(input, existing, cancellationToken)) return TransferOutcome.Skipped;
                    if (NativeFiles.Info(sourceHandle).Links != 1) throw new IOException("Source link identity changed");
                    NativeFiles.Delete(sourceHandle);
                    log(LogLevel.Info, "Deleted verified identical source: " + source);
                    return TransferOutcome.DeletedIdentical;
                }
                string suffix = identical ? folder.Duplicates.CopySuffix : "_{n}";
                target = Available(rule.Destination, Path.GetFileNameWithoutExtension(source), Path.GetExtension(source), suffix);
            }
            try
            {
                Rule? routed = Matching.First(folder, target);
                if (routed is null || !Paths.Comparer.Equals(routed.Destination, rule.Destination))
                    throw new ConfigException("Collision suffix would change rule routing; source preserved: " + source);
                if (!forceCopy && sourceInfo.Volume == destinationVolume)
                {
                    beforeCommit?.Invoke();
                    try { NativeFiles.Rename(sourceHandle, target); }
                    catch (IOException exception) when ((exception.HResult & 0xffff) == 17)
                    { await CopyCommitAsync(input, sourceHandle, target, cancellationToken, beforeCommit); }
                }
                else await CopyCommitAsync(input, sourceHandle, target, cancellationToken, beforeCommit);
                log(LogLevel.Info, $"Moved: {source} -> {target}");
                return TransferOutcome.Moved;
            }
            catch (IOException exception) when ((exception.HResult & 0xffff) is 80 or 183)
            {
                log(LogLevel.Debug, "Target claimed concurrently; resolving again: " + target);
            }
        }
        throw new IOException("Collision retry limit reached for " + source);
    }

    private static async Task<SafeFileHandle> OpenCollisionAsync(string path, CancellationToken token)
    {
        for (int attempt = 0; ; attempt++)
        {
            try { return NativeFiles.Open(path, false); }
            catch (IOException exception) when ((exception.HResult & 0xffff) is 32 or 33 && attempt < 20)
            {
                // A concurrent no-replace rename may still hold its DELETE-access source handle.
                await Task.Delay(50, token);
            }
        }
    }
    private static bool Exists(string path)
    {
        try { File.GetAttributes(path); return true; }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
    }
    private static string Available(string directory, string stem, string extension, string suffix)
    {
        for (int n = 1; n <= 100000; n++)
        {
            string candidate = Path.Combine(directory, stem + suffix.Replace("{n}", n.ToString(System.Globalization.CultureInfo.InvariantCulture)) + extension);
            if (!Exists(candidate)) return candidate;
        }
        throw new IOException("No available collision name");
    }
    private static async Task<bool> IdenticalAsync(FileStream a, FileStream b, CancellationToken token)
    {
        if (a.Length != b.Length) return false;
        a.Position = b.Position = 0;
        byte[] first = await SHA256.HashDataAsync(a, token);
        byte[] second = await SHA256.HashDataAsync(b, token);
        return CryptographicOperations.FixedTimeEquals(first, second);
    }
    private static async Task CopyCommitAsync(FileStream input, SafeFileHandle sourceHandle, string target,
        CancellationToken token, Action? beforeCommit)
    {
        string temp = Path.Combine(Path.GetDirectoryName(target)!, ".fileorganizer-" + Guid.NewGuid().ToString("N") + ".tmp");
        using SafeFileHandle tempHandle = NativeFiles.CreateNew(temp);
        using FileStream output = new(tempHandle, FileAccess.ReadWrite, 65536, false);
        bool owned = true;
        try
        {
            input.Position = 0;
            await input.CopyToAsync(output, 65536, token);
            output.Flush(true);
            if (!await IdenticalAsync(input, output, token)) throw new IOException("Cross-volume verification failed");
            token.ThrowIfCancellationRequested();
            beforeCommit?.Invoke();
            NativeFiles.Rename(tempHandle, target);
            owned = false;
            NativeFiles.Delete(sourceHandle);
        }
        finally { if (owned) NativeFiles.Delete(tempHandle); }
    }
}
