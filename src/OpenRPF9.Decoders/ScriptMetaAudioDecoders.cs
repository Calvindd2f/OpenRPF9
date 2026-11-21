using System.Text;
using OpenRPF9.Core;

namespace OpenRPF9.Decoders;

public sealed class ScriptDecoder : IAssetDecoder
{
    public bool CanDecode(string name, ReadOnlySpan<byte> data)
    {
        return name.EndsWith(".ysc", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".ysc.full", StringComparison.OrdinalIgnoreCase);
    }

    public DecodedAsset Decode(string name, ReadOnlyMemory<byte> data)
    {
        var sb = new StringBuilder();
        sb.AppendLine("; OpenRPF9 script disassembly scaffold");
        sb.AppendLine($"; source: {name}");
        sb.AppendLine($"; size: {data.Length}");
        if (Rsc9Header.TryRead(data.Span, out var header) && header is not null)
        {
            sb.AppendLine($"; rsc9.version: {header.Version}");
            sb.AppendLine($"; rsc9.system: {header.SystemSize}");
            sb.AppendLine($"; rsc9.graphics: {header.GraphicsSize}");
        }
        return new DecodedAsset
        {
            SourceName = name,
            Kind = "script",
            Files = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase)
            {
                [Path.ChangeExtension(Path.GetFileName(name), ".ysc.txt")] = Encoding.UTF8.GetBytes(sb.ToString())
            },
            Metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["decoder"] = "ysc", ["status"] = "scaffold" }
        };
    }
}

public sealed class MetaDecoder : IAssetDecoder
{
    static readonly string[] Extensions = [".ymap", ".ytyp", ".meta", ".rbf", ".pso", ".ymt"]; 

    public bool CanDecode(string name, ReadOnlySpan<byte> data)
    {
        foreach (var ext in Extensions)
        {
            if (name.EndsWith(ext, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return name.EndsWith(".pso", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".ymt", StringComparison.OrdinalIgnoreCase);
    }

    public DecodedAsset Decode(string name, ReadOnlyMemory<byte> data)
    {
        var xml = $"<?xml version=\"1.0\" encoding=\"utf-8\"?>\n<openrpf9 kind=\"meta\" source=\"{System.Security.SecurityElement.Escape(name)}\" status=\"scaffold\" bytes=\"{data.Length}\" />\n";
        return new DecodedAsset
        {
            SourceName = name,
            Kind = "meta",
            Files = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase)
            {
                [Path.ChangeExtension(Path.GetFileName(name), ".xml")] = Encoding.UTF8.GetBytes(xml)
            },
            Metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["decoder"] = "meta", ["status"] = "scaffold" }
        };
    }
}

public sealed class AudioDecoder : IAssetDecoder
{
    public bool CanDecode(string name, ReadOnlySpan<byte> data)
    {
        return name.EndsWith(".awc", StringComparison.OrdinalIgnoreCase);
    }

    public DecodedAsset Decode(string name, ReadOnlyMemory<byte> data)
    {
        var meta = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["decoder"] = "awc",
            ["status"] = "scaffold",
            ["bytes"] = data.Length.ToString()
        };
        return new DecodedAsset
        {
            SourceName = name,
            Kind = "audio",
            Files = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase)
            {
                [Path.ChangeExtension(Path.GetFileName(name), ".wav.placeholder")] = Encoding.UTF8.GetBytes("AWC decode scaffold")
            },
            Metadata = meta
        };
    }
}

