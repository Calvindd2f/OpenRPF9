using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.IO.Compression;

namespace OpenRPF9
{
    internal sealed class TtpfTexture
    {
        public int Index;
        public int Width;
        public int Height;
        public int Depth;
        public int Dimension;
        public int MipCount;
        public int NonTail;
        public int Mode;
        public int Format;
        public long BaseOffset;
        public long AllocatedBytes;
        public string FormatName = "";
        public int ElementBytes;
        public bool BlockCompressed;
        public string Problem;

        public bool IsTwoD
        {
            get { return Dimension == 1 && Depth == 1; }
        }

        public int TileBytes
        {
            get { return ((Mode >> 1) & 0xF) == 0 ? 65536 : 4096; }
        }

        public string Shape
        {
            get
            {
                if (Dimension == 2) return "cubemap";
                if (Dimension == 3) return "3D volume";
                if (Depth > 1) return "2D array";
                return "2D";
            }
        }
    }

    internal sealed class TtpfChunk
    {
        public long FileStart;
        public long FileLength;
        public long VirtualStart;
        public long VirtualLength;
        public int Codec;
    }

    internal sealed class TtpfMip
    {
        public int Index;
        public int BlocksW;
        public int BlocksH;
        public int PaddedW;
        public int PaddedH;
        public int PixelW;
        public int PixelH;
        public int TileX;
        public int TileY;
        public long Offset;
        public long Region;
        public bool InTail;
    }

    internal sealed class TtpfLayout
    {
        public int Tile;
        public int BlockW;
        public int BlockH;
        public int FirstTail;
        public int ElementBytes;
        public int MipCount;
        public long Size;
        public List<TtpfMip> Mips = new List<TtpfMip>();
    }

    internal sealed class TtpfFile
    {
        private const int MaxPreviewBytes = 256 * 1024 * 1024;
        private const int MaxCachedChunks = 8;

        private readonly object sync = new object();
        private readonly List<TtpfChunk> chunks = new List<TtpfChunk>();
        private readonly Dictionary<long, byte[]> cache = new Dictionary<long, byte[]>();
        private string path;
        private long virtualSize;

        public List<TtpfTexture> Textures = new List<TtpfTexture>();
        public string Path { get { return path; } }
        public long VirtualSize { get { return virtualSize; } }

        public static bool HasMagic(byte[] data, int length)
        {
            return data != null && length >= 4 && data[0] == (byte)'R' && data[1] == (byte)'D' && data[2] == (byte)'S' && data[3] == (byte)'5';
        }

        public static TtpfFile Open(string filePath)
        {
            TtpfFile file = new TtpfFile();
            file.path = filePath;
            using (FileStream fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                long fileLength = fs.Length;
                byte[] head = ReadExact(fs, 0, 24);
                if (!HasMagic(head, 24)) throw new InvalidDataException("Not an RDS5 TTPF file.");
                uint count = BitConverter.ToUInt32(head, 4);
                ulong sizeWord = BitConverter.ToUInt64(head, 8);
                file.virtualSize = (long)(sizeWord & 0x00FFFFFFFFFFFFFFUL);
                uint pageCount = BitConverter.ToUInt32(head, 16);
                if (count < 3 || count > 1000002) throw new InvalidDataException("Unsupported RDS5 chunk count " + count + ".");
                uint expectedPages = (count + 31) / 32;
                if (pageCount != expectedPages && pageCount != expectedPages + 1) throw new InvalidDataException("Unsupported RDS5 index layout.");
                if (file.virtualSize < 4096) throw new InvalidDataException("RDS5 virtual size is invalid.");
                int chunkCount = (int)count - 2;
                long tableEnd = 24 + ((long)count - 1) * 8;
                if (tableEnd > fileLength - 16) throw new InvalidDataException("RDS5 index exceeds the file size.");
                long etpf = Align(tableEnd, 256);
                byte[] table = ReadExact(fs, 24, (int)(tableEnd - 24));
                ulong sentinel = BitConverter.ToUInt64(table, table.Length - 8);
                if ((long)(sentinel & 0x3FFFFFFUL) * 256 != fileLength) throw new InvalidDataException("RDS5 index does not match the file length.");
                if ((long)((sentinel >> 30) & 0x3FFFFFUL) * 4096 != file.virtualSize) throw new InvalidDataException("RDS5 index does not match the virtual size.");

                for (int i = 0; i < chunkCount; i++)
                {
                    ulong v = BitConverter.ToUInt64(table, i * 8);
                    ulong next = BitConverter.ToUInt64(table, (i + 1) * 8);
                    long start = (long)(v & 0x3FFFFFFUL) * 256;
                    long end = (long)(next & 0x3FFFFFFUL) * 256;
                    long virt = (long)((v >> 30) & 0x3FFFFFUL) * 4096;
                    long vend = (long)((next >> 30) & 0x3FFFFFUL) * 4096;
                    long pad = (long)(v >> 52);
                    int codec = (int)((v >> 26) & 15);
                    long packed = end - start - pad;
                    long virtualLength = vend - virt;
                    if (packed < 1 || virtualLength < 1 || end > fileLength || vend > file.virtualSize) continue;
                    TtpfChunk chunk = new TtpfChunk();
                    chunk.FileStart = start;
                    chunk.FileLength = packed;
                    chunk.VirtualStart = virt;
                    chunk.VirtualLength = virtualLength;
                    chunk.Codec = codec;
                    file.chunks.Add(chunk);
                }
                if (file.chunks.Count == 0) throw new InvalidDataException("RDS5 chunk table has no readable chunks.");

                byte[] etpfHeader = ReadExact(fs, etpf, 16);
                if (etpfHeader[0] != (byte)'E' || etpfHeader[1] != (byte)'T' || etpfHeader[2] != (byte)'P' || etpfHeader[3] != (byte)'F')
                    throw new InvalidDataException("TTPF has no ETPF header at 0x" + etpf.ToString("X") + ".");
                uint version = BitConverter.ToUInt32(etpfHeader, 4);
                uint textureCount = BitConverter.ToUInt32(etpfHeader, 8);
                uint area = BitConverter.ToUInt32(etpfHeader, 12);
                if (version != 634) throw new InvalidDataException("Unsupported ETPF version " + version + ".");
                if (textureCount < 1 || textureCount > 250000) throw new InvalidDataException("ETPF texture count " + textureCount + " is invalid.");
                if (area < textureCount * 16 || (long)area > fileLength) throw new InvalidDataException("ETPF record table is invalid.");
                byte[] records = ReadExact(fs, etpf + 16, (int)(textureCount * 16));

                for (int i = 0; i < textureCount; i++)
                {
                    uint u0 = BitConverter.ToUInt32(records, i * 16);
                    uint u1 = BitConverter.ToUInt32(records, i * 16 + 4);
                    uint u2 = BitConverter.ToUInt32(records, i * 16 + 8);
                    TtpfTexture t = new TtpfTexture();
                    t.Index = i;
                    t.Width = (int)(u0 & 0x3FFF) + 1;
                    t.Height = (int)((u0 >> 14) & 0x3FFF) + 1;
                    t.NonTail = (int)(u1 & 7);
                    t.MipCount = (int)(u0 >> 28) + t.NonTail;
                    t.Format = (int)((u1 >> 3) & 0x7F);
                    t.Depth = (int)((u1 >> 10) & 0x7FF) + 1;
                    t.Dimension = (int)((u1 >> 21) & 3);
                    t.Mode = (int)(u1 >> 23);
                    t.BaseOffset = (long)(u2 & 0x1FFFFF) * 4096;
                    t.FormatName = FormatName(t.Format);
                    t.ElementBytes = ElementBytes(t.Format);
                    t.BlockCompressed = IsBlockCompressed(t.Format);
                    file.Textures.Add(t);
                }
            }
            file.ResolveSpans();
            return file;
        }

        private void ResolveSpans()
        {
            List<long> bases = new List<long>(Textures.Count);
            for (int i = 0; i < Textures.Count; i++)
            {
                long b = Textures[i].BaseOffset;
                if (!IsUnmapped(b)) bases.Add(b);
            }
            bases.Sort();
            List<long> unique = new List<long>(bases.Count);
            for (int i = 0; i < bases.Count; i++)
            {
                if (unique.Count == 0 || unique[unique.Count - 1] != bases[i]) unique.Add(bases[i]);
            }
            for (int i = 0; i < Textures.Count; i++)
            {
                TtpfTexture t = Textures[i];
                if (IsUnmapped(t.BaseOffset))
                {
                    t.Problem = "no mapped allocation";
                    continue;
                }
                long next = virtualSize;
                int lo = 0, hi = unique.Count;
                while (lo < hi)
                {
                    int mid = (lo + hi) / 2;
                    if (unique[mid] <= t.BaseOffset) lo = mid + 1; else hi = mid;
                }
                if (lo < unique.Count) next = unique[lo];
                t.AllocatedBytes = Math.Max(0, next - t.BaseOffset);
                if (!t.IsTwoD)
                {
                    t.Problem = t.Shape + " textures are listed but not previewable";
                    continue;
                }
                if (t.ElementBytes <= 0)
                {
                    t.Problem = "unsupported DXGI format " + t.Format;
                    continue;
                }
                TtpfLayout layout = ComputeLayout(t);
                if (layout == null)
                {
                    t.Problem = "unsupported texture layout";
                    continue;
                }
                if (t.AllocatedBytes > 0 && t.AllocatedBytes != layout.Size)
                    t.Problem = "layout does not match the allocation span";
            }
        }

        private static bool IsUnmapped(long baseOffset)
        {
            return baseOffset == 0 || (baseOffset >> 12) == 0x1FFFFF;
        }

        public byte[] ReadVirtual(long offset, int length)
        {
            if (length <= 0) return new byte[0];
            byte[] output = new byte[length];
            long end = offset + length;
            bool any = false;
            for (int i = 0; i < chunks.Count; i++)
            {
                TtpfChunk c = chunks[i];
                long lo = Math.Max(offset, c.VirtualStart);
                long hi = Math.Min(end, c.VirtualStart + c.VirtualLength);
                if (hi <= lo) continue;
                byte[] data = GetChunkData(c);
                int src = (int)(lo - c.VirtualStart);
                int count = (int)(hi - lo);
                if (src >= data.Length) continue;
                if (src + count > data.Length) count = data.Length - src;
                Buffer.BlockCopy(data, src, output, (int)(lo - offset), count);
                any = true;
            }
            if (!any) throw new InvalidDataException("Texture data is not mapped in the RDS5 chunk table.");
            return output;
        }

        private byte[] GetChunkData(TtpfChunk chunk)
        {
            lock (sync)
            {
                byte[] cached;
                if (cache.TryGetValue(chunk.FileStart, out cached)) return cached;
                if (chunk.FileLength < 1 || chunk.FileLength > int.MaxValue) throw new InvalidDataException("TTPF chunk size is invalid.");
                byte[] stored;
                using (FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    stored = ReadExact(fs, chunk.FileStart, (int)chunk.FileLength);
                }
                byte[] data;
                if (chunk.Codec == 0)
                {
                    data = stored;
                }
                else if (chunk.Codec == 8)
                {
                    data = Inflate(stored, chunk.VirtualLength);
                }
                else
                {
                    throw new InvalidDataException("Texture uses Xbox BCPack (" + CodecName(chunk.Codec) + "), which cannot be decoded on PC.");
                }
                if (cache.Count >= MaxCachedChunks) cache.Clear();
                cache[chunk.FileStart] = data;
                return data;
            }
        }

        private static byte[] Inflate(byte[] stored, long expected)
        {
            if (stored.Length < 3) throw new InvalidDataException("zlib chunk is truncated.");
            using (MemoryStream input = new MemoryStream(stored, 2, stored.Length - 2, false))
            using (DeflateStream zlib = new DeflateStream(input, CompressionMode.Decompress))
            using (MemoryStream output = new MemoryStream(expected > 0 && expected < int.MaxValue ? (int)expected : 0))
            {
                byte[] buffer = new byte[65536];
                int n;
                while ((n = zlib.Read(buffer, 0, buffer.Length)) > 0) output.Write(buffer, 0, n);
                return output.ToArray();
            }
        }

        private static string CodecName(int codec)
        {
            switch (codec)
            {
                case 1: return "BC1";
                case 2: return "BC2";
                case 3: return "BC3";
                case 4: return "BC4";
                case 5: return "BC5";
                case 6: return "BC6H";
                case 7: return "BC7";
                default: return "codec " + codec;
            }
        }

        public string Describe(TtpfTexture t)
        {
            if (t == null) return "";
            System.Text.StringBuilder sb = new System.Text.StringBuilder();
            sb.AppendLine("TTPF (RDS5/ETPF) texture #" + t.Index);
            sb.AppendLine("Size: " + t.Width + "x" + t.Height + (t.Depth > 1 ? "x" + t.Depth : ""));
            sb.AppendLine("Format: " + t.FormatName + " (DXGI " + t.Format + ")");
            sb.AppendLine("Shape: " + t.Shape);
            sb.AppendLine("Mips: " + t.MipCount + " (packed tail " + t.NonTail + ")");
            sb.AppendLine("Mode: " + t.Mode + " (" + (t.TileBytes / 1024) + " KB tiles)");
            sb.AppendLine("Allocation: 0x" + t.BaseOffset.ToString("X") + " span " + t.AllocatedBytes + " bytes");
            sb.AppendLine("Chunks: " + chunks.Count + " | virtual " + virtualSize + " bytes");
            if (!string.IsNullOrEmpty(t.Problem)) sb.AppendLine("Note: " + t.Problem);
            return sb.ToString();
        }

        public byte[] RenderPng(TtpfTexture t, out string error)
        {
            error = "";
            byte[] dds;
            int outWidth, outHeight;
            if (!RenderDds(t, out dds, out outWidth, out outHeight, out error)) return null;
            try
            {
                string info;
                using (Bitmap bmp = DdsDecoder.DecodeToBitmap(dds, out info))
                using (MemoryStream ms = new MemoryStream())
                {
                    bmp.Save(ms, ImageFormat.Png);
                    error = info;
                    return ms.ToArray();
                }
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return null;
            }
        }

        public bool RenderDds(TtpfTexture t, out byte[] dds, out int width, out int height, out string error)
        {
            dds = null;
            width = 0;
            height = 0;
            error = "";
            if (t == null) { error = "no texture"; return false; }
            if (!string.IsNullOrEmpty(t.Problem)) { error = t.Problem; return false; }
            TtpfLayout layout = ComputeLayout(t);
            if (layout == null) { error = "unsupported layout"; return false; }
            Exception failure = null;
            for (int i = 0; i < layout.Mips.Count; i++)
            {
                TtpfMip mip = layout.Mips[i];
                if (mip.Region <= 0 || mip.Region > MaxPreviewBytes) continue;
                try
                {
                    byte[] tiled = ReadVirtual(t.BaseOffset + mip.Offset, (int)mip.Region);
                    byte[] linear = Detile(layout, mip, tiled, t.ElementBytes);
                    width = mip.PixelW;
                    height = mip.PixelH;
                    dds = BuildDds(width, height, t.Format, t.ElementBytes, t.BlockCompressed, linear);
                    if (i > 0) error = "showing mip " + i;
                    return true;
                }
                catch (Exception ex)
                {
                    if (failure == null) failure = ex;
                }
            }
            error = failure == null ? "no readable mip level" : failure.Message;
            width = 0;
            height = 0;
            dds = null;
            return false;
        }

        private static TtpfLayout ComputeLayout(TtpfTexture t)
        {
            int log = ElementLog(t.ElementBytes);
            if (log < 0) return null;
            int mips = t.MipCount;
            int block = t.BlockCompressed ? 4 : 1;
            int levels = BitLength(Math.Max(t.Width, t.Height));
            if (mips < 1 || mips > levels) return null;
            int tile = t.TileBytes;
            int halfBits = tile == 4096 ? 6 : 8;
            int maxTail = tile == 4096 ? 8 : 12;
            int bw = 1 << (halfBits - log / 2);
            int bh = 1 << (halfBits - (log + 1) / 2);
            int mw = OriginX(log);
            int mh = OriginY(log);
            int ew = (t.Width + block - 1) / block;
            int eh = (t.Height + block - 1) / block;
            int tail = mips;
            TtpfLayout layout = new TtpfLayout();
            layout.Tile = tile;
            layout.BlockW = bw;
            layout.BlockH = bh;
            layout.ElementBytes = t.ElementBytes;
            layout.MipCount = mips;
            TtpfMip[] result = new TtpfMip[mips];
            long[] sizes = new long[mips];
            for (int i = 0; i < mips; i++)
            {
                int wc = Math.Max(1, (ew + (1 << i) - 1) >> i);
                int hc = Math.Max(1, (eh + (1 << i) - 1) >> i);
                if (mips > 1 && wc <= bw / 2 && hc <= bh && mips - i <= maxTail) { tail = i; break; }
                TtpfMip mip = new TtpfMip();
                mip.Index = i;
                mip.BlocksW = wc;
                mip.BlocksH = hc;
                mip.PaddedW = RoundUp(wc, bw);
                mip.PaddedH = RoundUp(hc, bh);
                mip.PixelW = Math.Max(1, t.Width >> i);
                mip.PixelH = Math.Max(1, t.Height >> i);
                mip.Region = (long)mip.PaddedW * mip.PaddedH * t.ElementBytes;
                result[i] = mip;
                sizes[i] = mip.Region;
            }
            long offset = tail < mips ? tile : 0;
            for (int i = tail - 1; i >= 0; i--)
            {
                result[i].Offset = offset;
                offset += sizes[i];
            }
            for (int i = tail; i < mips; i++)
            {
                int entry = maxTail - 1 - (i - tail);
                int moff = entry > 6 ? (16 << entry) : (entry << 8);
                int x = 0, y = 0;
                for (int bit = 0; bit < 6; bit++)
                {
                    x |= ((moff >> (9 + 2 * bit)) & 1) << bit;
                    y |= ((moff >> (8 + 2 * bit)) & 1) << bit;
                }
                int wc = Math.Max(1, (ew + (1 << i) - 1) >> i);
                int hc = Math.Max(1, (eh + (1 << i) - 1) >> i);
                TtpfMip mip = new TtpfMip();
                mip.Index = i;
                mip.BlocksW = wc;
                mip.BlocksH = hc;
                mip.PaddedW = bw;
                mip.PaddedH = bh;
                mip.PixelW = Math.Max(1, t.Width >> i);
                mip.PixelH = Math.Max(1, t.Height >> i);
                mip.Region = tile;
                mip.TileX = x * mw;
                mip.TileY = y * mh;
                mip.InTail = true;
                result[i] = mip;
            }
            for (int i = 0; i < mips; i++)
            {
                if (result[i] == null) return null;
                layout.Mips.Add(result[i]);
            }
            layout.FirstTail = tail;
            layout.Size = offset;
            return layout;
        }

        private static readonly sbyte[][] Patterns4K = new sbyte[][]
        {
            new sbyte[] { 1, 2, 3, 4, -1, -2, -3, -4, -5, 5, -6, 6 },
            new sbyte[] { 0, 1, 2, 3, -1, -2, -3, 4, -4, 5, -5, 6 },
            new sbyte[] { 0, 0, 1, 2, -1, -2, -3, 3, -4, 4, -5, 5 },
            new sbyte[] { 0, 0, 0, 1, -1, -2, 2, 3, -3, 4, -4, 5 },
            new sbyte[] { 0, 0, 0, 0, -1, -2, 1, 2, -3, 3, -4, 4 }
        };

        private static readonly sbyte[][] Patterns64K = new sbyte[][]
        {
            new sbyte[] { 1, 2, 3, -2, -1, -3, 4, -4, -5, 5, -6, 6, -7, 7, -8, 8 },
            new sbyte[] { 0, 1, 2, 3, -1, -2, -3, 4, -4, 5, -5, 6, -6, 7, -7, 8 },
            new sbyte[] { 0, 0, 1, 2, -1, -2, 3, -3, -4, 4, -5, 5, -6, 6, -7, 7 },
            new sbyte[] { 0, 0, 0, 1, -1, 2, 3, -2, -3, 4, -4, 5, -5, 6, -6, 7 },
            new sbyte[] { 0, 0, 0, 0, 1, -1, 2, -2, -3, 3, -4, 4, -5, 5, -6, 6 }
        };

        private static byte[] Detile(TtpfLayout layout, TtpfMip mip, byte[] tiled, int elementBytes)
        {
            int log = ElementLog(elementBytes);
            if (log < 0) throw new InvalidDataException("unsupported element size " + elementBytes);
            sbyte[] pattern = (layout.Tile == 4096 ? Patterns4K : Patterns64K)[log];
            if (layout.BlockW <= 0 || layout.BlockH <= 0) throw new InvalidDataException("invalid tile geometry");
            int rowBytes = mip.BlocksW * elementBytes;
            byte[] output = new byte[mip.BlocksW * mip.BlocksH * elementBytes];
            int tilesX = mip.PaddedW / layout.BlockW;
            for (int y = 0; y < mip.BlocksH; y++)
            {
                int yTile = (y / layout.BlockH) * tilesX;
                for (int x = 0; x < mip.BlocksW; x++)
                {
                    long source = (long)(yTile + x / layout.BlockW) * layout.Tile;
                    for (int bit = 0; bit < pattern.Length; bit++)
                    {
                        int s = pattern[bit];
                        if (s > 0) source |= (long)(((x + mip.TileX) >> (s - 1)) & 1) << bit;
                        else if (s < 0) source |= (long)(((y + mip.TileY) >> (-s - 1)) & 1) << bit;
                    }
                    int destination = y * rowBytes + x * elementBytes;
                    if (source < 0 || source + elementBytes > tiled.Length) continue;
                    Buffer.BlockCopy(tiled, (int)source, output, destination, elementBytes);
                }
            }
            return output;
        }

        private static byte[] BuildDds(int width, int height, int dxgi, int elementBytes, bool blockCompressed, byte[] payload)
        {
            byte[] dds = new byte[148 + payload.Length];
            dds[0] = (byte)'D'; dds[1] = (byte)'D'; dds[2] = (byte)'S'; dds[3] = (byte)' ';
            WriteI32(dds, 4, 124);
            WriteU32(dds, 8, 0x00081007u);
            WriteI32(dds, 12, height);
            WriteI32(dds, 16, width);
            int linear = blockCompressed
                ? ((width + 3) / 4) * ((height + 3) / 4) * elementBytes
                : width * elementBytes;
            WriteI32(dds, 20, linear);
            WriteI32(dds, 28, 1);
            WriteI32(dds, 76, 32);
            WriteU32(dds, 80, 4u);
            dds[84] = (byte)'D'; dds[85] = (byte)'X'; dds[86] = (byte)'1'; dds[87] = (byte)'0';
            WriteU32(dds, 108, 0x1000u);
            WriteI32(dds, 128, dxgi);
            WriteI32(dds, 132, 3);
            WriteI32(dds, 136, 0);
            WriteI32(dds, 140, 1);
            WriteI32(dds, 144, 0);
            Buffer.BlockCopy(payload, 0, dds, 148, payload.Length);
            return dds;
        }

        private static void WriteI32(byte[] data, int offset, int value)
        {
            data[offset] = (byte)value;
            data[offset + 1] = (byte)(value >> 8);
            data[offset + 2] = (byte)(value >> 16);
            data[offset + 3] = (byte)(value >> 24);
        }

        private static void WriteU32(byte[] data, int offset, uint value)
        {
            data[offset] = (byte)value;
            data[offset + 1] = (byte)(value >> 8);
            data[offset + 2] = (byte)(value >> 16);
            data[offset + 3] = (byte)(value >> 24);
        }

        private static byte[] ReadExact(FileStream fs, long offset, int length)
        {
            byte[] buffer = new byte[length];
            fs.Position = offset;
            int total = 0;
            while (total < length)
            {
                int n = fs.Read(buffer, total, length - total);
                if (n <= 0) throw new InvalidDataException("Unexpected end of TTPF data.");
                total += n;
            }
            return buffer;
        }

        private static int OriginX(int log)
        {
            return log == 0 ? 16 : (log == 1 ? 16 : (log == 2 ? 8 : (log == 3 ? 8 : 4)));
        }

        private static int OriginY(int log)
        {
            return log == 0 ? 16 : (log == 1 ? 8 : (log == 2 ? 8 : (log == 3 ? 4 : 4)));
        }

        private static int ElementLog(int bytes)
        {
            if (bytes == 1) return 0;
            if (bytes == 2) return 1;
            if (bytes == 4) return 2;
            if (bytes == 8) return 3;
            if (bytes == 16) return 4;
            return -1;
        }

        private static int BitLength(int value)
        {
            int bits = 0;
            while (value > 0) { bits++; value >>= 1; }
            return bits;
        }

        private static int RoundUp(int value, int alignment)
        {
            return (value + alignment - 1) / alignment * alignment;
        }

        private static long Align(long value, int alignment)
        {
            return (value + alignment - 1) / alignment * alignment;
        }

        public static bool IsBlockCompressed(int format)
        {
            return (format >= 70 && format <= 84) || (format >= 94 && format <= 99);
        }

        public static int ElementBytes(int format)
        {
            switch (format)
            {
                case 2: case 3: case 4: return 16;
                case 6: case 7: case 8: return 12;
                case 10: case 11: case 12: case 13: case 14: return 8;
                case 16: case 17: case 18: return 8;
                case 24: case 25: case 26: return 4;
                case 28: case 29: case 30: case 31: case 32: return 4;
                case 34: case 35: case 36: case 37: case 38: return 4;
                case 41: case 42: case 43: return 4;
                case 49: case 50: case 51: case 52: return 2;
                case 54: case 56: case 57: case 58: case 59: return 2;
                case 61: case 62: case 63: case 64: case 65: return 1;
                case 71: case 72: case 80: case 81: return 8;
                case 74: case 75: case 77: case 78: case 83: case 84: return 16;
                case 87: case 91: return 4;
                case 95: case 96: case 98: case 99: return 16;
                default: return 0;
            }
        }

        public static string FormatName(int format)
        {
            switch (format)
            {
                case 2: return "R32G32B32A32_FLOAT";
                case 3: return "R32G32B32A32_UINT";
                case 4: return "R32G32B32A32_SINT";
                case 10: return "R16G16B16A16_FLOAT";
                case 11: return "R16G16B16A16_UNORM";
                case 16: return "R32G32_FLOAT";
                case 24: return "R10G10B10A2_UNORM";
                case 28: return "R8G8B8A8_UNORM";
                case 29: return "R8G8B8A8_UNORM_SRGB";
                case 34: return "R16G16_FLOAT";
                case 41: return "R32_FLOAT";
                case 49: return "R8G8_UNORM";
                case 54: return "R16_FLOAT";
                case 56: return "R16_UNORM";
                case 61: return "R8_UNORM";
                case 65: return "A8_UNORM";
                case 71: return "BC1_UNORM";
                case 72: return "BC1_UNORM_SRGB";
                case 74: return "BC2_UNORM";
                case 75: return "BC2_UNORM_SRGB";
                case 77: return "BC3_UNORM";
                case 78: return "BC3_UNORM_SRGB";
                case 80: return "BC4_UNORM";
                case 81: return "BC4_SNORM";
                case 83: return "BC5_UNORM";
                case 84: return "BC5_SNORM";
                case 87: return "B8G8R8A8_UNORM";
                case 91: return "B8G8R8A8_UNORM_SRGB";
                case 95: return "BC6H_UF16";
                case 96: return "BC6H_SF16";
                case 98: return "BC7_UNORM";
                case 99: return "BC7_UNORM_SRGB";
                default: return "DXGI " + format;
            }
        }
    }
}
