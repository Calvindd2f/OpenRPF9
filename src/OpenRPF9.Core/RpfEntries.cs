namespace OpenRPF9.Core;

public abstract class RpfEntry
{
    public required string Name { get; init; }
    public required uint NameOffset { get; init; }
    public required RpfEntryKind Kind { get; init; }
    public RpfDirectoryEntry? Parent { get; internal set; }
    public string FullPath => Parent is null || Parent.Parent is null ? Name : string.Concat(Parent.FullPath, "/", Name);
}

public sealed class RpfDirectoryEntry : RpfEntry
{
    public required uint EntriesIndex { get; init; }
    public required uint EntriesCount { get; init; }
    public List<RpfDirectoryEntry> Directories { get; } = [];
    public List<RpfFileEntry> Files { get; } = [];
}

public abstract class RpfFileEntry : RpfEntry
{
    public required uint FileOffset { get; init; }
    public required uint FileSize { get; init; }
    public required uint FileUncompressedSize { get; init; }
    public required uint EncryptionType { get; init; }
}

public sealed class RpfBinaryFileEntry : RpfFileEntry
{
}

public sealed class RpfResourceFileEntry : RpfFileEntry
{
    public required uint SystemFlags { get; init; }
    public required uint GraphicsFlags { get; init; }
    public required ushort Version { get; init; }
}
