using System;
using System.Collections.Generic;
using System.IO;
namespace OpenRPF9.Core
{
public sealed class Rsc9Header
{
    public uint Magic { get; set; }
    public uint Version { get; set; }
    public uint SystemFlags { get; set; }
    public uint GraphicsFlags { get; set; }
    public int SystemPageCount { get { return CountPages(SystemFlags); } }
    public int GraphicsPageCount { get { return CountPages(GraphicsFlags); } }
    public int SystemSize { get { return TotalSize(SystemFlags); } }
    public int GraphicsSize { get { return TotalSize(GraphicsFlags); } }

    public static bool TryRead(byte[] data, out Rsc9Header header)
    {
        header = null;
        if (data.Length < 16)
            return false;
        var magic = BinaryIO.ReadU32(data, 0);
        if (magic != 0x37435352)
            return false;
        header = new Rsc9Header
        {
            Magic = magic,
            Version = BinaryIO.ReadU32(data, 4),
            SystemFlags = BinaryIO.ReadU32(data, 8),
            GraphicsFlags = BinaryIO.ReadU32(data, 12)
        };
        return true;
    }

    static int CountPages(uint flags)
    {
        var count = 0;
        for (var i = 0; i < 8; i++)
        {
            var shift = i * 4;
            var sizeCode = (int)((flags >> shift) & 0xFu);
            if (sizeCode == 0)
                continue;
            count++;
        }
        return count == 0 ? 1 : count;
    }

    static int TotalSize(uint flags)
    {
        var total = 0;
        var has = false;
        for (var i = 0; i < 8; i++)
        {
            var sizeCode = (int)((flags >> (i * 4)) & 0xFu);
            if (sizeCode == 0)
                continue;
            has = true;
            total += PageSize(sizeCode);
        }
        return has ? total : 0x2000;
    }

    static int PageSize(int code)
    {
        var baseSize = 0x2000 << (((code - 1) & 0x7));
        return baseSize;
    }
}

public sealed class Rsc9Resource
{
    public Rsc9Header Header { get; set; }
    public byte[] SystemSegment { get; set; }
    public byte[] GraphicsSegment { get; set; }
    public byte[] Raw { get; set; }
}

public static class Rsc9Parser
{
    public static Rsc9Resource Parse(byte[] data)
    {
        Rsc9Header header; if (!Rsc9Header.TryRead(data, out header) || header == null)
            throw new RpfResourceException("Invalid RSC9 header.");
        var sysSize = Math.Min(header.SystemSize, Math.Max(0, data.Length - 16));
        var gfxSize = Math.Min(header.GraphicsSize, Math.Max(0, data.Length - 16 - sysSize));
        byte[] sys = new byte[sysSize]; Array.Copy(data,16,sys,0,sysSize);
        byte[] gfx = new byte[gfxSize]; Array.Copy(data,16+sysSize,gfx,0,gfxSize);
        ApplyFixups(sys, header.SystemFlags);
        return new Rsc9Resource
        {
            Header = header,
            SystemSegment = sys,
            GraphicsSegment = gfx,
            Raw = data
        };
    }

    static void ApplyFixups(byte[] segment, uint flags)
    {
        if (segment.Length < 8)
            return;
        for (int i = 0; i + 8 <= segment.Length; i += 8)
        {
            ulong value = BinaryIO.ReadU64(segment, i);
            if ((value & 0xFFFFFFFE00000000UL) == 0x500000000UL)
            {
                int offset = (int)(value & 0x1FFFFFFFUL);
                if (offset >= 0 && offset < segment.Length)
                {
                    byte[] ob = BitConverter.GetBytes((ulong)offset);
                    Array.Copy(ob, 0, segment, i, 8);
                }
            }
        }
    }
}
}
