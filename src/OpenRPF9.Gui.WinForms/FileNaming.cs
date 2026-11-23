using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;

namespace OpenRPF9
{
    internal static class FileNaming
    {
        private const int MaxStemLength = 64;

        internal static readonly Regex FileNameAttribute = new Regex(
            "\\b(?:filename|fileName|FileName)\\s*=\\s*[\"']([^\"']{1,160})[\"']",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        internal static readonly Regex NameAttribute = new Regex(
            "\\b(?:name|Name)\\s*=\\s*[\"']([^\"']{1,160})[\"']",
            RegexOptions.CultureInvariant);

        internal static readonly Regex RootElement = new Regex(
            "^\\s*(?:<\\?xml[^>]*\\?>\\s*)?(?:<!--[\\s\\S]*?-->\\s*)*<([A-Za-z_][A-Za-z0-9_.:-]*)",
            RegexOptions.CultureInvariant);

        private static readonly string[] ReservedNames = new string[]
        {
            "con", "prn", "aux", "nul",
            "com1", "com2", "com3", "com4", "com5", "com6", "com7", "com8", "com9",
            "lpt1", "lpt2", "lpt3", "lpt4", "lpt5", "lpt6", "lpt7", "lpt8", "lpt9"
        };

        public static void Assign(List<FileEntry> entries)
        {
            if (entries == null) return;
            Dictionary<string, int> used = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < entries.Count; i++)
            {
                FileEntry e = entries[i];
                if (e == null) continue;
                e.Name = BuildPath(e, used);
            }
        }

        private static string BuildPath(FileEntry e, Dictionary<string, int> used)
        {
            string dir = DirectoryFor(e.Kind);
            string stem = SanitizeStem(StemFor(e));
            string ext = ExtensionFor(e);
            return Unique(used, dir, stem, ext);
        }

        private static string StemFor(FileEntry e)
        {
            if (e == null) return "";
            switch (e.Kind)
            {
                case "texture":
                    if (e.TextureDict != null) return TextureDictionaryStem(e.TextureDict);
                    return "texture_dictionary";
                case "ytd":
                    return "texture_dictionary";
                case "awc":
                    if (e.Awc != null) return AwcStem(e.Awc);
                    return "awc_bundle";
                case "audio":
                    return "sound";
                case "video":
                    return "movie";
                case "image":
                    return ImageStem(e.Data);
                case "xml":
                case "meta":
                case "ps":
                {
                    string xml = XmlCarver.FriendlyStem(e.Data);
                    if (xml.Length > 0) return xml;
                    return "xml_document";
                }
                case "json":
                    return "json_document";
                case "csv":
                    return "data_table";
                case "text":
                {
                    string line = FirstContentLine(e.Data);
                    if (line.Length > 0) return line;
                    return "text_note";
                }
                case "rsc":
                    return "resource";
                case "pso":
                    return "meta_resource";
                default:
                    return "binary";
            }
        }

        private static string TextureDictionaryStem(RageTextureDictionary dict)
        {
            if (dict == null || dict.Textures == null || dict.Textures.Count == 0)
                return "texture_dictionary";

            List<string> names = new List<string>();
            for (int i = 0; i < dict.Textures.Count && names.Count < 32; i++)
            {
                string n = dict.Textures[i] == null ? null : dict.Textures[i].Name;
                if (string.IsNullOrEmpty(n)) continue;
                if (IsHashPlaceholder(n)) continue;
                names.Add(n);
            }
            if (names.Count == 0) return "texture_dictionary";
            if (names.Count == 1) return names[0];

            string prefix = CommonPrefix(names);
            if (prefix.Length >= 4) return prefix;
            return "texture_dictionary";
        }

        private static bool IsHashPlaceholder(string name)
        {
            return Regex.IsMatch(name, "^texture_[0-9A-Fa-f]{8,16}$");
        }

        private static string CommonPrefix(List<string> names)
        {
            if (names == null || names.Count == 0) return "";
            string prefix = names[0];
            for (int i = 1; i < names.Count; i++)
            {
                string other = names[i];
                int max = Math.Min(prefix.Length, other.Length);
                int j = 0;
                while (j < max && char.ToLowerInvariant(prefix[j]) == char.ToLowerInvariant(other[j])) j++;
                prefix = prefix.Substring(0, j);
                if (prefix.Length == 0) return "";
            }
            int cut = prefix.LastIndexOf('_');
            if (cut > 0) prefix = prefix.Substring(0, cut);
            return prefix.Trim('_', '-', ' ');
        }

        private static string AwcStem(AwcFile awc)
        {
            if (awc == null) return "awc_bundle";
            string codec = null;
            int channels = 0;
            int rate = 0;
            for (int i = 0; i < awc.Streams.Count; i++)
            {
                AwcStreamInfo s = awc.Streams[i];
                if (s == null || s.Codec == -1) continue;
                if (string.IsNullOrEmpty(s.CodecName)) continue;
                codec = s.CodecName;
                channels = s.Channels;
                rate = s.SampleRate;
                break;
            }
            if (codec == null && rate <= 0) return awc.IsEncrypted ? "encrypted_awc" : "awc_bundle";

            StringBuilder sb = new StringBuilder();
            sb.Append(awc.IsMusic ? "music" : "awc");
            if (codec != null)
            {
                sb.Append('_');
                sb.Append(codec);
            }
            if (rate > 0)
            {
                sb.Append('_');
                sb.Append(rate);
                sb.Append("hz");
            }
            if (channels > 0)
            {
                sb.Append('_');
                sb.Append(channels);
                sb.Append("ch");
            }
            if (awc.Streams.Count > 1)
            {
                sb.Append('_');
                sb.Append(awc.Streams.Count);
                sb.Append("stems");
            }
            return sb.ToString();
        }

        private static string ImageStem(byte[] data)
        {
            int width = 0;
            int height = 0;
            if (data != null && data.Length >= 24 &&
                data[0] == 'D' && data[1] == 'D' && data[2] == 'S' && data[3] == ' ')
            {
                height = ReadU32(data, 12);
                width = ReadU32(data, 16);
            }
            else if (data != null && data.Length >= 24 &&
                     data[0] == 0x89 && data[1] == 'P' && data[2] == 'N' && data[3] == 'G')
            {
                width = ReadU32Be(data, 16);
                height = ReadU32Be(data, 20);
            }
            if (width > 0 && height > 0 && width <= 65536 && height <= 65536)
                return "image_" + width + "x" + height;
            return "image";
        }

        private static int ReadU32(byte[] d, int offset)
        {
            if (d == null || offset < 0 || offset + 4 > d.Length) return 0;
            return d[offset] | (d[offset + 1] << 8) | (d[offset + 2] << 16) | (d[offset + 3] << 24);
        }

        private static int ReadU32Be(byte[] d, int offset)
        {
            if (d == null || offset < 0 || offset + 4 > d.Length) return 0;
            return (d[offset] << 24) | (d[offset + 1] << 16) | (d[offset + 2] << 8) | d[offset + 3];
        }

        private static string FirstContentLine(byte[] data)
        {
            string text = XmlCarver.HeadText(data, 512);
            if (text.Length == 0) return "";
            int nl = text.IndexOf('\n');
            if (nl >= 0) text = text.Substring(0, nl);
            text = text.Trim();
            if (text.Length < 3 || text.Length > 60) return "";
            return text;
        }

        private static string DirectoryFor(string kind)
        {
            switch (kind)
            {
                case "texture":
                case "ytd":
                case "image":
                    return "textures";
                case "awc":
                case "audio":
                    return "audio";
                case "video":
                    return "video";
                case "xml":
                case "meta":
                case "ps":
                case "json":
                case "csv":
                case "text":
                    return "text";
                case "rsc":
                case "pso":
                    return "resources";
                default:
                    return "bin";
            }
        }

        private static string ExtensionFor(FileEntry e)
        {
            if (e == null) return ".bin";
            switch (e.Kind)
            {
                case "texture": return (e.TextureDict != null && e.TextureDict.Textures != null && e.TextureDict.Textures.Count > 0) ? ".png" : ".bin";
                case "awc": return ".awc";
                case "video": return ".bk2";
                case "audio":
                    if (LooksLikeWav(e.Data)) return ".wav";
                    if (LooksLikeOgg(e.Data)) return ".ogg";
                    if (LooksLikeMp3(e.Data)) return ".mp3";
                    return ".bin";
                case "image":
                    if (LooksLikePng(e.Data)) return ".png";
                    if (LooksLikeJpg(e.Data)) return ".jpg";
                    if (LooksLikeDds(e.Data)) return ".dds";
                    return ".img";
                case "ytd": return ".ytd";
                case "rsc": return ".rsc";
                case "pso": return ".pso";
                case "xml": return ".xml";
                case "meta": return ".meta";
                case "ps": return ".ps";
                case "json": return ".json";
                case "csv": return ".csv";
                case "text": return ".txt";
                default: return ".bin";
            }
        }

        private static bool LooksLikeWav(byte[] d)
        {
            return d != null && d.Length >= 12 && d[0] == 'R' && d[1] == 'I' && d[2] == 'F' && d[3] == 'F' &&
                   d[8] == 'W' && d[9] == 'A' && d[10] == 'V' && d[11] == 'E';
        }

        private static bool LooksLikeOgg(byte[] d)
        {
            return d != null && d.Length >= 4 && d[0] == 'O' && d[1] == 'g' && d[2] == 'g' && d[3] == 'S';
        }

        private static bool LooksLikeMp3(byte[] d)
        {
            return d != null && d.Length >= 3 &&
                   ((d[0] == 0xFF && (d[1] & 0xE0) == 0xE0) || (d[0] == 'I' && d[1] == 'D' && d[2] == '3'));
        }

        private static bool LooksLikePng(byte[] d)
        {
            return d != null && d.Length >= 8 && d[0] == 0x89 && d[1] == 'P' && d[2] == 'N' && d[3] == 'G';
        }

        private static bool LooksLikeJpg(byte[] d)
        {
            return d != null && d.Length >= 3 && d[0] == 0xFF && d[1] == 0xD8 && d[2] == 0xFF;
        }

        private static bool LooksLikeDds(byte[] d)
        {
            return d != null && d.Length >= 4 && d[0] == 'D' && d[1] == 'D' && d[2] == 'S' && d[3] == ' ';
        }

        private static string SanitizeStem(string name)
        {
            if (string.IsNullOrEmpty(name)) return "";
            StringBuilder sb = new StringBuilder(name.Length);
            bool lastUnderscore = false;
            for (int i = 0; i < name.Length; i++)
            {
                char c = char.ToLowerInvariant(name[i]);
                if ((c >= 'a' && c <= 'z') || (c >= '0' && c <= '9'))
                {
                    sb.Append(c);
                    lastUnderscore = false;
                }
                else if (c == '_' || c == '-' || c == ' ' || c == '.' || c == '/' || c == '\\')
                {
                    if (!lastUnderscore && sb.Length > 0)
                    {
                        sb.Append('_');
                        lastUnderscore = true;
                    }
                }
            }
            string s = sb.ToString().Trim('_');
            if (s.Length > MaxStemLength)
            {
                int cut = s.LastIndexOf('_', MaxStemLength - 1);
                s = cut > 8 ? s.Substring(0, cut) : s.Substring(0, MaxStemLength);
                s = s.Trim('_');
            }
            for (int i = 0; i < ReservedNames.Length; i++)
            {
                if (string.Equals(s, ReservedNames[i], StringComparison.OrdinalIgnoreCase))
                {
                    s = "file_" + s;
                    break;
                }
            }
            return s;
        }

        private static string Unique(Dictionary<string, int> used, string dir, string stem, string ext)
        {
            if (string.IsNullOrEmpty(stem)) stem = "file";
            string basePath = dir + "/" + stem + ext;
            if (!used.ContainsKey(basePath))
            {
                used[basePath] = 1;
                return basePath;
            }
            for (int n = 2; n < 100000; n++)
            {
                string candidate = dir + "/" + stem + "_" + n + ext;
                if (!used.ContainsKey(candidate))
                {
                    used[candidate] = n;
                    return candidate;
                }
            }
            used[basePath] = used[basePath] + 1;
            return dir + "/" + stem + "_" + used[basePath] + ext;
        }
    }

    internal static class XmlCarver
    {
        private static readonly XmlReaderSettings ReaderSettings = CreateReaderSettings();

        private static XmlReaderSettings CreateReaderSettings()
        {
            XmlReaderSettings settings = new XmlReaderSettings();
            settings.CheckCharacters = false;
            settings.DtdProcessing = DtdProcessing.Ignore;
            settings.IgnoreComments = true;
            settings.IgnoreProcessingInstructions = false;
            settings.ConformanceLevel = ConformanceLevel.Document;
            return settings;
        }

        public static bool IsXml(byte[] data)
        {
            byte[] n = Normalize(data);
            if (n == null || n.Length < 2 || n[0] != (byte)'<') return false;
            if (StartsWithAscii(n, 0, "<?xml")) return true;
            if (n.Length < 3) return false;
            char c = (char)n[1];
            return (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') || c == '_' || c == '!' || c == '?';
        }

        public static bool HasRsc7Header(byte[] data)
        {
            if (data == null || data.Length < 4) return false;
            int start = 0;
            if (data.Length >= 3 && data[0] == 0xEF && data[1] == 0xBB && data[2] == 0xBF) start = 3;
            return data.Length - start >= 4 && data[start] == 'R' && data[start + 1] == 'S' &&
                   data[start + 2] == 'C' && data[start + 3] == '7';
        }

        public static bool StartsWithDeclaration(byte[] data)
        {
            byte[] n = Normalize(data);
            return n != null && StartsWithAscii(n, 0, "<?xml");
        }

        public static bool MayEndDocument(byte[] part, byte[] head)
        {
            byte[] n = Normalize(part);
            if (n == null || n.Length < 3) return false;
            int end = n.Length - 1;
            while (end >= 0 && (n[end] == 0 || n[end] == ' ' || n[end] == '\t' || n[end] == '\r' || n[end] == '\n')) end--;
            if (end < 0 || n[end] != (byte)'>') return false;

            string headText = HeadText(head, 4096);
            Match root = FileNaming.RootElement.Match(headText);
            if (!root.Success) return false;
            string close = "</" + root.Groups[1].Value;

            int tailStart = Math.Max(0, n.Length - 1024);
            string tail = Encoding.UTF8.GetString(n, tailStart, n.Length - tailStart);
            return tail.IndexOf(close, StringComparison.Ordinal) >= 0;
        }

        public static int MeasureDocument(byte[] data)
        {
            byte[] n = Normalize(data);
            if (n == null || n.Length < 8 || !IsXml(n)) return 0;

            int declClose = StartsWithAscii(n, 0, "<?xml") ? AsciiIndexOf(n, "?>", 0) : -1;
            int p = declClose >= 0 ? declClose + 2 : 0;
            while (true)
            {
                while (p < n.Length && (n[p] == ' ' || n[p] == '\t' || n[p] == '\r' || n[p] == '\n')) p++;
                if (p + 3 < n.Length && n[p] == '<' && n[p + 1] == '!' && n[p + 2] == '-' && n[p + 3] == '-')
                {
                    int commentEnd = AsciiIndexOf(n, "-->", p + 4);
                    if (commentEnd < 0) return 0;
                    p = commentEnd + 3;
                    continue;
                }
                break;
            }
            if (p >= n.Length || n[p] != '<') return 0;

            int nameStart = p + 1;
            int nameEnd = nameStart;
            while (nameEnd < n.Length && IsNameChar(n[nameEnd])) nameEnd++;
            if (nameEnd == nameStart) return 0;
            string name = Encoding.ASCII.GetString(n, nameStart, nameEnd - nameStart);

            int openEnd = nameEnd;
            while (openEnd < n.Length && n[openEnd] != '>') openEnd++;
            if (openEnd >= n.Length) return 0;
            if (n[openEnd - 1] == (byte)'/') return openEnd + 1;

            string close = "</" + name;
            int closeStart = AsciiIndexOf(n, close, nameEnd);
            if (closeStart < 0) return 0;
            int closeEnd = closeStart + close.Length;
            while (closeEnd < n.Length && n[closeEnd] != '>') closeEnd++;
            if (closeEnd >= n.Length) return 0;
            return closeEnd + 1;
        }

        private static bool IsNameChar(byte c)
        {
            return (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') ||
                   (c >= '0' && c <= '9') || c == '_' || c == '-' || c == '.' || c == ':';
        }

        public static bool IsStandaloneDocument(byte[] data)
        {
            byte[] n = Normalize(data);
            return n != null && n.Length > 0 && IsXml(n) && IsWellFormed(n);
        }

        public static bool IsWellFormed(byte[] data)
        {
            byte[] n = Normalize(data);
            if (n == null || n.Length == 0) return false;
            try
            {
                using (MemoryStream stream = new MemoryStream(n, false))
                using (XmlReader reader = XmlReader.Create(stream, ReaderSettings))
                {
                    while (reader.Read()) { }
                }
                return true;
            }
            catch (XmlException)
            {
                return false;
            }
            catch (ArgumentException)
            {
                return false;
            }
            catch (InvalidOperationException)
            {
                return false;
            }
        }

        public static byte[] Normalize(byte[] data)
        {
            if (data == null || data.Length == 0) return data;
            int start = 0;
            int end = data.Length;

            start = SkipBom(data, start, end);
            if (end - start >= 16 &&
                data[start] == 'R' && data[start + 1] == 'S' && data[start + 2] == 'C' && data[start + 3] == '7')
                start += 16;
            start = SkipBom(data, start, end);

            while (start < end && (data[start] == 0 || data[start] == ' ' || data[start] == '\t' ||
                                   data[start] == '\r' || data[start] == '\n'))
                start++;
            while (end > start && data[end - 1] == 0) end--;

            if (start == 0 && end == data.Length) return data;
            byte[] result = new byte[end - start];
            Array.Copy(data, start, result, 0, result.Length);
            return result;
        }

        private static int SkipBom(byte[] data, int start, int end)
        {
            if (end - start >= 3 && data[start] == 0xEF && data[start + 1] == 0xBB && data[start + 2] == 0xBF)
                return start + 3;
            return start;
        }

        public static byte[] Merge(List<byte[]> parts)
        {
            if (parts == null || parts.Count == 0) return new byte[0];
            MemoryStream ms = new MemoryStream();
            for (int i = 0; i < parts.Count; i++)
            {
                byte[] part = Normalize(parts[i]);
                if (part == null || part.Length == 0) continue;
                int offset = 0;
                if (StartsWithAscii(part, 0, "<?xml"))
                {
                    int close = AsciiIndexOf(part, "?>", 0);
                    if (close >= 0 && ms.Length > 0) offset = close + 2;
                }
                if (ms.Length > 0 && offset == 0) ms.WriteByte((byte)'\n');
                ms.Write(part, offset, part.Length - offset);
            }
            return ms.ToArray();
        }

        public static string FriendlyStem(byte[] data)
        {
            string text = HeadText(data, 8192);
            if (text.Length == 0) return "";

            Match file = FileNaming.FileNameAttribute.Match(text);
            if (file.Success)
            {
                string stem = StemFromPath(file.Groups[1].Value);
                if (stem.Length > 0) return stem;
            }

            Match name = FileNaming.NameAttribute.Match(text);
            if (name.Success && !IsHashPlaceholder(name.Groups[1].Value))
            {
                string stem = StemFromPath(name.Groups[1].Value);
                if (stem.Length > 0) return stem;
            }

            Match root = FileNaming.RootElement.Match(text);
            if (root.Success)
            {
                string known = KnownRoot(root.Groups[1].Value);
                if (known != null) return known;
                string snake = PascalToSnake(StripTypePrefix(root.Groups[1].Value));
                if (snake.Length > 0) return snake;
            }
            return "";
        }

        public static string HeadText(byte[] data, int maxBytes)
        {
            byte[] n = Normalize(data);
            if (n == null || n.Length == 0) return "";
            int length = Math.Min(n.Length, Math.Max(1, maxBytes));
            return Encoding.UTF8.GetString(n, 0, length).Trim('\uFEFF', ' ', '\t', '\r', '\n', '\0');
        }

        private static string StemFromPath(string value)
        {
            if (string.IsNullOrEmpty(value)) return "";
            string v = value.Trim();
            int slash = Math.Max(v.LastIndexOf('/'), v.LastIndexOf('\\'));
            if (slash >= 0) v = v.Substring(slash + 1);
            int dot = v.LastIndexOf('.');
            if (dot > 0) v = v.Substring(0, dot);
            if (v.Length < 3 || v.Length > 80) return "";
            for (int i = 0; i < v.Length; i++)
            {
                char c = v[i];
                bool ok = (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') ||
                          (c >= '0' && c <= '9') || c == '_' || c == '-' || c == ' ';
                if (!ok) return "";
            }
            return v;
        }

        private static bool IsHashPlaceholder(string value)
        {
            if (string.IsNullOrEmpty(value)) return true;
            return Regex.IsMatch(value.Trim(), "^[0-9A-Fa-f]{8,16}$");
        }

        private static string KnownRoot(string root)
        {
            switch (root)
            {
                case "CMapData": return "map_data";
                case "CVehicleModelInfo__InitDataList": return "vehicles";
                case "CPedModelInfo__InitDataList": return "peds";
                case "CWeaponInfoBlob": return "weapons";
                case "CHandlingDataMgr": return "handling";
                case "CMapParentTxds": return "texture_relationships";
                case "CVehicleColors": return "vehicle_colors";
                case "CVehicleModelInfoVarGlobal": return "vehicle_globals";
                case "CScenarioPointManifest": return "scenario_points";
                case "CTimeCycleMods": return "time_cycle";
                case "CGameDataMgr": return "game_data";
                default: return null;
            }
        }

        private static string StripTypePrefix(string root)
        {
            if (root.Length > 2 && root[0] == 'C' && root[1] >= 'A' && root[1] <= 'Z')
                return root.Substring(1);
            return root;
        }

        private static string PascalToSnake(string value)
        {
            if (string.IsNullOrEmpty(value)) return "";
            StringBuilder sb = new StringBuilder(value.Length + 8);
            for (int i = 0; i < value.Length; i++)
            {
                char c = value[i];
                if ((c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9'))
                {
                    bool previousLower = i > 0 && ((value[i - 1] >= 'a' && value[i - 1] <= 'z') ||
                                                   (value[i - 1] >= '0' && value[i - 1] <= '9'));
                    bool nextLower = i + 1 < value.Length && value[i + 1] >= 'a' && value[i + 1] <= 'z';
                    bool previousUpper = i > 0 && value[i - 1] >= 'A' && value[i - 1] <= 'Z';
                    if (sb.Length > 0 && sb[sb.Length - 1] != '_' && (previousLower || (previousUpper && nextLower)))
                        sb.Append('_');
                    sb.Append(char.ToLowerInvariant(c));
                }
                else if (c == '_' || c == ' ' || c == '-' || c == '.')
                {
                    if (sb.Length > 0 && sb[sb.Length - 1] != '_') sb.Append('_');
                }
            }
            string s = sb.ToString().Trim('_');
            while (s.IndexOf("__", StringComparison.Ordinal) >= 0)
                s = s.Replace("__", "_");
            return s;
        }

        private static bool StartsWithAscii(byte[] data, int offset, string text)
        {
            if (data == null || text == null || offset < 0 || offset + text.Length > data.Length) return false;
            for (int i = 0; i < text.Length; i++)
                if (data[offset + i] != (byte)text[i]) return false;
            return true;
        }

        private static int AsciiIndexOf(byte[] data, string text, int start)
        {
            if (data == null || text == null || text.Length == 0) return -1;
            int last = data.Length - text.Length;
            for (int i = Math.Max(0, start); i <= last; i++)
                if (StartsWithAscii(data, i, text)) return i;
            return -1;
        }
    }
}
