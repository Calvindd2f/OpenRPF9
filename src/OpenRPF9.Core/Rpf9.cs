namespace OpenRPF9.Core;

public sealed class Rpf9HeaderProbe
{
    public required uint Magic { get; init; }
    public required uint Field04 { get; init; }
    public required uint Field08 { get; init; }
    public required uint Field0C { get; init; }
    public required uint Field10 { get; init; }
    public required uint Field14 { get; init; }
    public required uint Field18 { get; init; }
    public required uint Field1C { get; init; }

    public static Rpf9HeaderProbe Read(ReadOnlySpan<byte> data)
    {
        if (data.Length < 32)
            throw new RpfFormatException("RPF9 header too small.");
        var magic = BinaryIO.ReadU32(data, 0);
        if (magic != 0x52504639)
            throw new RpfFormatException("Not an RPF9 archive.");
        return new Rpf9HeaderProbe
        {
            Magic = magic,
            Field04 = BinaryIO.ReadU32(data, 4),
            Field08 = BinaryIO.ReadU32(data, 8),
            Field0C = BinaryIO.ReadU32(data, 12),
            Field10 = BinaryIO.ReadU32(data, 16),
            Field14 = BinaryIO.ReadU32(data, 20),
            Field18 = BinaryIO.ReadU32(data, 24),
            Field1C = BinaryIO.ReadU32(data, 28)
        };
    }
}

public static class Rpf9Scanner
{
    public static IReadOnlyList<long> FindZlibStreams(ReadOnlySpan<byte> data)
    {
        var hits = new List<long>();
        for (var i = 0; i < data.Length - 1; i++)
        {
            if (data[i] != 0x78)
                continue;
            var b2 = data[i + 1];
            if (b2 is 0x01 or 0x5E or 0x9C or 0xDA)
                hits.Add(i);
        }
        return hits;
    }
}
