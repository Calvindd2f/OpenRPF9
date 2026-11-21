namespace OpenRPF9.Core;

public interface IRpfArchive : IDisposable
{
    RpfArchiveInfo Info { get; }
    Stream OpenRead(RpfFileEntry entry);
    byte[] ReadFile(RpfFileEntry entry);
    void ExtractFile(RpfFileEntry entry, string outputPath);
    IEnumerable<RpfFileEntry> EnumerateFiles();
}
