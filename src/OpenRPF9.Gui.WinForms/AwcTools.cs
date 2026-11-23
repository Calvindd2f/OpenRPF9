using System;
using System.Collections.Generic;
using System.IO;
using System.Diagnostics;
using System.Text;

namespace OpenRPF9
{
    internal static class AwcContainerTool
    {
        internal sealed class Chunk { public byte Type; public uint Size; public uint Offset; }
        internal sealed class StreamEntry { public uint Id; public List<Chunk> Chunks = new System.Collections.Generic.List<Chunk>(); }
        internal sealed class AwcParsed { public byte[] Data; public int BaseOffset; public uint Flags; public uint HeaderLength; public List<StreamEntry> Streams = new System.Collections.Generic.List<StreamEntry>(); }

        public static bool TryParse(byte[] data, int baseOffset, out AwcParsed parsed)
        {
            parsed = null;
            if (data == null || baseOffset < 0 || baseOffset + 16 > data.Length) return false;
            if (data[baseOffset] != (byte)'A' || data[baseOffset + 1] != (byte)'D' || data[baseOffset + 2] != (byte)'A' || data[baseOffset + 3] != (byte)'T') return false;
            uint flags = BitConverter.ToUInt32(data, baseOffset + 4);
            uint streamCount = BitConverter.ToUInt32(data, baseOffset + 8);
            uint headerLength = BitConverter.ToUInt32(data, baseOffset + 12);
            if (streamCount == 0 || streamCount > 100000 || headerLength < 16 || baseOffset + headerLength > data.Length) return false;
            int pos = 16;
            if ((flags & 0x00400000u) != 0) pos += 4;
            int firstTags = pos;
            if ((flags & 0x00010000u) != 0) pos += (int)streamCount * 2;
            if ((flags & 0x00200000u) != 0) pos += ((int)streamCount + 1) * 4;
            int table = pos;
            if (table + (long)streamCount * 4 > headerLength) return false;
            uint[] infos = new uint[streamCount];
            int tagTotal = 0;
            for (int i = 0; i < streamCount; i++)
            {
                infos[i] = BitConverter.ToUInt32(data, baseOffset + table + i * 4);
                if ((flags & 0x00010000u) != 0)
                {
                    int ft = BitConverter.ToUInt16(data, baseOffset + firstTags + i * 2);
                    if (ft != tagTotal) return false;
                }
                tagTotal += (int)((infos[i] >> 29) & 7u);
            }
            int tagsOff = table + (int)streamCount * 4;
            if (tagsOff + tagTotal * 8 != (int)headerLength) return false;
            AwcParsed a = new AwcParsed(); a.Data = data; a.BaseOffset = baseOffset; a.Flags = flags; a.HeaderLength = headerLength;
            int p = baseOffset + tagsOff;
            for (int i = 0; i < streamCount; i++)
            {
                StreamEntry s = new StreamEntry(); s.Id = infos[i] & 0x1FFFFFFFu;
                int tc = (int)((infos[i] >> 29) & 7u);
                for (int t = 0; t < tc; t++)
                {
                    ulong raw = BitConverter.ToUInt64(data, p); p += 8;
                    Chunk c = new Chunk(); c.Type = (byte)(raw >> 56); c.Size = (uint)((raw >> 28) & 0x0FFFFFFFu); c.Offset = (uint)(raw & 0x0FFFFFFFu);
                    s.Chunks.Add(c);
                }
                a.Streams.Add(s);
            }
            parsed = a;
            return true;
        }

        public static Chunk FindChunk(StreamEntry s, byte type)
        {
            if (s == null) return null;
            for (int i = 0; i < s.Chunks.Count; i++) if (s.Chunks[i].Type == type) return s.Chunks[i];
            return null;
        }

        public static byte[] ChunkBytes(AwcParsed a, Chunk c)
        {
            if (a == null || c == null) return new byte[0];
            long start = (long)a.BaseOffset + c.Offset;
            if (start < 0 || c.Size == 0 || start + c.Size > a.Data.Length) return new byte[0];
            byte[] b = new byte[c.Size];
            Array.Copy(a.Data, start, b, 0, c.Size);
            return b;
        }
    }

    internal static class OpusPacketTool
    {
        public static int PacketSamples(byte[] packet)
        {
            if (packet == null || packet.Length == 0) return 0;
            int toc = packet[0] & 0xFF;
            int config = toc >> 3;
            int code = toc & 3;
            int frameSize;
            if (config < 12) { int[] s = { 480, 960, 1920, 2880 }; frameSize = s[config & 3]; }
            else if (config < 16) frameSize = (config & 1) == 0 ? 480 : 960;
            else { int[] s = { 120, 240, 480, 960 }; frameSize = s[config & 3]; }
            int frameCount;
            if (code == 0) frameCount = 1;
            else if (code == 1 || code == 2) frameCount = 2;
            else
            {
                if (packet.Length < 2) return 0;
                frameCount = packet[packet.Length - 1] & 0x3F;
                if (frameCount == 0) return 0;
            }
            return frameCount * frameSize;
        }

        public static int ChannelsFromPackets(List<byte[]> packets, int fallback)
        {
            if (packets == null || packets.Count == 0) return fallback > 0 ? fallback : 1;
            byte[] biggest = null;
            for (int i = 0; i < packets.Count; i++) { byte[] p = packets[i]; if (p != null && p.Length > 0 && (biggest == null || p.Length > biggest.Length)) biggest = p; }
            if (biggest == null) return fallback > 0 ? fallback : 1;
            return (biggest[0] & 0x04) != 0 ? 2 : 1;
        }

        public static bool Collect2KbPackets(byte[] blob, int start, int length, List<byte[]> packets)
        {
            if (blob == null || length <= 0) return false;
            int end = start + length;
            if (end > blob.Length) end = blob.Length;
            bool found = false;
            for (int bpos = start; bpos < end; bpos += 2048)
            {
                int bend = Math.Min(bpos + 2048, end);
                int p = bpos;
                while (p + 2 <= bend)
                {
                    int plen = blob[p] | (blob[p + 1] << 8);
                    if (plen <= 0 || plen > 1275 || p + 2 + plen > bend) break;
                    byte[] pkt = new byte[plen];
                    Array.Copy(blob, p + 2, pkt, 0, plen);
                    packets.Add(pkt);
                    found = true;
                    p += 2 + plen;
                }
                if (p < bend)
                {
                    for (; p < bend; p++) if (blob[p] != 0) return false;
                }
            }
            return found;
        }

        public static int? ProbeSuperblockStreams(byte[] blob)
        {
            if (blob == null || blob.Length < 4096) return null;
            int n = 0;
            int totalBlks = 0;
            int limit = Math.Min(blob.Length, 2048);
            while ((n + 1) * 24 <= limit)
            {
                uint marker = BitConverter.ToUInt32(blob, n * 24);
                if (marker != 0xFFFFFFFFu) break;
                uint cnt = BitConverter.ToUInt32(blob, n * 24 + 4);
                if (cnt == 0 || cnt > 255) break;
                totalBlks += (int)cnt;
                n++;
            }
            if (n >= 1 && totalBlks <= 255) return n;
            return null;
        }

        public static List<byte[]> PacketsForSuperblockSubstream(byte[] blob, int nSub, int subIdx)
        {
            List<byte[]> packets = new System.Collections.Generic.List<byte[]>();
            if (blob == null || nSub <= 0 || subIdx < 0 || subIdx >= nSub) return packets;
            for (int sbStart = 0; sbStart < blob.Length; sbStart += 0x80000)
            {
                int sbLen = Math.Min(0x80000, blob.Length - sbStart);
                if (sbLen < nSub * 24) continue;
                int[] counts = new int[nSub];
                bool valid = true;
                int totalBlks = 0;
                for (int s = 0; s < nSub; s++)
                {
                    uint marker = BitConverter.ToUInt32(blob, sbStart + s * 24);
                    if (marker != 0xFFFFFFFFu) { valid = false; break; }
                    uint c = BitConverter.ToUInt32(blob, sbStart + s * 24 + 4);
                    if (c > 255) { valid = false; break; }
                    counts[s] = (int)c;
                    totalBlks += counts[s];
                }
                if (!valid || totalBlks == 0 || totalBlks > 256) continue;
                int dirBytes = nSub * 24 + totalBlks * 4;
                int dirBlks = Math.Max(1, (dirBytes + 2047) / 2048);
                int startBlk = dirBlks;
                for (int s = 0; s < subIdx; s++) startBlk += counts[s];
                int endBlk = startBlk + counts[subIdx];
                int byteStart = sbStart + startBlk * 2048;
                int byteEnd = Math.Min(sbStart + endBlk * 2048, sbStart + sbLen);
                if (byteStart < byteEnd) Collect2KbPackets(blob, byteStart, byteEnd - byteStart, packets);
            }
            return packets;
        }
    }

    internal static class OggOpusTool
    {
        static uint[] CrcTable = BuildCrcTable();
        static uint[] BuildCrcTable()
        {
            uint[] t = new uint[256];
            for (uint i = 0; i < 256; i++)
            {
                uint v = i << 24;
                for (int k = 0; k < 8; k++) v = (v & 0x80000000u) != 0 ? ((v << 1) ^ 0x04C11DB7u) : (v << 1);
                t[i] = v;
            }
            return t;
        }
        static uint Crc(byte[] data)
        {
            uint crc = 0;
            for (int i = 0; i < data.Length; i++) crc = (crc << 8) ^ CrcTable[((crc >> 24) ^ data[i]) & 0xFF];
            return crc;
        }
        static byte[] Page(uint serial, uint seq, byte headerType, ulong granule, byte[] payload, byte[] lacing)
        {
            using (MemoryStream ms = new MemoryStream())
            using (BinaryWriter bw = new BinaryWriter(ms))
            {
                bw.Write(Encoding.ASCII.GetBytes("OggS"));
                bw.Write((byte)0);
                bw.Write(headerType);
                bw.Write(granule);
                bw.Write(serial);
                bw.Write(seq);
                bw.Write((uint)0);
                bw.Write((byte)lacing.Length);
                bw.Write(lacing);
                bw.Write(payload);
                byte[] page = ms.ToArray();
                uint c = Crc(page);
                page[22] = (byte)c; page[23] = (byte)(c >> 8); page[24] = (byte)(c >> 16); page[25] = (byte)(c >> 24);
                return page;
            }
        }
        static List<byte> Lacing(int length)
        {
            List<byte> l = new System.Collections.Generic.List<byte>();
            while (length >= 255) { l.Add(255); length -= 255; }
            l.Add((byte)length);
            return l;
        }
        public static byte[] BuildOgg(List<byte[]> packets, int channels, int preSkip, uint serial)
        {
            if (packets == null || packets.Count == 0) return null;
            if (channels <= 0) channels = 1;
            if (channels > 2) channels = 2;
            if (preSkip < 0) preSkip = 312;
            using (MemoryStream ms = new MemoryStream())
            {
                byte[] head = new byte[19];
                Array.Copy(Encoding.ASCII.GetBytes("OpusHead"), 0, head, 0, 8);
                head[8] = 1; head[9] = (byte)channels;
                Array.Copy(BitConverter.GetBytes((ushort)preSkip), 0, head, 10, 2);
                Array.Copy(BitConverter.GetBytes(48000u), 0, head, 12, 4);
                head[18] = 0;
                byte[] headPage = Page(serial, 0, 2, 0, head, Lacing(head.Length).ToArray());
                ms.Write(headPage, 0, headPage.Length);
                byte[] vendor = Encoding.ASCII.GetBytes("OpenRPF9");
                byte[] tags = new byte[8 + 4 + vendor.Length + 4];
                Array.Copy(Encoding.ASCII.GetBytes("OpusTags"), 0, tags, 0, 8);
                Array.Copy(BitConverter.GetBytes(vendor.Length), 0, tags, 8, 4);
                Array.Copy(vendor, 0, tags, 12, vendor.Length);
                byte[] tagPage = Page(serial, 1, 0, 0, tags, Lacing(tags.Length).ToArray());
                ms.Write(tagPage, 0, tagPage.Length);
                ulong granule = (ulong)preSkip;
                uint seq = 2;
                for (int i = 0; i < packets.Count; i += 50)
                {
                    int take = Math.Min(50, packets.Count - i);
                    List<byte> lacing = new System.Collections.Generic.List<byte>();
                    using (MemoryStream body = new MemoryStream())
                    {
                        for (int j = 0; j < take; j++)
                        {
                            byte[] p = packets[i + j];
                            lacing.AddRange(Lacing(p.Length));
                            body.Write(p, 0, p.Length);
                            granule += (ulong)OpusPacketTool.PacketSamples(p);
                        }
                        byte ht = (i + take >= packets.Count) ? (byte)4 : (byte)0;
                        byte[] page = Page(serial, seq++, ht, granule, body.ToArray(), lacing.ToArray());
                        ms.Write(page, 0, page.Length);
                    }
                }
                return ms.ToArray();
            }
        }
    }

    internal static class AwcExtractTool
    {
        public static int Run(string awcPath, string outDir)
        {
            byte[] data = File.ReadAllBytes(awcPath);
            AwcContainerTool.AwcParsed awc;
            if (!AwcContainerTool.TryParse(data, 0, out awc)) { Console.WriteLine("not AWC: " + awcPath); return 0; }
            Directory.CreateDirectory(outDir);
            string baseName = Path.GetFileNameWithoutExtension(awcPath);
            int total = 0;
            for (int i = 0; i < awc.Streams.Count; i++)
            {
                AwcContainerTool.Chunk dataChunk = AwcContainerTool.FindChunk(awc.Streams[i], 0x55);
                if (dataChunk == null) dataChunk = AwcContainerTool.FindChunk(awc.Streams[i], 0x44);
                if (dataChunk == null) continue;
                byte[] blob = AwcContainerTool.ChunkBytes(awc, dataChunk);
                if (blob.Length == 0) continue;
                int? nSub = OpusPacketTool.ProbeSuperblockStreams(blob);
                List<List<byte[]>> subs = new List<List<byte[]>>();
                if (nSub == null)
                {
                    List<byte[]> pk = new System.Collections.Generic.List<byte[]>();
                    OpusPacketTool.Collect2KbPackets(blob, 0, blob.Length, pk);
                    subs.Add(pk);
                }
                else
                {
                    for (int s = 0; s < nSub.Value; s++) subs.Add(OpusPacketTool.PacketsForSuperblockSubstream(blob, nSub.Value, s));
                }
                for (int s = 0; s < subs.Count; s++)
                {
                    List<byte[]> packets = subs[s];
                    if (packets.Count == 0) continue;
                    int ch = OpusPacketTool.ChannelsFromPackets(packets, 1);
                    string suffix = subs.Count == 1 ? "s" + i.ToString("00") : "s" + i.ToString("00") + "_" + s;
                    string ogg = Path.Combine(outDir, baseName + "_" + suffix + ".opus");
                    string wav = Path.Combine(outDir, baseName + "_" + suffix + ".wav");
                    byte[] oggBytes = OggOpusTool.BuildOgg(packets, ch, 312, (uint)(0x50530001u + i * 8 + s));
                    if (oggBytes == null) continue;
                    File.WriteAllBytes(ogg, oggBytes);
                    string ff = Environment.GetEnvironmentVariable("OPUS_FFMPEG");
                    if (string.IsNullOrEmpty(ff)) ff = "ffmpeg";
                    try
                    {
                        ProcessStartInfo psi = new ProcessStartInfo(ff, "-y -loglevel error -i \"" + ogg + "\" \"" + wav + "\"");
                        psi.UseShellExecute = false; psi.CreateNoWindow = true;
                        using (Process p = Process.Start(psi)) { if (p != null) p.WaitForExit(120000); }
                    }
                    catch { }
                    try { File.Delete(ogg); } catch { }
                    if (File.Exists(wav) && new FileInfo(wav).Length > 44) { total++; Console.WriteLine(wav); }
                }
            }
            return total;
        }
    }
}
