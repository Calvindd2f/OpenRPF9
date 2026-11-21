using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;

namespace OpenRPF9.Core;

public sealed class RpfArchive : IRpfArchive
{
    readonly MemoryMappedFile _mmf;
    readonly MemoryMappedViewAccessor _accessor;
    readonly FileStream _stream;
    readonly byte[] _keyAes;
    readonly byte[] _keyNg;
    bool _disposed;

    public RpfArchiveInfo Info { get; }

    RpfArchive(string path, MemoryMappedFile mmf, MemoryMappedViewAccessor accessor, FileStream stream, RpfArchiveInfo info, byte[] keyAes, byte[] keyNg)
    {
        _mmf = mmf;
        _accessor = accessor;
        _stream = stream;
        Info = info;
        _keyAes = keyAes;
        _keyNg = keyNg;
    }

    public static RpfArchive Open(string path, byte[]? aesKey = null, byte[]? ngKey = null)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("Path is required.", nameof(path));
        var fullPath = Path.GetFullPath(path);
        var fileInfo = new FileInfo(fullPath);
        if (!fileInfo.Exists)
            throw new FileNotFoundException("RPF archive not found.", fullPath);

        var stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.RandomAccess);
        var mmf = MemoryMappedFile.CreateFromFile(stream, mapName: null, capacity: 0, MemoryMappedFileAccess.Read, HandleInheritability.None, leaveOpen: true);
        var accessor = mmf.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);
        try
        {
            var info = RpfParser.Parse(fullPath, fileInfo.Length, accessor, aesKey ?? [], ngKey ?? []);
            return new RpfArchive(fullPath, mmf, accessor, stream, info, aesKey ?? [], ngKey ?? []);
        }
        catch
        {
            accessor.Dispose();
            mmf.Dispose();
            stream.Dispose();
            throw;
        }
    }

    public IEnumerable<RpfFileEntry> EnumerateFiles()
    {
        var stack = new Stack<RpfDirectoryEntry>();
        stack.Push(Info.Root);
        while (stack.Count > 0)
        {
            var dir = stack.Pop();
            foreach (var file in dir.Files)
                yield return file;
            for (var i = dir.Directories.Count - 1; i >= 0; i--)
                stack.Push(dir.Directories[i]);
        }
    }

    public byte[] ReadFile(RpfFileEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var data = ReadRaw(entry);
        return DecodeFileData(entry, data);
    }

    public Stream OpenRead(RpfFileEntry entry)
    {
        return new MemoryStream(ReadFile(entry), writable: false);
    }

    public void ExtractFile(RpfFileEntry entry, string outputPath)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        var dir = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
        var data = ReadFile(entry);
        File.WriteAllBytes(outputPath, data);
    }

    byte[] ReadRaw(RpfFileEntry entry)
    {
        var offset = (long)entry.FileOffset * 512;
        if (offset < 0 || offset + entry.FileSize > Info.FileSize)
            throw new RpfFormatException($"File entry '{entry.FullPath}' points outside archive bounds.");
        var buffer = new byte[entry.FileSize];
        _accessor.ReadArray(offset, buffer, 0, buffer.Length);
        return buffer;
    }

    byte[] DecodeFileData(RpfFileEntry entry, byte[] data)
    {
        var decrypted = entry.EncryptionType switch
        {
            0 => data,
            1 when Info.Header.Encryption == RpfEncryption.Aes => RpfCrypto.DecryptAes(data, _keyAes),
            1 when Info.Header.Encryption == RpfEncryption.Ng => RpfCrypto.DecryptNg(data, entry.Name, entry.FileSize, _keyNg),
            1 when _keyAes.Length == 32 => RpfCrypto.DecryptAes(data, _keyAes),
            1 when _keyNg.Length > 0 => RpfCrypto.DecryptNg(data, entry.Name, entry.FileSize, _keyNg),
            _ => data
        };

        if (entry.FileUncompressedSize != 0 && entry.FileUncompressedSize != entry.FileSize && decrypted.Length > 0)
        {
            try
            {
                return RpfCompression.Decompress(decrypted, (int)entry.FileUncompressedSize);
            }
            catch
            {
                return decrypted;
            }
        }
        return decrypted;
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _accessor.Dispose();
        _mmf.Dispose();
        _stream.Dispose();
    }
}
