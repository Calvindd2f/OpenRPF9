using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace OpenRPF9
{
    internal sealed class RageTextureInfo
    {
        public int Index;
        public string Name = "";
        public int Width;
        public int Height;
        public int DxgiFormat;
        public string FormatName = "";
        public int MipCount;
        public int GraphicsOffset;
        public int AllocationBytes;
        public int Depth = 1;
        public int Dimension = 1;
        public uint NameHash;
        public ulong AssetKey;
        public bool HasAssetKey;
        public int TileMode = 5;

        public int PreviewMipLevel
        {
            get
            {
                int level = 0;
                while ((long)Math.Max(1, Width >> level) * Math.Max(1, Height >> level) > RageTextureTools.MaxPreviewPixels &&
                       level + 1 < MipCount) level++;
                return level;
            }
        }

        public string Shape
        {
            get
            {
                if (Dimension == 3) return "3D volume (" + Depth + " slices)";
                if (Dimension == 2) return "Cube array (" + (Depth / 6) + " cubes)";
                if (Depth > 1) return "2D array (" + Depth + " slices)";
                return "2D";
            }
        }

        public string Describe()
        {
            string s = Name + "  " + Width + "x" + Height + " " + FormatName + " (" + DxgiFormat + ")";
            if (MipCount > 1) s += " mips=" + MipCount;
            if (Depth > 1 || Dimension != 1) s += " " + Shape;
            s += " gfx=0x" + GraphicsOffset.ToString("X") + " alloc=" + AllocationBytes;
            if (HasAssetKey) s += " key=" + AssetKey.ToString("X16");
            return s;
        }
    }

    internal sealed class RageTextureDictionary
    {
        public int SystemBytes;
        public int GraphicsBytes;
        public List<RageTextureInfo> Textures = new List<RageTextureInfo>();
    }

    internal sealed class RageTextureMip
    {
        public int Width;
        public int Height;
        public int PaddedWidth;
        public int Offset;
        public int TailX;
        public int TailY;
    }

    internal sealed class RageTextureLayout
    {
        public int Size;
        public int BlockWidth;
        public int BlockHeight;
        public int BlockDepth = 1;
        public int ZBlockStride;
        public bool Volume;
        public List<RageTextureMip> Mips = new List<RageTextureMip>();
    }

    internal static class RageTextureTools
    {
        public const int MaxSystemBytes = 16 * 1024 * 1024;
        public const int MaxGraphicsBytes = 256 * 1024 * 1024;
        public const int MaxPreviewPixels = 16 * 1024 * 1024;

        private static readonly int[] Pattern8 = new int[] { 0, 0, 0, 1, -1, -2, 2, 3, -3, 4, -4, 5 };
        private static readonly int[] Pattern16 = new int[] { 0, 0, 0, 0, -1, -2, 1, 2, -3, 3, -4, 4 };
        private static readonly int[] Pattern1 = new int[] { 1, 2, 3, 4, -1, -2, -3, -4, -5, 5, -6, 6 };
        private static readonly int[] Pattern2 = new int[] { 0, 1, 2, 3, -1, -2, -3, 4, -4, 5, -5, 6 };
        private static readonly int[] Pattern4 = new int[] { 0, 0, 1, 2, -1, -2, -3, 3, -4, 4, -5, 5 };

        private static readonly int[][] VolumePatterns = new int[][]
        {
            new int[] { 1, 2, 101, -1, 102, -2, 3, 103, -3, 4, 104, -4 },
            new int[] { 0, 1, 101, -1, 102, -2, 2, 103, -3, 3, 104, -4 },
            new int[] { 0, 0, 1, -1, 101, -2, 2, 102, -3, 3, 103, -4 },
            new int[] { 0, 0, 0, 1, 101, -1, 2, 102, -2, 3, 103, -3 },
            new int[] { 0, 0, 0, 0, 101, -1, 1, 102, -2, 2, 103, -3 }
        };


        private static void Check(int offset, int count, int length)
        {
            if (offset < 0 || count < 0 || offset > length || count > length - offset)
                throw new InvalidDataException("Resource range exceeds its section.");
        }

        private static ushort U16(byte[] d, int at)
        {
            Check(at, 2, d.Length);
            return (ushort)(d[at] | (d[at + 1] << 8));
        }

        private static uint U32(byte[] d, int at)
        {
            Check(at, 4, d.Length);
            return (uint)(d[at] | (d[at + 1] << 8) | (d[at + 2] << 16) | (d[at + 3] << 24));
        }

        private static ulong U64(byte[] d, int at)
        {
            Check(at, 8, d.Length);
            return BitConverter.ToUInt64(d, at);
        }

        private static bool IsSystemPointer(ulong v)
        {
            return v >= 0x50000000UL && v < 0x51000000UL;
        }

        private static int VirtualSystemOffset(ulong v)
        {
            if (!IsSystemPointer(v)) throw new InvalidDataException("Invalid system pointer.");
            return checked((int)(v - 0x50000000UL));
        }

        private static int Pointer(byte[] d, int at)
        {
            int off = VirtualSystemOffset(U64(d, at));
            Check(off, 1, d.Length);
            return off;
        }

        private static int Align(int value, int boundary)
        {
            return checked((value + boundary - 1) / boundary * boundary);
        }

        private static int Elements(int pixels, int mip, int blockPixels)
        {
            return (Math.Max(1, pixels >> mip) + blockPixels - 1) / blockPixels;
        }

        private static int ElementLog2(int eb)
        {
            if (eb == 1) return 0;
            if (eb == 2) return 1;
            if (eb == 4) return 2;
            if (eb == 8) return 3;
            if (eb == 16) return 4;
            throw new InvalidDataException("Invalid element size.");
        }

        public static bool IsBlockCompressed(int format)
        {
            return (format >= 70 && format <= 84) || (format >= 94 && format <= 99);
        }

        public static int ElementBytes(int format)
        {
            switch (format)
            {
                case 61: case 65: return 1;
                case 49: case 54: case 56: return 2;
                case 24: case 28: case 29: case 34: case 41:
                case 87: case 88: case 91: case 93: return 4;
                case 10: case 11: case 71: case 72: case 80: case 81: return 8;
                case 74: case 75: case 77: case 78: case 83: case 84:
                case 95: case 96: case 98: case 99: return 16;
                default: throw new InvalidDataException("Unsupported DXGI format " + format + ".");
            }
        }

        public static string FormatName(int format)
        {
            switch (format)
            {
                case 10: return "R16G16B16A16_FLOAT";
                case 11: return "R16G16B16A16_UNORM";
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
                case 87: return "B8G8R8A8_UNORM";
                case 88: return "B8G8R8X8_UNORM";
                case 91: return "B8G8R8A8_UNORM_SRGB";
                case 93: return "B8G8R8X8_UNORM_SRGB";
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
                case 95: return "BC6H_UF16";
                case 96: return "BC6H_SF16";
                case 98: return "BC7_UNORM";
                case 99: return "BC7_UNORM_SRGB";
                default: return "Unknown";
            }
        }


        public static bool LooksLikeDictionary(byte[] system)
        {
            if (system == null || system.Length < 64) return false;
            try
            {
                int count = U16(system, 0x38);
                return count > 0 && count <= 4096 &&
                       U16(system, 0x3A) >= count &&
                       U16(system, 0x28) == count &&
                       IsSystemPointer(U64(system, 0x20)) &&
                       IsSystemPointer(U64(system, 0x30));
            }
            catch
            {
                return false;
            }
        }

        public static bool TryGetResourcePageCounts(byte[] firstSystemPage, out int systemPages, out int graphicsPages)
        {
            systemPages = 0;
            graphicsPages = 0;
            try
            {
                if (!LooksLikeDictionary(firstSystemPage)) return false;
                int pageInfo = Pointer(firstSystemPage, 8);
                Check(pageInfo, 16, firstSystemPage.Length);
                systemPages = firstSystemPage[pageInfo + 8];
                graphicsPages = firstSystemPage[pageInfo + 9];
                return systemPages >= 1 && systemPages <= 32 && graphicsPages >= 1 && graphicsPages <= 32;
            }
            catch
            {
                return false;
            }
        }

        public static bool TryGetMinimumSystemBytes(byte[] partial, out int required)
        {
            required = 64;
            try
            {
                if (!LooksLikeDictionary(partial)) return false;
                int count = U16(partial, 0x38);
                int page = VirtualSystemOffset(U64(partial, 8));
                int hashes = VirtualSystemOffset(U64(partial, 0x20));
                int pointers = VirtualSystemOffset(U64(partial, 0x30));
                required = Math.Max(page + 16, Math.Max(hashes + count * 4, pointers + count * 8));
                if (required > MaxSystemBytes) return false;
                if (required > partial.Length) return true;
                for (int i = 0; i < count; i++)
                {
                    int t = VirtualSystemOffset(U64(partial, pointers + i * 8));
                    required = Math.Max(required, checked(t + 0x70));
                    if (required > MaxSystemBytes) return false;
                    if (t + 0x70 > partial.Length) continue;
                    int format = partial[t + 0x3F];
                    if (partial[t + 0x3E] < 1 || partial[t + 0x3E] > 3 || partial[t + 0x40] != 5 ||
                        U16(partial, t + 0x0C) != ElementBytes(format) ||
                        U64(partial, t + 0x28) != 0x50000000UL + (ulong)t + 0x50) return false;
                    int name = checked(VirtualSystemOffset(U64(partial, t + 0x30)) + 8);
                    required = Math.Max(required, name + 1);
                    if (name < partial.Length)
                    {
                        int limit = Math.Min(1024, partial.Length - name);
                        bool foundNull = false;
                        for (int k = 0; k < limit; k++) { if (partial[name + k] == 0) { foundNull = true; break; } }
                        if (!foundNull) required = Math.Max(required, name + 1024);
                    }
                }
                return required <= MaxSystemBytes;
            }
            catch
            {
                return false;
            }
        }

        public static bool TryGetRequiredGraphicsBytes(byte[] system, out int required)
        {
            required = 0;
            try
            {
                List<RageTextureInfo> textures = Parse(system, MaxGraphicsBytes);
                int max = 0;
                for (int i = 0; i < textures.Count; i++)
                {
                    int end = checked(textures[i].GraphicsOffset + textures[i].AllocationBytes);
                    if (end > max) max = end;
                }
                required = max;
                return required > 0;
            }
            catch
            {
                return false;
            }
        }


        public static RageTextureDictionary Inspect(byte[] system, byte[] graphics)
        {
            if (system == null || graphics == null) return null;
            try
            {
                List<RageTextureInfo> textures = Parse(system, graphics.Length);
                RageTextureDictionary dict = new RageTextureDictionary();
                dict.SystemBytes = system.Length;
                dict.GraphicsBytes = graphics.Length;
                dict.Textures = textures;
                return dict;
            }
            catch
            {
                return null;
            }
        }

        private static List<RageTextureInfo> Parse(byte[] system, int graphicsLength)
        {
            if (system.Length > MaxSystemBytes || graphicsLength > MaxGraphicsBytes || graphicsLength < 0 ||
                !LooksLikeDictionary(system))
                throw new InvalidDataException("Not a supported bounded texture dictionary.");

            int count = U16(system, 0x38);
            int hashes = Pointer(system, 0x20);
            int pointers = Pointer(system, 0x30);
            Check(hashes, checked(count * 4), system.Length);
            Check(pointers, checked(count * 8), system.Length);

            List<RageTextureInfo> result = new List<RageTextureInfo>(count);
            List<int> rangeStart = new List<int>();
            List<int> rangeEnd = new List<int>();

            for (int i = 0; i < count; i++)
            {
                int t = Pointer(system, pointers + i * 8);
                Check(t, 0x70, system.Length);
                int descriptor = Pointer(system, t + 0x30);
                Check(descriptor, 8, system.Length);

                ulong assetKey = 0;
                bool hasAssetKey = false;
                string name;

                if (U64(system, descriptor) == 0)
                {
                    int nameStart = checked(descriptor + 8);
                    Check(nameStart, 1, system.Length);
                    int limit = Math.Min(1024, system.Length - nameStart);
                    int end = -1;
                    for (int k = 0; k < limit; k++) { if (system[nameStart + k] == 0) { end = k; break; } }
                    if (end <= 0) throw new InvalidDataException("Invalid texture name.");
                    name = Encoding.UTF8.GetString(system, nameStart, end);
                    for (int c = 0; c < name.Length; c++)
                        if (char.IsControl(name[c])) throw new InvalidDataException("Invalid texture name characters.");
                }
                else
                {
                    Check(descriptor, 16, system.Length);
                    ulong marker = U64(system, descriptor);
                    ulong key = U64(system, descriptor + 8);
                    if (descriptor < 16 || marker != 0x50000000UL + (ulong)descriptor - 11 || key == 0 ||
                        U32(system, descriptor - 16) != 6 || U32(system, descriptor - 12) != 0 || U32(system, descriptor - 4) != 0)
                        throw new InvalidDataException("Unsupported keyed texture descriptor.");
                    assetKey = key;
                    hasAssetKey = true;
                    name = "texture_" + key.ToString("X16");
                }

                int width = U16(system, t + 0x38);
                int height = U16(system, t + 0x3A);
                int format = system[t + 0x3F];
                int eb = ElementBytes(format);
                int mips = system[t + 0x42];
                int depth = U16(system, t + 0x3C);
                int dimension = system[t + 0x3E];

                if (width < 1 || width > 16384 || height < 1 || height > 16384 ||
                    depth < 1 || depth > 2048 || dimension < 1 || dimension > 3 ||
                    (dimension == 2 && depth % 6 != 0) || system[t + 0x40] != 5 ||
                    U16(system, t + 0x0C) != eb ||
                    U64(system, t + 0x28) != 0x50000000UL + (ulong)t + 0x50)
                    throw new InvalidDataException("Unsupported texture descriptor.");

                ulong gfxPointer = U64(system, t + 0x18);
                if (gfxPointer < 0x60000000UL || gfxPointer - 0x60000000UL > int.MaxValue)
                    throw new InvalidDataException("Invalid graphics pointer.");
                int gfx = (int)(gfxPointer - 0x60000000UL);
                int length = checked((int)U32(system, t + 8) * eb);
                Check(gfx, length, graphicsLength);

                RageTextureInfo info = new RageTextureInfo();
                info.Index = i;
                info.Name = name;
                info.Width = width;
                info.Height = height;
                info.DxgiFormat = format;
                info.FormatName = FormatName(format);
                info.MipCount = mips;
                info.GraphicsOffset = gfx;
                info.AllocationBytes = length;
                info.Depth = depth;
                info.Dimension = dimension;
                info.NameHash = U32(system, hashes + i * 4);
                info.AssetKey = assetKey;
                info.HasAssetKey = hasAssetKey;

                RageTextureLayout layout = TextureLayout(info);
                int slices = dimension == 3 ? 1 : depth;
                if (checked(layout.Size * slices) != length)
                    throw new InvalidDataException("Descriptor allocation disagrees with AMD mip layout.");
                for (int r = 0; r < rangeStart.Count; r++)
                    if (gfx < rangeEnd[r] && rangeStart[r] < gfx + length)
                        throw new InvalidDataException("Overlapping texture allocations.");
                rangeStart.Add(gfx);
                rangeEnd.Add(gfx + length);
                result.Add(info);
            }
            return result;
        }


        private static RageTextureLayout TextureLayout(RageTextureInfo info)
        {
            return ComputeLayout(info.Width, info.Height, ElementBytes(info.DxgiFormat), info.MipCount,
                IsBlockCompressed(info.DxgiFormat) ? 4 : 1, info.Depth, info.Dimension == 3);
        }

        private static RageTextureLayout ComputeLayout(int w, int h, int eb, int mips, int pixelsPerElement, int depth, bool volume)
        {
            int maxMips = 1;
            int biggest = Math.Max(Math.Max(w, h), volume ? depth : 1);
            for (int size = biggest; size > 1; size >>= 1) maxMips++;
            if (mips < 1 || mips > maxMips) throw new InvalidDataException("Invalid mip count.");

            int log = ElementLog2(eb);
            int bw, bh, bd;
            if (volume)
            {
                switch (log)
                {
                    case 0: bw = 16; bh = 16; bd = 16; break;
                    case 1: bw = 8; bh = 16; bd = 16; break;
                    case 2: bw = 8; bh = 16; bd = 8; break;
                    case 3: bw = 8; bh = 8; bd = 8; break;
                    default: bw = 4; bh = 8; bd = 8; break;
                }
            }
            else
            {
                bd = 1;
                switch (log)
                {
                    case 0: bw = 64; bh = 64; break;
                    case 1: bw = 64; bh = 32; break;
                    case 2: bw = 32; bh = 32; break;
                    case 3: bw = 32; bh = 16; break;
                    default: bw = 16; bh = 16; break;
                }
            }

            int mw, mh;
            if (volume)
            {
                switch (log)
                {
                    case 0: mw = 8; mh = 4; break;
                    case 1: case 2: mw = 4; mh = 4; break;
                    case 3: mw = 4; mh = 2; break;
                    default: mw = 2; mh = 2; break;
                }
            }
            else
            {
                switch (log)
                {
                    case 0: mw = 16; mh = 16; break;
                    case 1: mw = 16; mh = 8; break;
                    case 2: mw = 8; mh = 8; break;
                    case 3: mw = 8; mh = 4; break;
                    default: mw = 4; mh = 4; break;
                }
            }

            int ew = (w + pixelsPerElement - 1) / pixelsPerElement;
            int eh = (h + pixelsPerElement - 1) / pixelsPerElement;
            int tail = mips;
            int tailW = volume ? bw : bw / 2;
            int tailH = volume ? bh / 2 : bh;
            int maxTail = volume ? 5 : 8;

            List<RageTextureMip> layouts = new List<RageTextureMip>();
            List<int> sizes = new List<int>();

            for (int m = 0; m < mips; m++)
            {
                int wc = Math.Max(1, (ew + (1 << m) - 1) >> m);
                int hc = Math.Max(1, (eh + (1 << m) - 1) >> m);
                if (mips > 1 && wc <= tailW && hc <= tailH && mips - m <= maxTail) { tail = m; break; }
                int pw = Align(wc, bw);
                int ph = Align(hc, bh);
                sizes.Add(checked(pw * ph * eb));
                RageTextureMip mi = new RageTextureMip();
                mi.Width = Elements(w, m, pixelsPerElement);
                mi.Height = Elements(h, m, pixelsPerElement);
                mi.PaddedWidth = pw;
                layouts.Add(mi);
            }

            int off = tail < mips ? 4096 : 0;
            for (int m = tail - 1; m >= 0; m--)
            {
                layouts[m].Offset = off;
                off = checked(off + sizes[m] * bd);
            }

            for (int m = tail; m < mips; m++)
            {
                int entry = maxTail - 1 - (m - tail);
                int moff = entry > 6 ? 16 << entry : entry << 8;
                int mx = 0, my = 0;
                for (int bit = 0; bit < 6; bit++)
                {
                    mx |= ((moff >> (9 + 2 * bit)) & 1) << bit;
                    my |= ((moff >> (8 + 2 * bit)) & 1) << bit;
                }
                RageTextureMip mi = new RageTextureMip();
                mi.Width = Elements(w, m, pixelsPerElement);
                mi.Height = Elements(h, m, pixelsPerElement);
                mi.PaddedWidth = bw;
                mi.TailX = mx * mw;
                mi.TailY = my * mh;
                layouts.Add(mi);
            }

            int sizeBytes = volume ? checked(off / bd * Align(depth, bd)) : off;
            if (sizeBytes > MaxGraphicsBytes) throw new InvalidDataException("Texture layout exceeds the output limit.");

            RageTextureLayout layout = new RageTextureLayout();
            layout.Size = sizeBytes;
            layout.BlockWidth = bw;
            layout.BlockHeight = bh;
            layout.BlockDepth = bd;
            layout.Volume = volume;
            layout.ZBlockStride = volume ? off : 0;
            layout.Mips = layouts;
            return layout;
        }

        private static int TiledOffset(RageTextureLayout layout, RageTextureMip mip, int eb, int x, int y, int slice)
        {
            int[] pattern;
            if (layout.Volume) pattern = VolumePatterns[ElementLog2(eb)];
            else
            {
                switch (eb)
                {
                    case 1: pattern = Pattern1; break;
                    case 2: pattern = Pattern2; break;
                    case 4: pattern = Pattern4; break;
                    case 8: pattern = Pattern8; break;
                    default: pattern = Pattern16; break;
                }
            }

            int block = (y / layout.BlockHeight) * (mip.PaddedWidth / layout.BlockWidth) + x / layout.BlockWidth;
            int address = 0;
            int tx = x + mip.TailX;
            int ty = y + mip.TailY;
            for (int bit = 0; bit < pattern.Length; bit++)
            {
                int s = pattern[bit];
                if (s >= 101) address |= ((slice >> (s - 101)) & 1) << bit;
                else if (s > 0) address |= ((tx >> (s - 1)) & 1) << bit;
                else if (s < 0) address |= ((ty >> (-s - 1)) & 1) << bit;
            }
            return checked((slice / layout.BlockDepth) * layout.ZBlockStride + mip.Offset + block * 4096 + address);
        }

        private static byte[] Detile(byte[] allocation, int allocationOffset, int allocationLength,
            RageTextureLayout layout, int eb, int level, int slice)
        {
            RageTextureMip mip = layout.Mips[level];
            byte[] linear = new byte[checked(mip.Width * mip.Height * eb)];
            for (int y = 0; y < mip.Height; y++)
            {
                for (int x = 0; x < mip.Width; x++)
                {
                    int offset = TiledOffset(layout, mip, eb, x, y, slice);
                    Check(offset, eb, allocationLength);
                    Buffer.BlockCopy(allocation, allocationOffset + offset, linear, (y * mip.Width + x) * eb, eb);
                }
            }
            return linear;
        }


        public static byte[] CreateDds(byte[] system, byte[] graphics, int index)
        {
            List<RageTextureInfo> textures = Parse(system, graphics.Length);
            if (index < 0 || index >= textures.Count) throw new ArgumentOutOfRangeException("index");
            RageTextureInfo info = textures[index];
            int eb = ElementBytes(info.DxgiFormat);
            RageTextureLayout layout = TextureLayout(info);
            bool volume = info.Dimension == 3;
            bool cube = info.Dimension == 2;

            MemoryStream ms = new MemoryStream();
            BinaryWriter w = new BinaryWriter(ms);
            w.Write(new byte[] { (byte)'D', (byte)'D', (byte)'S', (byte)' ' });
            w.Write(124);
            w.Write((uint)((IsBlockCompressed(info.DxgiFormat) ? 0x000A1007u : 0x0002100Fu) | (volume ? 0x800000u : 0u)));
            w.Write(info.Height);
            w.Write(info.Width);
            w.Write(IsBlockCompressed(info.DxgiFormat)
                ? layout.Mips[0].Width * layout.Mips[0].Height * eb
                : info.Width * eb);
            w.Write(volume ? info.Depth : 0);
            w.Write(info.MipCount);
            w.Write(new byte[44]);
            w.Write(32);
            w.Write(4u);
            w.Write(0x30315844u);
            w.Write(new byte[20]);
            w.Write((uint)((info.MipCount > 1 ? 0x401008u : 0x1000u) | (volume || cube || info.Depth > 1 ? 8u : 0u)));
            w.Write(volume ? 0x200000 : cube ? 0xFE00 : 0);
            w.Write(new byte[12]);
            w.Write(info.DxgiFormat);
            w.Write(volume ? 4 : 3);
            w.Write(cube ? 4 : 0);
            w.Write(volume ? 1 : cube ? info.Depth / 6 : info.Depth);
            w.Write(0);

            if (volume)
            {
                for (int mip = 0; mip < info.MipCount; mip++)
                {
                    int slices = Math.Max(1, info.Depth >> mip);
                    for (int z = 0; z < slices; z++)
                        w.Write(Detile(graphics, info.GraphicsOffset, info.AllocationBytes, layout, eb, mip, z));
                }
            }
            else
            {
                for (int slice = 0; slice < info.Depth; slice++)
                {
                    int baseOffset = checked(info.GraphicsOffset + slice * layout.Size);
                    for (int mip = 0; mip < info.MipCount; mip++)
                        w.Write(Detile(graphics, baseOffset, layout.Size, layout, eb, mip, 0));
                }
            }
            w.Flush();
            return ms.ToArray();
        }

        public static byte[] CreateDdsMip(byte[] system, byte[] graphics, int index, int level)
        {
            List<RageTextureInfo> textures = Parse(system, graphics.Length);
            if (index < 0 || index >= textures.Count) throw new ArgumentOutOfRangeException("index");
            RageTextureInfo info = textures[index];
            if (level < 0 || level >= info.MipCount) throw new ArgumentOutOfRangeException("level");
            int eb = ElementBytes(info.DxgiFormat);
            RageTextureLayout layout = TextureLayout(info);
            int width = Math.Max(1, info.Width >> level);
            int height = Math.Max(1, info.Height >> level);

            byte[] pixels;
            if (info.Dimension == 3)
                pixels = Detile(graphics, info.GraphicsOffset, info.AllocationBytes, layout, eb, level, 0);
            else
                pixels = Detile(graphics, info.GraphicsOffset, layout.Size, layout, eb, level, 0);

            MemoryStream ms = new MemoryStream();
            BinaryWriter w = new BinaryWriter(ms);
            w.Write(new byte[] { (byte)'D', (byte)'D', (byte)'S', (byte)' ' });
            w.Write(124);
            w.Write(IsBlockCompressed(info.DxgiFormat) ? 0x000A1007u : 0x0002100Fu);
            w.Write(height);
            w.Write(width);
            w.Write(IsBlockCompressed(info.DxgiFormat)
                ? Math.Max(1, (width + 3) / 4) * Math.Max(1, (height + 3) / 4) * eb
                : width * eb);
            w.Write(0);
            w.Write(1);
            w.Write(new byte[44]);
            w.Write(32);
            w.Write(4u);
            w.Write(0x30315844u);
            w.Write(new byte[20]);
            w.Write(0x1000u);
            w.Write(0);
            w.Write(new byte[12]);
            w.Write(info.DxgiFormat);
            w.Write(3);
            w.Write(0);
            w.Write(1);
            w.Write(0);
            w.Write(pixels);
            w.Flush();
            return ms.ToArray();
        }

        public static string Describe(byte[] system, byte[] graphics)
        {
            RageTextureDictionary dict = Inspect(system, graphics);
            if (dict == null) return "Not a supported texture dictionary.";
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("Texture dictionary: " + dict.Textures.Count + " textures, system=" + dict.SystemBytes + " graphics=" + dict.GraphicsBytes + " bytes");
            for (int i = 0; i < dict.Textures.Count; i++)
                sb.AppendLine("  [" + i + "] " + dict.Textures[i].Describe());
            return sb.ToString();
        }
    }
}
