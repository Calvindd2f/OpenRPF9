using System;
using System.Collections.Generic;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Text;

namespace OpenRPF9.Core
{
    public static class RpfParser
    {
        public static RpfArchiveInfo Parse(string path, long fileSize, MemoryMappedViewAccessor accessor, byte[] aesKey, byte[] ngKey)
        {
            byte[] headerBytes = new byte[16];
            accessor.ReadArray(0, headerBytes, 0, 16);
            RpfHeader header = ReadHeader(headerBytes, fileSize);

            int tocSize = checked((int)(header.EntryCount * 16));
            int namesSize = checked((int)header.NamesLength);
            long tocOffset = 16L;
            long namesOffset = tocOffset + tocSize;
            if (namesOffset + namesSize > fileSize) throw new RpfFormatException("RPF header declares TOC/names outside archive bounds.");

            byte[] toc = ReadBytes(accessor, tocOffset, tocSize);
            byte[] names = ReadBytes(accessor, namesOffset, namesSize);
            byte[] tocDec = DecryptBlock(header, toc, path, (uint)tocSize, aesKey, ngKey);
            byte[] namesDec = DecryptBlock(header, names, path, (uint)namesSize, aesKey, ngKey);

            List<RpfEntry> entries = ParseEntries(header, tocDec, namesDec);
            RpfDirectoryEntry root = BuildTree(entries);

            RpfArchiveInfo info = new RpfArchiveInfo();
            info.Path = path; info.FileSize = fileSize; info.Header = header; info.Root = root; info.Entries = entries;
            return info;
        }

        private static RpfHeader ReadHeader(byte[] data, long fileSize)
        {
            uint magic = BinaryIO.ReadU32(data, 0);
            if (!Enum.IsDefined(typeof(RpfVersion), magic)) throw new RpfFormatException("Unsupported RPF magic 0x" + magic.ToString("X8") + ".");
            RpfVersion version = (RpfVersion)magic;
            uint entryCount = BinaryIO.ReadU32(data, 4);
            uint namesRaw = BinaryIO.ReadU32(data, 8);
            uint encryptionRaw = BinaryIO.ReadU32(data, 12);

            uint namesLength = (version == RpfVersion.Rpf7) ? (namesRaw & 0x0FFFFFFFu) : namesRaw;
            uint nameShift = (version == RpfVersion.Rpf7) ? ((namesRaw >> 28) & 0x7u) : 0u;
            bool headerFlag = version == RpfVersion.Rpf7 && (namesRaw & 0x80000000u) != 0;
            RpfEncryption encryption = RpfEncryption.Ng;
            if (encryptionRaw == 0) encryption = RpfEncryption.None;
            else if (encryptionRaw == 0x4E45504F) encryption = RpfEncryption.Open;
            else if (encryptionRaw == 0x0FFFFFF9) encryption = RpfEncryption.Aes;
            else if (encryptionRaw == 0x0FEFFFFF) encryption = RpfEncryption.Ng;

            if (entryCount == 0) throw new RpfFormatException("RPF archive has no entries.");
            long tocBytes = (long)entryCount * 16;
            if (16 + tocBytes + namesLength > fileSize) throw new RpfFormatException("RPF header sizes exceed file size.");

            RpfHeader h = new RpfHeader();
            h.Version = version; h.EntryCount = entryCount; h.NamesLength = namesLength; h.Encryption = encryption; h.NameShift = nameShift; h.HeaderFlag = headerFlag; h.RawFlags = namesRaw;
            return h;
        }

        private static byte[] ReadBytes(MemoryMappedViewAccessor accessor, long offset, int length)
        {
            byte[] buffer = new byte[length];
            accessor.ReadArray(offset, buffer, 0, length);
            return buffer;
        }

        private static byte[] DecryptBlock(RpfHeader header, byte[] data, string path, uint length, byte[] aesKey, byte[] ngKey)
        {
            if (header.Encryption == RpfEncryption.None || header.Encryption == RpfEncryption.Open) return data;
            if (header.Encryption == RpfEncryption.Aes && aesKey != null && aesKey.Length == 32) return RpfCrypto.DecryptAes(data, aesKey);
            if (header.Encryption == RpfEncryption.Ng && ngKey != null && ngKey.Length > 0) return RpfCrypto.DecryptNg(data, Path.GetFileName(path), length, ngKey);
            if (header.Encryption == RpfEncryption.Aes) throw new RpfCryptoException("AES key for encrypted RPF archive.");
            if (header.Encryption == RpfEncryption.Ng) throw new RpfCryptoException("NG key for encrypted RPF archive.");
            return data;
        }

        private static List<RpfEntry> ParseEntries(RpfHeader header, byte[] toc, byte[] names)
        {
            List<RpfEntry> entries = new List<RpfEntry>((int)header.EntryCount);
            for (int i = 0; i < header.EntryCount; i++)
            {
                int o = i * 16;
                uint y = BinaryIO.ReadU32(toc, o);
                uint x = BinaryIO.ReadU32(toc, o + 4);
                uint nameOffset = (header.Version == RpfVersion.Rpf7) ? (uint)(BinaryIO.ReadU16(toc, o) << (int)header.NameShift) : y;
                string name = SafeName(names, (int)nameOffset, i);

                if (x == 0x7FFFFF00u)
                {
                    RpfDirectoryEntry de = new RpfDirectoryEntry();
                    de.Kind = RpfEntryKind.Directory; de.Name = name; de.NameOffset = nameOffset;
                    de.EntriesIndex = BinaryIO.ReadU32(toc, o + 8); de.EntriesCount = BinaryIO.ReadU32(toc, o + 12);
                    entries.Add(de);
                    continue;
                }

                if ((x & 0x80000000u) == 0)
                {
                    uint fileSize = BinaryIO.ReadU24(toc, o + 4);
                    uint fileOffset = BinaryIO.ReadU24(toc, o + 7);
                    uint uncomp = BinaryIO.ReadU32(toc, o + 12);
                    RpfBinaryFileEntry be = new RpfBinaryFileEntry();
                    be.Kind = RpfEntryKind.BinaryFile; be.Name = name; be.NameOffset = (uint)BinaryIO.ReadU16(toc, o);
                    be.FileOffset = fileOffset & 0x7FFFFFu; be.FileSize = (fileSize == 0 ? uncomp : fileSize); be.FileUncompressedSize = uncomp; be.EncryptionType = (fileOffset >> 23) & 1u;
                    entries.Add(be);
                }
                else
                {
                    ushort nameOff16 = BinaryIO.ReadU16(toc, o);
                    uint fileSize = BinaryIO.ReadU24(toc, o + 2);
                    uint fileOffset = BinaryIO.ReadU24(toc, o + 5);
                    uint sysFlags = BinaryIO.ReadU32(toc, o + 8);
                    uint gfxFlags = BinaryIO.ReadU32(toc, o + 12);
                    RpfResourceFileEntry re = new RpfResourceFileEntry();
                    re.Kind = RpfEntryKind.ResourceFile; re.Name = name; re.NameOffset = nameOff16; re.FileOffset = fileOffset & 0x7FFFFFu; re.FileSize = fileSize; re.FileUncompressedSize = 0; re.EncryptionType = (fileOffset >> 23) & 1u; re.SystemFlags = sysFlags; re.GraphicsFlags = gfxFlags; re.Version = (ushort)(sysFlags & 0xFFFF);
                    entries.Add(re);
                }
            }
            return entries;
        }

        private static string SafeName(byte[] names, int offset, int index)
        {
            if (offset < 0 || offset >= names.Length) return "entry_" + index;
            int end = offset;
            while (end < names.Length && names[end] != 0) end++;
            string name = Encoding.UTF8.GetString(names, offset, end - offset);
            return string.IsNullOrWhiteSpace(name) ? ("entry_" + index) : name;
        }

        private static RpfDirectoryEntry BuildTree(List<RpfEntry> entries)
        {
            if (entries.Count == 0 || !(entries[0] is RpfDirectoryEntry)) throw new RpfFormatException("RPF archive missing root directory entry.");
            RpfDirectoryEntry root = (RpfDirectoryEntry)entries[0];
            Stack<RpfDirectoryEntry> stack = new Stack<RpfDirectoryEntry>();
            stack.Push(root);
            while (stack.Count > 0)
            {
                RpfDirectoryEntry dir = stack.Pop();
                int start = checked((int)dir.EntriesIndex);
                int end = checked(start + (int)dir.EntriesCount);
                if (start < 0 || end > entries.Count) throw new RpfFormatException("Directory '" + dir.Name + "' has invalid entry range.");
                for (int i = start; i < end; i++)
                {
                    RpfEntry child = entries[i];
                    child.Parent = dir;
                    RpfDirectoryEntry childDir = child as RpfDirectoryEntry;
                    RpfFileEntry childFile = child as RpfFileEntry;
                    if (childDir != null) { dir.Directories.Add(childDir); stack.Push(childDir); }
                    else if (childFile != null) { dir.Files.Add(childFile); }
                }
            }
            return root;
        }
    }
}
