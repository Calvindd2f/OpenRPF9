using System.Text;
using OpenRPF9.Core;

namespace OpenRPF9.Decoders;

public sealed class TextureDictionaryDecoder : IAssetDecoder
{
    public bool CanDecode(string name, ReadOnlySpan<byte> data)
    {
        return name.EndsWith(".ytd", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".ytd.rsc", StringComparison.OrdinalIgnoreCase);
    }

    public DecodedAsset Decode(string name, ReadOnlyMemory<byte> data)
    {
        var files = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        var meta = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["decoder"] = "ytd",
            ["status"] = "scaffold"
        };
        var span = data.Span;
        if (Rsc9Header.TryRead(span, out var header) && header is not null)
        {
            meta["rsc9.version"] = header.Version.ToString();
            meta["rsc9.systemSize"] = header.SystemSize.ToString();
            meta["rsc9.graphicsSize"] = header.GraphicsSize.ToString();
        }
        files[Path.GetFileName(name) + ".bin"] = data.ToArray();
        return new DecodedAsset
        {
            SourceName = name,
            Kind = "texture-dictionary",
            Files = files,
            Metadata = meta
        };
    }
}

public sealed class DrawableDecoder : IAssetDecoder
{
    public bool CanDecode(string name, ReadOnlySpan<byte> data)
    {
        return name.EndsWith(".ydr", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".ydd", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".yft", StringComparison.OrdinalIgnoreCase);
    }

    public DecodedAsset Decode(string name, ReadOnlyMemory<byte> data)
    {
        var meta = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["decoder"] = "drawable",
            ["status"] = "scaffold"
        };
        if (Rsc9Header.TryRead(data.Span, out var header) && header is not null)
        {
            meta["rsc9.systemSize"] = header.SystemSize.ToString();
            meta["rsc9.graphicsSize"] = header.GraphicsSize.ToString();
        }
        return new DecodedAsset
        {
            SourceName = name,
            Kind = "drawable",
            Files = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase)
            {
                [Path.ChangeExtension(Path.GetFileName(name), ".glb.placeholder")] = Encoding.UTF8.GetBytes("GLB export scaffold")
            },
            Metadata = meta
        };
    }
}

public sealed class CollisionDecoder : IAssetDecoder
{
    public bool CanDecode(string name, ReadOnlySpan<byte> data)
    {
        return name.EndsWith(".ybn", StringComparison.OrdinalIgnoreCase);
    }

    public DecodedAsset Decode(string name, ReadOnlyMemory<byte> data)
    {
        return new DecodedAsset
        {
            SourceName = name,
            Kind = "bounds",
            Files = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase)
            {
                [Path.ChangeExtension(Path.GetFileName(name), ".bounds.json")] = Encoding.UTF8.GetBytes("{\"status\":\"scaffold\"}")
            },
            Metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["decoder"] = "ybn", ["status"] = "scaffold" }
        };
    }
}

public sealed class ClipDictionaryDecoder : IAssetDecoder
{
    public bool CanDecode(string name, ReadOnlySpan<byte> data)
    {
        return name.EndsWith(".ycd", StringComparison.OrdinalIgnoreCase);
    }

    public DecodedAsset Decode(string name, ReadOnlyMemory<byte> data)
    {
        return new DecodedAsset
        {
            SourceName = name,
            Kind = "clip-dictionary",
            Files = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase)
            {
                [Path.ChangeExtension(Path.GetFileName(name), ".anim.json")] = Encoding.UTF8.GetBytes("{\"status\":\"scaffold\"}")
            },
            Metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["decoder"] = "ycd", ["status"] = "scaffold" }
        };
    }
}
