using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace OpenRPF9
{
    internal static class R9TxdFile
    {
        public const string Extension = ".r9txd";

        private const int HeaderSize = 0x40;
        private const int EntrySize = 0x40;
        private const uint Version = 1;
        private const uint FlagIndex = 1;
        private const uint FlagImages = 2;
        private const uint FlagSegments = 4;
        private const int MaxNameLength = 200;

        private static readonly byte[] Magic = new byte[]
        {
            (byte)'R', (byte)'9', (byte)'T', (byte)'X', (byte)'D', 0x0D, 0x0A, 0x1A
        };

        public static bool CanBuild(FileEntry entry)
        {
            if (entry == null || entry.Data == null || entry.TextureDict == null) return false;
            if (entry.TextureDict.Textures == null || entry.TextureDict.Textures.Count == 0) return false;
            int systemLength = entry.TextureSystemBytes;
            if (systemLength <= 0 || systemLength >= entry.Data.Length) return false;
            return entry.TextureDict.Textures.Count <= 65535;
        }

        public static bool TryBuild(FileEntry entry, out byte[] file)
        {
            file = null;
            if (!CanBuild(entry)) return false;

            int systemLength = entry.TextureSystemBytes;
            byte[] system = new byte[systemLength];
            byte[] graphics = new byte[entry.Data.Length - systemLength];
            Buffer.BlockCopy(entry.Data, 0, system, 0, system.Length);
            Buffer.BlockCopy(entry.Data, systemLength, graphics, 0, graphics.Length);

            List<RageTextureInfo> textures = entry.TextureDict.Textures;
            int count = textures.Count;
            byte[][] images = new byte[count][];
            bool anyImage = false;
            for (int i = 0; i < count; i++)
            {
                try
                {
                    images[i] = RageTextureTools.CreateDds(system, graphics, i);
                }
                catch
                {
                    images[i] = null;
                }
                if (images[i] != null && images[i].Length > 0) anyImage = true;
            }

            string[] names = new string[count];
            int[] nameOffsets = new int[count];
            int namesLength = 0;
            for (int i = 0; i < count; i++)
            {
                string name = textures[i] == null ? "" : (textures[i].Name ?? "");
                if (textures[i] != null && textures[i].HasAssetKey) name = "";
                if (name.Length > MaxNameLength) name = name.Substring(0, MaxNameLength);
                names[i] = name;
                nameOffsets[i] = name.Length > 0 ? namesLength : 0;
                if (name.Length > 0) namesLength += Encoding.UTF8.GetByteCount(name) + 1;
            }

            long indexOffset = HeaderSize;
            long indexLength = (long)count * EntrySize;
            long namesOffset = indexOffset + indexLength;
            long imagesOffset = namesOffset + namesLength;
            long[] imageOffsets = new long[count];
            long imagesLength = 0;
            for (int i = 0; i < count; i++)
            {
                if (images[i] == null || images[i].Length == 0)
                {
                    imageOffsets[i] = 0;
                    continue;
                }
                imageOffsets[i] = imagesOffset + imagesLength;
                imagesLength += images[i].Length;
            }
            long segmentOffset = imagesOffset + imagesLength;
            long total = segmentOffset + system.Length + graphics.Length;
            if (total > int.MaxValue) return false;

            using (MemoryStream output = new MemoryStream((int)total))
            {
                output.Write(Magic, 0, Magic.Length);
                WriteU32(output, Version);
                WriteU32(output, FlagIndex | FlagSegments | (anyImage ? FlagImages : 0));
                WriteU32(output, (uint)count);
                WriteU32(output, (uint)indexOffset);
                WriteU32(output, (uint)indexLength);
                WriteU32(output, (uint)namesLength);
                WriteU32(output, (uint)imagesOffset);
                WriteU32(output, (uint)imagesLength);
                WriteU32(output, (uint)system.Length);
                WriteU32(output, (uint)graphics.Length);
                WriteU32(output, (uint)segmentOffset);
                for (int i = 0; i < 12; i++) output.WriteByte(0);

                for (int i = 0; i < count; i++)
                {
                    RageTextureInfo info = textures[i];
                    WriteU32(output, (uint)nameOffsets[i]);
                    WriteU32(output, info == null ? 0u : info.NameHash);
                    WriteU16(output, info == null ? (ushort)0 : (ushort)info.Width);
                    WriteU16(output, info == null ? (ushort)0 : (ushort)info.Height);
                    WriteU16(output, info == null ? (ushort)0 : (ushort)info.Depth);
                    output.WriteByte(info == null ? (byte)0 : (byte)info.Dimension);
                    output.WriteByte(info == null ? (byte)0 : (byte)info.DxgiFormat);
                    output.WriteByte(info == null ? (byte)0 : (byte)info.MipCount);
                    output.WriteByte(info == null ? (byte)0 : (byte)info.TileMode);
                    WriteU16(output, 0);
                    WriteU32(output, (uint)imageOffsets[i]);
                    WriteU32(output, images[i] == null ? 0u : (uint)images[i].Length);
                    long gfxOffset = segmentOffset + system.Length + (info == null ? 0 : info.GraphicsOffset);
                    WriteU32(output, (uint)gfxOffset);
                    WriteU32(output, info == null ? 0u : (uint)info.AllocationBytes);
                    WriteU64(output, info != null && info.HasAssetKey ? info.AssetKey : 0UL);
                    uint flags = 0;
                    if (info != null && info.HasAssetKey) flags |= 1;
                    if (images[i] != null && images[i].Length > 0) flags |= 2;
                    WriteU32(output, flags);
                    WriteU32(output, (uint)i);
                    WriteU32(output, 0);
                    WriteU32(output, 0);
                    WriteU32(output, 0);
                }

                for (int i = 0; i < count; i++)
                {
                    if (names[i].Length == 0) continue;
                    byte[] bytes = Encoding.UTF8.GetBytes(names[i]);
                    output.Write(bytes, 0, bytes.Length);
                    output.WriteByte(0);
                }
                for (int i = 0; i < count; i++)
                {
                    if (images[i] == null || images[i].Length == 0) continue;
                    output.Write(images[i], 0, images[i].Length);
                }
                output.Write(system, 0, system.Length);
                output.Write(graphics, 0, graphics.Length);
                file = output.ToArray();
            }
            return true;
        }

        private static void WriteU16(Stream stream, ushort value)
        {
            stream.WriteByte((byte)value);
            stream.WriteByte((byte)(value >> 8));
        }

        private static void WriteU32(Stream stream, uint value)
        {
            stream.WriteByte((byte)value);
            stream.WriteByte((byte)(value >> 8));
            stream.WriteByte((byte)(value >> 16));
            stream.WriteByte((byte)(value >> 24));
        }

        private static void WriteU64(Stream stream, ulong value)
        {
            WriteU32(stream, (uint)value);
            WriteU32(stream, (uint)(value >> 32));
        }
    }
}
