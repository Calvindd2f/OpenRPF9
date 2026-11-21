namespace OpenRPF9.Core;

public sealed class Rsc9Header
{
    public required uint Magic { get; init; }
    public required uint Version { get; init; }
    public required uint SystemFlags { get; init; }
    public required uint GraphicsFlags { get; init; }
    public int SystemPageCount => CountPages(SystemFlags);
    public int GraphicsPageCount => CountPages(GraphicsFlags);
    public int SystemSize => TotalSize(SystemFlags);
    public int GraphicsSize => TotalSize(GraphicsFlags);

    public static bool TryRead(ReadOnlySpan<byte> data, out Rsc9Header? header)
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
    public required Rsc9Header Header { get; init; }
    public required byte[] SystemSegment { get; init; }
    public required byte[] GraphicsSegment { get; init; }
    public required byte[] Raw { get; init; }
}

public static class Rsc9Parser
{
    public static Rsc9Resource Parse(ReadOnlySpan<byte> data)
    {
        if (!Rsc9Header.TryRead(data, out var header) || header is null)
            throw new RpfResourceException("Invalid RSC9 header.");
        var sysSize = Math.Min(header.SystemSize, Math.Max(0, data.Length - 16));
        var gfxSize = Math.Min(header.GraphicsSize, Math.Max(0, data.Length - 16 - sysSize));
        var sys = data.Slice(16, sysSize).ToArray();
        var gfx = data.Slice(16 + sysSize, gfxSize).ToArray();
        ApplyFixups(sys, header.SystemFlags);
        return new Rsc9Resource
        {
            Header = header,
            SystemSegment = sys,
            GraphicsSegment = gfx,
            Raw = data.ToArray()
        };
    }

    static void ApplyFixups(Span<byte> segment, uint flags)
    {
        if (segment.Length < 8)
            return;
        for (var i = 0; i + 8 <= segment.Length; i += 8)
        {
            var value = BinaryIO.ReadU64(segment, i);
            if ((value & 0xFFFFFFFE00000000UL) == 0x500000000UL)
            {
                var offset = (int)(value & 0x1FFFFFFFUL);
                if (offset >= 0 && offset < segment.Length)
                    System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(segment.Slice(i, 8), (ulong)offset);
            }
        }
    }
}
