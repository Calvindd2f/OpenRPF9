using System;
using System.Collections.Generic;
using System.IO;
using System.IO.MemoryMappedFiles;

namespace OpenRPF9.Core
{
    public sealed class RpfArchive : IRpfArchive
    {
        private readonly MemoryMappedFile _mmf;
        private readonly MemoryMappedViewAccessor _accessor;
        private readonly FileStream _stream;
        private readonly byte[] _keyAes;
        private readonly byte[] _keyNg;
        private bool _disposed;

        public RpfArchiveInfo Info { get; private set; }

        private RpfArchive(string path, MemoryMappedFile mmf, MemoryMappedViewAccessor accessor, FileStream stream, RpfArchiveInfo info, byte[] keyAes, byte[] keyNg)
        {
            _mmf = mmf;
            _accessor = accessor;
            _stream = stream;
            Info = info;
            _keyAes = keyAes;
            _keyNg = keyNg;
        }

        public static RpfArchive Open(string path, byte[] aesKey = null, byte[] ngKey = null)
        {
            if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("Path is required.", "path");
            string fullPath = Path.GetFullPath(path);
            FileInfo fileInfo = new FileInfo(fullPath);
            if (!fileInfo.Exists) throw new FileNotFoundException("RPF archive not found.", fullPath);

            FileStream stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.RandomAccess);
            MemoryMappedFile mmf = MemoryMappedFile.CreateFromFile(stream, null, 0, MemoryMappedFileAccess.Read, HandleInheritability.None, true);
            MemoryMappedViewAccessor accessor = mmf.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);
            try
            {
                RpfArchiveInfo info = RpfParser.Parse(fullPath, fileInfo.Length, accessor, (aesKey == null ? new byte[0] : aesKey), (ngKey == null ? new byte[0] : ngKey));
                return new RpfArchive(fullPath, mmf, accessor, stream, info, (aesKey == null ? new byte[0] : aesKey), (ngKey == null ? new byte[0] : ngKey));
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
            Stack<RpfDirectoryEntry> stack = new Stack<RpfDirectoryEntry>();
            stack.Push(Info.Root);
            while (stack.Count > 0)
            {
                RpfDirectoryEntry dir = stack.Pop();
                foreach (RpfFileEntry file in dir.Files) yield return file;
                for (int i = dir.Directories.Count - 1; i >= 0; i--) stack.Push(dir.Directories[i]);
            }
        }

        public byte[] ReadFile(RpfFileEntry entry)
        {
            if (entry == null) throw new ArgumentNullException("entry");
            byte[] data = ReadRaw(entry);
            return DecodeFileData(entry, data);
        }

        public Stream OpenRead(RpfFileEntry entry)
        {
            return new MemoryStream(ReadFile(entry), false);
        }

        public void ExtractFile(RpfFileEntry entry, string outputPath)
        {
            if (entry == null) throw new ArgumentNullException("entry");
            if (string.IsNullOrWhiteSpace(outputPath)) throw new ArgumentException("outputPath");
            string dir = Path.GetDirectoryName(outputPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            byte[] data = ReadFile(entry);
            File.WriteAllBytes(outputPath, data);
        }

        private byte[] ReadRaw(RpfFileEntry entry)
        {
            long offset = (long)entry.FileOffset * 512;
            if (offset < 0 || offset + entry.FileSize > Info.FileSize)
                throw new RpfFormatException("File entry '" + entry.FullPath + "' points outside archive bounds.");
            byte[] buffer = new byte[entry.FileSize];
            _accessor.ReadArray(offset, buffer, 0, buffer.Length);
            return buffer;
        }

        private byte[] DecodeFileData(RpfFileEntry entry, byte[] data)
        {
            byte[] decrypted = data;
            if (entry.EncryptionType == 0) decrypted = data;
            else if (entry.EncryptionType == 1 && Info.Header.Encryption == RpfEncryption.Aes) decrypted = RpfCrypto.DecryptAes(data, _keyAes);
            else if (entry.EncryptionType == 1 && Info.Header.Encryption == RpfEncryption.Ng) decrypted = RpfCrypto.DecryptNg(data, entry.Name, entry.FileSize, _keyNg);
            else if (entry.EncryptionType == 1 && _keyAes.Length == 32) decrypted = RpfCrypto.DecryptAes(data, _keyAes);
            else if (entry.EncryptionType == 1 && _keyNg.Length > 0) decrypted = RpfCrypto.DecryptNg(data, entry.Name, entry.FileSize, _keyNg);

            if (entry.FileUncompressedSize != 0 && entry.FileUncompressedSize != entry.FileSize && decrypted.Length > 0)
            {
                try { return RpfCompression.Decompress(decrypted, (int)entry.FileUncompressedSize); }
                catch { return decrypted; }
            }
            return decrypted;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _accessor.Dispose();
            _mmf.Dispose();
            _stream.Dispose();
        }
    }
}
