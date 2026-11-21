using System.IO.MemoryMappedFiles;

namespace OpenRPF9.Core;

public static class RpfParser
{
    public static RpfArchiveInfo Parse(string path, long fileSize, MemoryMappedViewAccessor accessor, byte[] aesKey, byte[] ngKey)
    {
        Span<byte> headerBytes = stackalloc byte[16];
        accessor.ReadArray(0, headerBytes.ToArray(), 0, 16);
        var header = ReadHeader(headerBytes, fileSize);

        var tocSize = checked((int)(header.EntryCount * 16));
        var namesSize = checked((int)header.NamesLength);
        var tocOffset = 16L;
        var namesOffset = tocOffset + tocSize;
        if (namesOffset + namesSize > fileSize)
            throw new RpfFormatException("RPF header declares TOC/names outside archive bounds.");

        var toc = ReadBytes(accessor, tocOffset, tocSize);
        var names = ReadBytes(accessor, namesOffset, namesSize);
        var tocDec = DecryptBlock(header, toc, path, (uint)tocSize, aesKey, ngKey);
        var namesDec = DecryptBlock(header, names, path, (uint)namesSize, aesKey, ngKey);

        var entries = ParseEntries(header, tocDec, namesDec);
        var root = BuildTree(entries);

        return new RpfArchiveInfo
        {
            Path = path,
            FileSize = fileSize,
            Header = header,
            Root = root,
            Entries = entries
        };
    }

    static RpfHeader ReadHeader(ReadOnlySpan<byte> data, long fileSize)
    {
        var magic = BinaryIO.ReadU32(data, 0);
        if (!Enum.IsDefined(typeof(RpfVersion), magic))
            throw new RpfFormatException($"Unsupported RPF magic 0x{magic:X8}.");
        var version = (RpfVersion)magic;
        var entryCount = BinaryIO.ReadU32(data, 4);
        var namesRaw = BinaryIO.ReadU32(data, 8);
        var encryptionRaw = BinaryIO.ReadU32(data, 12);

        var namesLength = version == RpfVersion.Rpf7 ? namesRaw & 0x0FFFFFFFu : namesRaw;
        var nameShift = version == RpfVersion.Rpf7 ? (namesRaw >> 28) & 0x7u : 0u;
        var headerFlag = version == RpfVersion.Rpf7 && (namesRaw & 0x80000000u) != 0;
        var encryption = encryptionRaw switch
        {
            0 => RpfEncryption.None,
            0x4E45504F => RpfEncryption.Open,
            0x0FFFFFF9 => RpfEncryption.Aes,
            0x0FEFFFFF => RpfEncryption.Ng,
            _ => RpfEncryption.Ng
        };

        if (entryCount == 0)
            throw new RpfFormatException("RPF archive has no entries.");
        var tocBytes = (long)entryCount * 16;
        if (16 + tocBytes + namesLength > fileSize)
            throw new RpfFormatException("RPF header sizes exceed file size.");

        return new RpfHeader
        {
            Version = version,
            EntryCount = entryCount,
            NamesLength = namesLength,
            Encryption = encryption,
            NameShift = nameShift,
            HeaderFlag = headerFlag,
            RawFlags = namesRaw
        };
    }

    static byte[] ReadBytes(MemoryMappedViewAccessor accessor, long offset, int length)
    {
        var buffer = new byte[length];
        accessor.ReadArray(offset, buffer, 0, length);
        return buffer;
    }

    static byte[] DecryptBlock(RpfHeader header, byte[] data, string path, uint length, byte[] aesKey, byte[] ngKey)
    {
        return header.Encryption switch
        {
            RpfEncryption.None => data,
            RpfEncryption.Open => data,
            RpfEncryption.Aes when aesKey.Length == 32 => RpfCrypto.DecryptAes(data, aesKey),
            RpfEncryption.Ng when ngKey.Length > 0 => RpfCrypto.DecryptNg(data, Path.GetFileName(path), length, ngKey),
            RpfEncryption.Aes => throw new RpfCryptoException("AES key required for encrypted RPF archive."),
            RpfEncryption.Ng => throw new RpfCryptoException("NG key required for encrypted RPF archive."),
            _ => data
        };
    }

    static List<RpfEntry> ParseEntries(RpfHeader header, byte[] toc, byte[] names)
    {
        var entries = new List<RpfEntry>((int)header.EntryCount);
        for (var i = 0; i < header.EntryCount; i++)
        {
            var o = i * 16;
            var y = BinaryIO.ReadU32(toc, o);
            var x = BinaryIO.ReadU32(toc, o + 4);
            var nameOffset = header.Version == RpfVersion.Rpf7 ? (BinaryIO.ReadU16(toc, o) << (int)header.NameShift) : y;
            var name = SafeName(names, (int)nameOffset, i);

            if (x == 0x7FFFFF00u)
            {
                entries.Add(new RpfDirectoryEntry
                {
                    Kind = RpfEntryKind.Directory,
                    Name = name,
                    NameOffset = nameOffset,
                    EntriesIndex = BinaryIO.ReadU32(toc, o + 8),
                    EntriesCount = BinaryIO.ReadU32(toc, o + 12)
                });
                continue;
            }

            if ((x & 0x80000000u) == 0)
            {
                var fileSize = BinaryIO.ReadU24(toc, o + 4);
                var fileOffset = BinaryIO.ReadU24(toc, o + 7);
                var uncomp = BinaryIO.ReadU32(toc, o + 12);
                entries.Add(new RpfBinaryFileEntry
                {
                    Kind = RpfEntryKind.BinaryFile,
                    Name = name,
                    NameOffset = (uint)BinaryIO.ReadU16(toc, o),
                    FileOffset = fileOffset & 0x7FFFFFu,
                    FileSize = fileSize == 0 ? uncomp : fileSize,
                    FileUncompressedSize = uncomp,
                    EncryptionType = (fileOffset >> 23) & 1u
                });
            }
            else
            {
                var nameOff16 = BinaryIO.ReadU16(toc, o);
                var fileSize = BinaryIO.ReadU24(toc, o + 2);
                var fileOffset = BinaryIO.ReadU24(toc, o + 5);
                var sysFlags = BinaryIO.ReadU32(toc, o + 8);
                var gfxFlags = BinaryIO.ReadU32(toc, o + 12);
                entries.Add(new RpfResourceFileEntry
                {
                    Kind = RpfEntryKind.ResourceFile,
                    Name = name,
                    NameOffset = nameOff16,
                    FileOffset = fileOffset & 0x7FFFFFu,
                    FileSize = fileSize,
                    FileUncompressedSize = 0,
                    EncryptionType = (fileOffset >> 23) & 1u,
                    SystemFlags = sysFlags,
                    GraphicsFlags = gfxFlags,
                    Version = (ushort)(sysFlags & 0xFFFF)
                });
            }
        }
        return entries;
    }

    static string SafeName(byte[] names, int offset, int index)
    {
        if (offset < 0 || offset >= names.Length)
            return $"entry_{index}";
        var end = offset;
        while (end < names.Length && names[end] != 0)
            end++;
        var name = System.Text.Encoding.UTF8.GetString(names, offset, end - offset);
        return string.IsNullOrWhiteSpace(name) ? $"entry_{index}" : name;
    }

    static RpfDirectoryEntry BuildTree(List<RpfEntry> entries)
    {
        if (entries.Count == 0 || entries[0] is not RpfDirectoryEntry root)
            throw new RpfFormatException("RPF archive missing root directory entry.");

        var stack = new Stack<RpfDirectoryEntry>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var dir = stack.Pop();
            var start = checked((int)dir.EntriesIndex);
            var end = checked(start + (int)dir.EntriesCount);
            if (start < 0 || end > entries.Count)
                throw new RpfFormatException($"Directory '{dir.Name}' has invalid entry range.");
            for (var i = start; i < end; i++)
            {
                var child = entries[i];
                child.Parent = dir;
                switch (child)
                {
                    case RpfDirectoryEntry childDir:
                        dir.Directories.Add(childDir);
                        stack.Push(childDir);
                        break;
                    case RpfFileEntry childFile:
                        dir.Files.Add(childFile);
                        break;
                }
            }
        }
        return root;
    }
}
