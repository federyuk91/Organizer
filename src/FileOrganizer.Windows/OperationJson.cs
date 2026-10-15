using System.Text.Json.Serialization;

namespace FileOrganizer.Windows;

[JsonSerializable(typeof(MoveRecord))]
[JsonSerializable(typeof(EngineSnapshot))]
internal partial class OperationJson : JsonSerializerContext;

public sealed record FileStamp(uint Volume, uint IndexHigh, uint IndexLow, long WriteTime)
{
    internal static FileStamp From(NativeFiles.Information info) => new(info.Volume, info.IndexHigh, info.IndexLow,
        ((long)info.Write.dwHighDateTime << 32) | (uint)info.Write.dwLowDateTime);
    public bool SameObject(FileStamp other) => Volume == other.Volume && IndexHigh == other.IndexHigh && IndexLow == other.IndexLow;
}
