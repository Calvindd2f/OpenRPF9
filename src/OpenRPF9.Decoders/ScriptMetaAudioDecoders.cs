using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using OpenRPF9.Core;

namespace OpenRPF9.Decoders
{
    public sealed class ScriptDecoder : IAssetDecoder
    {
        public bool CanDecode(string name, byte[] data)
        {
            return name.EndsWith(".ysc", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".ysc.full", StringComparison.OrdinalIgnoreCase);
        }

        public DecodedAsset Decode(string name, byte[] data)
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("; OpenRPF9 script disassembly scaffold");
            sb.AppendLine("; source: " + name);
            sb.AppendLine("; size: " + data.Length);
            Rsc9Header header;
            if (Rsc9Header.TryRead(data, out header) && header != null)
            {
                sb.AppendLine("; rsc9.version: " + header.Version);
                sb.AppendLine("; rsc9.system: " + header.SystemSize);
                sb.AppendLine("; rsc9.graphics: " + header.GraphicsSize);
            }
            DecodedAsset a = new DecodedAsset();
            a.SourceName = name; a.Kind = "script";
            a.Files = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
            a.Files[Path.ChangeExtension(Path.GetFileName(name), ".ysc.txt")] = Encoding.UTF8.GetBytes(sb.ToString());
            a.Metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            a.Metadata["decoder"] = "ysc"; a.Metadata["status"] = "scaffold";
            return a;
        }
    }

    public sealed class MetaDecoder : IAssetDecoder
    {
        static readonly string[] Extensions = new string[] { ".ymap", ".ytyp", ".meta", ".rbf", ".pso", ".ymt" };

        public bool CanDecode(string name, byte[] data)
        {
            for (int i = 0; i < Extensions.Length; i++) if (name.EndsWith(Extensions[i], StringComparison.OrdinalIgnoreCase)) return true;
            return name.EndsWith(".pso", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".ymt", StringComparison.OrdinalIgnoreCase);
        }

        public DecodedAsset Decode(string name, byte[] data)
        {
            string xml = "<xml version=\"1.0\" encoding=\"utf-8\">\n<openrpf9 kind=\"meta\" source=\"" + System.Security.SecurityElement.Escape(name) + "\" status=\"scaffold\" bytes=\"" + data.Length + "\" />\n";
            DecodedAsset a = new DecodedAsset();
            a.SourceName = name; a.Kind = "meta";
            a.Files = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
            a.Files[Path.ChangeExtension(Path.GetFileName(name), ".xml")] = Encoding.UTF8.GetBytes(xml);
            a.Metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            a.Metadata["decoder"] = "meta"; a.Metadata["status"] = "scaffold";
            return a;
        }
    }

    public sealed class AudioDecoder : IAssetDecoder
    {
        public bool CanDecode(string name, byte[] data) { return name.EndsWith(".awc", StringComparison.OrdinalIgnoreCase); }
        public DecodedAsset Decode(string name, byte[] data)
        {
            DecodedAsset a = new DecodedAsset();
            a.SourceName = name; a.Kind = "audio";
            a.Files = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
            a.Files[Path.ChangeExtension(Path.GetFileName(name), ".wav.placeholder")] = Encoding.UTF8.GetBytes("AWC decode scaffold");
            a.Metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            a.Metadata["decoder"] = "awc"; a.Metadata["status"] = "scaffold"; a.Metadata["bytes"] = data.Length.ToString();
            return a;
        }
    }
}
