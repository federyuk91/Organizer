using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FileOrganizer.Core;
using Microsoft.Win32.SafeHandles;

namespace FileOrganizer.Windows;

public sealed record MoveRecord(int Version, string Id, DateTimeOffset Time, string ConfigPath, string Source,
    string Destination, long Size, string Sha256, FileStamp DestinationStamp, string State, FileStamp? RestoredStamp = null);
public sealed record UndoResult(string Id, bool Success, string Message);
public sealed class HistoryUnavailableException(string message, Exception inner) : IOException(message, inner);

public sealed class MoveHistory(string directory, Func<DateTimeOffset>? clock = null)
{
    public static string DefaultDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FileOrganizer", "history");
    public string DirectoryPath { get; } = Path.GetFullPath(directory);
    private readonly Func<DateTimeOffset> now = clock ?? (() => DateTimeOffset.UtcNow);
    private DateOnly lastPruned;
    private readonly object gate = new();
    private bool validated;
    private readonly Dictionary<string, long> expectedLengths = new(Paths.Comparer);

    internal async Task<MoveRecord> PrepareAsync(string configPath, string source, string target, FileStream input,
        NativeFiles.Information info, CancellationToken token)
    {
        input.Position = 0;
        string hash = Convert.ToHexString(await SHA256.HashDataAsync(input, token));
        input.Position = 0;
        MoveRecord entry = new(1, Guid.NewGuid().ToString("N"), now(), Path.GetFullPath(configPath), source, target,
            input.Length, hash, FileStamp.From(info), "Prepared");
        Append(entry);
        return entry;
    }
    internal MoveRecord SetDestination(MoveRecord entry, NativeFiles.Information info)
    {
        MoveRecord updated = entry with { DestinationStamp = FileStamp.From(info) };
        Append(updated); return updated;
    }
    internal void Commit(MoveRecord entry, NativeFiles.Information info) => Append(entry with { State = "Committed", DestinationStamp = FileStamp.From(info) });
    internal void Abort(MoveRecord entry) => Append(entry with { State = "Aborted" });

    private void Append(MoveRecord entry)
    {
        lock (gate)
        {
            try
            {
            if (!validated) { Read(); validated = true; }
            Prune();
            using DirectoryGuard guard = new(); guard.Hold(DirectoryPath, true);
            string path = Path.Combine(DirectoryPath, "history-" + entry.Time.UtcDateTime.ToString("yyyyMMdd", CultureInfo.InvariantCulture) + ".jsonl");
            using SafeFileHandle handle = NativeFiles.OpenJournal(path);
            using FileStream stream = new(handle, FileAccess.ReadWrite);
            if (stream.Length > 0 && (!expectedLengths.TryGetValue(path, out long expected) || expected != stream.Length)) Read();
            RepairTail(stream);
            stream.Seek(0, SeekOrigin.End);
            byte[] bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(entry, OperationJson.Default.MoveRecord) + "\n");
            stream.Write(bytes); stream.Flush(true);
            expectedLengths[path] = stream.Length;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            { throw new HistoryUnavailableException("History could not be saved; further movements must stop.", exception); }
        }
    }
    public IReadOnlyList<MoveRecord> Read(string? configPath = null)
    {
        lock (gate)
        {
            Prune();
            Dictionary<string, MoveRecord> entries = new(StringComparer.Ordinal);
            Dictionary<string, int> order = new(StringComparer.Ordinal);
            if (!Directory.Exists(DirectoryPath)) return [];
            Paths.NoReparse(DirectoryPath);
            foreach (string path in Directory.EnumerateFiles(DirectoryPath, "history-*.jsonl").Order(StringComparer.Ordinal))
            {
                Paths.NoReparse(path);
                using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using StreamReader reader = new(stream, Encoding.UTF8, true);
                string? line;
                while ((line = reader.ReadLine()) is not null)
                {
                    MoveRecord entry;
                    try { entry = JsonSerializer.Deserialize(line, OperationJson.Default.MoveRecord) ?? throw new IOException("Empty history record"); }
                    catch (JsonException exception)
                    {
                        if (reader.EndOfStream && IsOwnPartialRecord(line) && !EndsWithNewline(stream)) break;
                        throw new IOException("History is damaged; no unsafe undo will be attempted.", exception);
                    }
                    if (entry.Version != 1 || !Guid.TryParseExact(entry.Id, "N", out _) || entry.Size < 0 ||
                        entry.Sha256 is null || entry.Sha256.Length != 64 || entry.DestinationStamp is null ||
                        string.IsNullOrWhiteSpace(entry.Source) || string.IsNullOrWhiteSpace(entry.Destination) || string.IsNullOrWhiteSpace(entry.ConfigPath) ||
                        !Path.IsPathFullyQualified(entry.Source) || !Path.IsPathFullyQualified(entry.Destination) || !Path.IsPathFullyQualified(entry.ConfigPath) ||
                        entry.State is not ("Prepared" or "Committed" or "Aborted" or "UndoPrepared" or "Undone") ||
                        entry.Time > now().AddMinutes(1)) throw new IOException("Invalid history record");
                    if (entries.TryGetValue(entry.Id, out MoveRecord? previous) &&
                        (previous.Source != entry.Source || previous.Destination != entry.Destination || previous.Sha256 != entry.Sha256 || previous.ConfigPath != entry.ConfigPath || previous.Time != entry.Time))
                        throw new IOException("History record identity changed");
                    if (!entries.ContainsKey(entry.Id)) order[entry.Id] = order.Count;
                    entries[entry.Id] = entry;
                }
            }
            return entries.Values.Where(e => e.Time >= now().AddDays(-7) && e.State != "Aborted" &&
                (configPath is null || Paths.Comparer.Equals(e.ConfigPath, Path.GetFullPath(configPath)))).OrderByDescending(e => e.Time).ThenByDescending(e => order[e.Id]).ToArray();
        }
    }
    private static bool IsOwnPartialRecord(string line)
    {
        const string prefix = "{\"Version\":1,\"Id\":";
        return line.Length > 0 && (prefix.StartsWith(line, StringComparison.Ordinal) || line.StartsWith(prefix, StringComparison.Ordinal));
    }
    private static bool EndsWithNewline(FileStream stream)
    {
        if (stream.Length == 0) return true;
        stream.Position = stream.Length - 1; return stream.ReadByte() == '\n';
    }
    private static void RepairTail(FileStream stream)
    {
        if (EndsWithNewline(stream)) return;
        int length = (int)Math.Min(stream.Length, 1024 * 1024);
        byte[] tail = new byte[length]; stream.Position = stream.Length - length; stream.ReadExactly(tail);
        int start = Array.LastIndexOf(tail, (byte)'\n') + 1;
        if (start == 0 && stream.Length > length) throw new IOException("Oversized history tail");
        string line = Encoding.UTF8.GetString(tail, start, length - start);
        try
        {
            if (JsonSerializer.Deserialize(line, OperationJson.Default.MoveRecord) is null) throw new IOException("Invalid history tail");
            stream.Seek(0, SeekOrigin.End); stream.WriteByte((byte)'\n');
        }
        catch (JsonException)
        {
            if (!IsOwnPartialRecord(line)) throw new IOException("Unrecognized history tail; nothing changed");
            stream.SetLength(stream.Length - length + start);
        }
        stream.Flush(true);
    }
    private void Prune()
    {
        DateOnly today = DateOnly.FromDateTime(now().UtcDateTime);
        if (today == lastPruned) return;
        if (Directory.Exists(DirectoryPath))
        {
            using DirectoryGuard guard = new(); guard.Hold(DirectoryPath);
            DateOnly cutoff = DateOnly.FromDateTime(now().AddDays(-7).UtcDateTime);
            foreach (string path in Directory.EnumerateFiles(DirectoryPath, "history-*.jsonl"))
            {
                string name = Path.GetFileNameWithoutExtension(path);
                if (name.Length != 16 || !DateOnly.TryParseExact(name[8..], "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateOnly date) || date >= cutoff) continue;
                using SafeFileHandle handle = NativeFiles.Open(path, false, true); NativeFiles.Delete(handle);
            }
        }
        lastPruned = today;
    }

    public async Task<IReadOnlyList<UndoResult>> UndoAsync(IEnumerable<string> ids, string? configPath, CancellationToken token = default)
    {
        using InstanceLease lease = new(configPath ?? Path.Combine(EngineStateStore.DefaultDirectory, "history-undo.toml"), TimeSpan.FromSeconds(10));
        Dictionary<string, MoveRecord> entries = Read().ToDictionary(e => e.Id);
        Dictionary<string, int> rank = entries.Keys.Select((id, index) => (id, index)).ToDictionary(x => x.id, x => x.index);
        List<UndoResult> results = [];
        foreach (string id in ids.Distinct().OrderBy(id => rank.GetValueOrDefault(id, int.MaxValue)))
        {
            token.ThrowIfCancellationRequested();
            try
            {
                if (!entries.TryGetValue(id, out MoveRecord? entry)) throw new IOException("Movement is unavailable or older than seven days");
                if (configPath is not null && !Paths.Comparer.Equals(entry.ConfigPath, Path.GetFullPath(configPath))) throw new IOException("Movement belongs to another configuration");
                if (entry.State is not ("Committed" or "Prepared" or "UndoPrepared")) throw new IOException("This movement is already undone or unavailable");
                string source = Paths.NormalizeLiteral(entry.Destination), target = Paths.NormalizeLiteral(entry.Source);
                if (Paths.Comparer.Equals(source, target) || Paths.Within(source, DirectoryPath) || Paths.Within(target, DirectoryPath)) throw new IOException("Unsafe undo path");
                using DirectoryGuard guard = new();
                guard.Hold(Path.GetDirectoryName(source)!); guard.Hold(Path.GetDirectoryName(target)!, true);
                if (entry.State == "UndoPrepared" && File.Exists(target) && !File.Exists(source) && entry.RestoredStamp is not null)
                {
                    using SafeFileHandle restoredHandle = NativeFiles.Open(target, false);
                    using FileStream restoredContent = new(restoredHandle, FileAccess.Read, 65536, false);
                    FileStamp completedStamp = FileStamp.From(NativeFiles.Info(restoredHandle));
                    if (!completedStamp.SameObject(entry.RestoredStamp) || restoredContent.Length != entry.Size ||
                        Convert.ToHexString(await SHA256.HashDataAsync(restoredContent, token)) != entry.Sha256)
                        throw new IOException("Interrupted undo cannot be verified");
                    Complete(entry, completedStamp, entries);
                    results.Add(new(id, true, "Previously completed undo verified")); continue;
                }
                if (File.Exists(target) || Directory.Exists(target)) throw new IOException("Original path is occupied; nothing overwritten");
                using SafeFileHandle handle = NativeFiles.Open(source, false, true);
                using FileStream input = new(handle, FileAccess.Read, 65536, false);
                NativeFiles.Information info = NativeFiles.Info(handle);
                FileStamp stamp = FileStamp.From(info);
                if (!stamp.SameObject(entry.DestinationStamp) || entry.State == "Committed" && stamp.WriteTime != entry.DestinationStamp.WriteTime || input.Length != entry.Size)
                    throw new IOException("Destination file was replaced or modified");
                input.Position = 0;
                string hash = Convert.ToHexString(await SHA256.HashDataAsync(input, token));
                if (hash != entry.Sha256) throw new IOException("Destination content changed; nothing moved");
                MoveRecord undo = entry with { State = "UndoPrepared", RestoredStamp = stamp };
                Append(undo);
                bool copied = await SafeTransfer.MoveLockedAsync(input, handle, target, info.Volume, guard.Volume, token, prepareCopy: copied =>
                { undo = undo with { RestoredStamp = FileStamp.From(copied) }; Append(undo); });
                NativeFiles.Information restored;
                if (!copied) restored = NativeFiles.Info(handle);
                else { using SafeFileHandle restoredHandle = NativeFiles.Open(target, false); restored = NativeFiles.Info(restoredHandle); }
                FileStamp restoredStamp = FileStamp.From(restored);
                Complete(undo, restoredStamp, entries);
                results.Add(new(id, true, "Restored to " + target));
            }
            catch (HistoryUnavailableException) { throw; }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ConfigException)
            { results.Add(new(id, false, exception.Message)); }
        }
        return results;
    }
    private void Complete(MoveRecord entry, FileStamp restored, Dictionary<string, MoveRecord> entries)
    {
        MoveRecord done = entry with { State = "Undone", RestoredStamp = restored }; Append(done); entries[entry.Id] = done;
        MoveRecord? predecessor = entries.Values.Where(e => e.Id != entry.Id && e.State == "Committed" && e.Time <= entry.Time &&
            Paths.Comparer.Equals(e.Destination, entry.Source) && e.Sha256 == entry.Sha256).MaxBy(e => e.Time);
        if (predecessor is not null)
        {
            MoveRecord refreshed = predecessor with { DestinationStamp = restored }; Append(refreshed); entries[predecessor.Id] = refreshed;
        }
    }
}
