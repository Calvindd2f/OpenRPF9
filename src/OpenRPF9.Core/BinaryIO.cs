using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Text;

namespace OpenRPF9.Core;

public static class BinaryIO
{
    public static uint ReadU32(ReadOnlySpan<byte> data, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(offset, 4));
    public static int ReadI32(ReadOnlySpan<byte> data, int offset) => BinaryPrimitives.ReadInt32LittleEndian(data.Slice(offset, 4));
    public static ushort ReadU16(ReadOnlySpan<byte> data, int offset) => BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(offset, 2));
    public static ulong ReadU64(ReadOnlySpan<byte> data, int offset) => BinaryPrimitives.ReadUInt64LittleEndian(data.Slice(offset, 8));
    public static uint ReadU32BE(ReadOnlySpan<byte> data, int offset) => BinaryPrimitives.ReadUInt32BigEndian(data.Slice(offset, 4));
    public static ushort ReadU16BE(ReadOnlySpan<byte> data, int offset) => BinaryPrimitives.ReadUInt16BigEndian(data.Slice(offset, 2));

    public static uint ReadU24(ReadOnlySpan<byte> data, int offset)
    {
        return (uint)(data[offset] | (data[offset + 1] << 8) | (data[offset + 2] << 16));
    }

    public static uint ReadU24BE(ReadOnlySpan<byte> data, int offset)
    {
        return (uint)((data[offset] << 16) | (data[offset + 1] << 8) | data[offset + 2]);
    }

    public static string ReadNullTerminatedString(ReadOnlySpan<byte> data, int offset)
    {
        var end = offset;
        while (end < data.Length && data[end] != 0)
            end++;
        return Encoding.UTF8.GetString(data.Slice(offset, end - offset));
    }

    public static string ReadMagic(ReadOnlySpan<byte> data, int offset = 0)
    {
        return Encoding.ASCII.GetString(data.Slice(offset, 4));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool HasMagic(ReadOnlySpan<byte> data, string magic)
    {
        return data.Length >= 4 && data[0] == (byte)magic[0] && data[1] == (byte)magic[1] && data[2] == (byte)magic[2] && data[3] == (byte)magic[3];
    }
}
