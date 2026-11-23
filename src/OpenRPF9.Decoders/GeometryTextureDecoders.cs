using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace OpenRPF9.Decoders
{
    public sealed class TextureDictionaryDecoder : IAssetDecoder
    {
        public bool CanDecode(string name, byte[] data) { return name.EndsWith(".ytd", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".ytd.full", StringComparison.OrdinalIgnoreCase); }
        public DecodedAsset Decode(string name, byte[] data)
        {
            DecodedAsset a = new DecodedAsset();
            a.SourceName = name; a.Kind = "texture";
            a.Files = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
            a.Files[Path.GetFileNameWithoutExtension(name) + ".dds.placeholder"] = Encoding.UTF8.GetBytes("YTD decode scaffold");
            a.Metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            a.Metadata["decoder"] = "ytd"; a.Metadata["status"] = "scaffold"; a.Metadata["bytes"] = data.Length.ToString();
            return a;
        }
    }

    public sealed class DrawableDecoder : IAssetDecoder
    {
        public bool CanDecode(string name, byte[] data)
        {
            string n = name.ToLowerInvariant();
            return n.EndsWith(".ydr") || n.EndsWith(".ydd") || n.EndsWith(".yft");
        }
        public DecodedAsset Decode(string name, byte[] data)
        {
            DecodedAsset a = new DecodedAsset();
            a.SourceName = name; a.Kind = "geometry";
            a.Files = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
            a.Files[Path.GetFileNameWithoutExtension(name) + ".obj.placeholder"] = Encoding.UTF8.GetBytes("Drawable decode scaffold");
            a.Metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            a.Metadata["decoder"] = "drawable"; a.Metadata["status"] = "scaffold"; a.Metadata["bytes"] = data.Length.ToString();
            return a;
        }
    }

    public sealed class BoundsDecoder : IAssetDecoder
    {
        public bool CanDecode(string name, byte[] data) { return name.EndsWith(".ybn", StringComparison.OrdinalIgnoreCase); }
        public DecodedAsset Decode(string name, byte[] data)
        {
            DecodedAsset a = new DecodedAsset(); a.SourceName = name; a.Kind = "bounds";
            a.Files = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
            a.Files[Path.GetFileNameWithoutExtension(name) + ".bounds.txt"] = Encoding.UTF8.GetBytes("YBN decode scaffold");
            a.Metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            a.Metadata["decoder"] = "ybn"; a.Metadata["status"] = "scaffold";
            return a;
        }
    }

    public sealed class ClothDecoder : IAssetDecoder
    {
        public bool CanDecode(string name, byte[] data) { return name.EndsWith(".yld", StringComparison.OrdinalIgnoreCase); }
        public DecodedAsset Decode(string name, byte[] data)
        {
            DecodedAsset a = new DecodedAsset(); a.SourceName = name; a.Kind = "cloth";
            a.Files = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
            a.Files[Path.GetFileNameWithoutExtension(name) + ".cloth.txt"] = Encoding.UTF8.GetBytes("YLD decode scaffold");
            a.Metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            a.Metadata["decoder"] = "yld"; a.Metadata["status"] = "scaffold";
            return a;
        }
    }
}
