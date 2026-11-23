using System;
using System.Text;
namespace OpenRPF9.Core
{
    public static class BinaryIO
    {
        public static uint ReadU32(byte[] data, int offset) { return BitConverter.ToUInt32(data, offset); }
        public static int ReadI32(byte[] data, int offset) { return BitConverter.ToInt32(data, offset); }
        public static ushort ReadU16(byte[] data, int offset) { return BitConverter.ToUInt16(data, offset); }
        public static ulong ReadU64(byte[] data, int offset) { return BitConverter.ToUInt64(data, offset); }
        public static uint ReadU32BE(byte[] data, int offset) { return ((uint)data[offset] << 24) | ((uint)data[offset + 1] << 16) | ((uint)data[offset + 2] << 8) | data[offset + 3]; }
        public static ushort ReadU16BE(byte[] data, int offset) { return (ushort)(((uint)data[offset] << 8) | data[offset + 1]); }
        public static uint ReadU24(byte[] data, int offset) { return (uint)(data[offset] | (data[offset + 1] << 8) | (data[offset + 2] << 16)); }
        public static uint ReadU24BE(byte[] data, int offset) { return (uint)((data[offset] << 16) | (data[offset + 1] << 8) | data[offset + 2]); }
        public static string ReadNullTerminatedString(byte[] data, int offset) { int end = offset; while (end < data.Length && data[end] != 0) end++; return Encoding.UTF8.GetString(data, offset, end - offset); }
        public static string ReadMagic(byte[] data, int offset) { return Encoding.ASCII.GetString(data, offset, 4); }
        public static string ReadMagic(byte[] data) { return ReadMagic(data, 0); }
        public static bool HasMagic(byte[] data, string magic) { return data.Length >= 4 && data[0] == (byte)magic[0] && data[1] == (byte)magic[1] && data[2] == (byte)magic[2] && data[3] == (byte)magic[3]; }
    }
}
