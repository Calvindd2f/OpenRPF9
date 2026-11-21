using OpenRPF9.Core;

namespace OpenRPF9.Decoders;

public interface IAssetDecoder
{
    bool CanDecode(string name, ReadOnlySpan<byte> data);
    DecodedAsset Decode(string name, ReadOnlyMemory<byte> data);
}

public sealed class DecodedAsset
{
    public required string SourceName { get; init; }
    public required string Kind { get; init; }
    public required IReadOnlyDictionary<string, byte[]> Files { get; init; }
    public IReadOnlyDictionary<string, string>? Metadata { get; init; }
}

public sealed class DecoderRegistry
{
    readonly List<IAssetDecoder> _decoders = [];

    public void Register(IAssetDecoder decoder)
    {
        ArgumentNullException.ThrowIfNull(decoder);
        _decoders.Add(decoder);
    }

    public IAssetDecoder? Resolve(string name, ReadOnlySpan<byte> data)
    {
        for (var i = 0; i < _decoders.Count; i++)
        {
            if (_decoders[i].CanDecode(name, data))
                return _decoders[i];
        }
        return null;
    }

    public DecodedAsset? Decode(string name, ReadOnlyMemory<byte> data)
    {
        var decoder = Resolve(name, data.Span);
        return decoder?.Decode(name, data);
    }
}
