namespace OpenRPF9.Core;

public sealed class RpfHeader
{
    public required RpfVersion Version { get; init; }
    public required uint EntryCount { get; init; }
    public required uint NamesLength { get; init; }
    public required RpfEncryption Encryption { get; init; }
    public uint NameShift { get; init; }
    public bool HeaderFlag { get; init; }
    public uint RawFlags { get; init; }
}

public sealed class RpfArchiveInfo
{
    public required string Path { get; init; }
    public required long FileSize { get; init; }
    public required RpfHeader Header { get; init; }
    public required RpfDirectoryEntry Root { get; init; }
    public required IReadOnlyList<RpfEntry> Entries { get; init; }
}
