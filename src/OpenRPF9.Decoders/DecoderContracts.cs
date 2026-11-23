using System;
using System.Collections.Generic;
using System.IO;
using OpenRPF9.Core;

namespace OpenRPF9.Decoders
{
public interface IAssetDecoder
{
    bool CanDecode(string name, byte[] data);
    DecodedAsset Decode(string name, byte[] data);
}

public sealed class DecodedAsset
{
    public string SourceName { get; set; }
    public string Kind { get; set; }
    public Dictionary<string, byte[]> Files { get; set; }
    public Dictionary<string, string> Metadata { get; set; }
}

public sealed class DecoderRegistry
{
    readonly List<IAssetDecoder> _decoders = new System.Collections.Generic.List<IAssetDecoder>();

    public void Register(IAssetDecoder decoder)
    {
        if (decoder == null) throw new ArgumentNullException("decoder");
        _decoders.Add(decoder);
    }

    public IAssetDecoder Resolve(string name, byte[] data)
    {
        for (var i = 0; i < _decoders.Count; i++)
        {
            if (_decoders[i].CanDecode(name, data))
                return _decoders[i];
        }
        return null;
    }

    public DecodedAsset Decode(string name, byte[] data)
    {
        var decoder = Resolve(name, data);
        return decoder.Decode(name, data);
    }
}
}
