using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Windows.Forms;

namespace OpenRPF9
{
    internal static class Program
    {
        [STAThread]
        private static void Main()
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new MainForm());
        }
    }

    internal sealed class CarvedStream
    {
        public long Offset;
        public int DecompressedSize;
        public string Kind;
        public string Preview;
        public byte[] Data;
    }

    internal sealed class ArchiveInfo
    {
        public string Path;
        public long Size;
        public string MagicAscii;
        public uint MagicLE;
        public uint MagicBE;
        public string DetectedFormat;
        public bool MagicEndianReversed;
        public List<CarvedStream> Streams = new List<CarvedStream>();
    }

    internal sealed class MainForm : Form
    {
        private ToolStrip toolStrip;
        private ToolStripButton openButton;
        private ToolStripButton exportButton;
        private ToolStripButton exportAllButton;
        private SplitContainer split;
        private TreeView tree;
        private TextBox details;
        private StatusStrip status;
        private ToolStripStatusLabel statusLabel;
        private ArchiveInfo current;

        public MainForm()
        {
            Text = "OpenRPF9";
            Width = 1200;
            Height = 760;
            StartPosition = FormStartPosition.CenterScreen;
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }
            toolStrip = new ToolStrip();
            openButton = new ToolStripButton("Open RPF");
            exportButton = new ToolStripButton("Export Selected");
            exportAllButton = new ToolStripButton("Export All Streams");
            toolStrip.Items.Add(openButton);
            toolStrip.Items.Add(exportButton);
            toolStrip.Items.Add(exportAllButton);
            split = new SplitContainer();
            split.Dock = DockStyle.Fill;
            split.SplitterDistance = 460;
            tree = new TreeView();
            tree.Dock = DockStyle.Fill;
            tree.HideSelection = false;
            details = new TextBox();
            details.Dock = DockStyle.Fill;
            details.Multiline = true;
            details.ReadOnly = true;
            details.ScrollBars = ScrollBars.Both;
            details.WordWrap = false;
            details.Font = new Font("Consolas", 10f);
            status = new StatusStrip();
            statusLabel = new ToolStripStatusLabel("Open an RPF file to start.");
            status.Items.Add(statusLabel);
            split.Panel1.Controls.Add(tree);
            split.Panel2.Controls.Add(details);
            Controls.Add(split);
            Controls.Add(toolStrip);
            Controls.Add(status);
            openButton.Click += OpenButton_Click;
            exportButton.Click += ExportButton_Click;
            exportAllButton.Click += ExportAllButton_Click;
            tree.AfterSelect += Tree_AfterSelect;
        }

        private void OpenButton_Click(object sender, EventArgs e)
        {
            OpenFileDialog dlg = new OpenFileDialog();
            dlg.Filter = "RPF Archives (*.rpf)|*.rpf|All Files (*.*)|*.*";
            dlg.Title = "Open RPF";
            if (dlg.ShowDialog() != DialogResult.OK) return;
            try
            {
                statusLabel.Text = "Reading " + dlg.FileName + " ...";
                Refresh();
                Cursor = Cursors.WaitCursor;
                current = RpfScanner.Open(dlg.FileName, 500, 1024 * 1024);
                PopulateTree();
                statusLabel.Text = current.DetectedFormat + " | " + current.Streams.Count.ToString() + " streams | " + current.Path;
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, "OpenRPF9", MessageBoxButtons.OK, MessageBoxIcon.Error);
                statusLabel.Text = "Could not open file.";
            }
            finally { Cursor = Cursors.Default; }
        }

        private void PopulateTree()
        {
            tree.Nodes.Clear();
            if (current == null) return;
            TreeNode root = new TreeNode(Path.GetFileName(current.Path) + " [" + current.DetectedFormat + "]");
            root.Tag = "root";
            TreeNode info = new TreeNode("Archive Info");
            info.Tag = "info";
            root.Nodes.Add(info);
            TreeNode streamsRoot = new TreeNode("Found Streams (" + current.Streams.Count.ToString() + ")");
            streamsRoot.Tag = "streams";
            for (int i = 0; i < current.Streams.Count; i++)
            {
                CarvedStream s = current.Streams[i];
                string name = "0x" + s.Offset.ToString("X") + " | " + s.Kind + " | " + s.DecompressedSize.ToString() + " bytes";
                if (!string.IsNullOrEmpty(s.Preview))
                {
                    string p = s.Preview;
                    if (p.Length > 80) p = p.Substring(0, 80);
                    name += " | " + p;
                }
                TreeNode sn = new TreeNode(name);
                sn.Tag = s;
                streamsRoot.Nodes.Add(sn);
            }
            root.Nodes.Add(streamsRoot);
            TreeNode note = new TreeNode("TOC not readable yet (encrypted/unknown layout). Showing carved streams.");
            note.Tag = "note";
            root.Nodes.Add(note);
            tree.Nodes.Add(root);
            root.Expand();
            streamsRoot.Expand();
            tree.SelectedNode = info;
        }

        private void Tree_AfterSelect(object sender, TreeViewEventArgs e)
        {
            details.Text = GetDetails(e.Node);
            if (e.Node != null) statusLabel.Text = e.Node.Text;
        }

        private string GetDetails(TreeNode node)
        {
            if (node == null || current == null) return "";
            if (node.Tag is string)
            {
                string tag = (string)node.Tag;
                if (tag == "root" || tag == "info")
                {
                    StringBuilder sb = new StringBuilder();
                    sb.AppendLine("Path: " + current.Path);
                    sb.AppendLine("Size: " + current.Size.ToString());
                    sb.AppendLine("Magic ASCII: " + current.MagicAscii);
                    sb.AppendLine("Magic LE: 0x" + current.MagicLE.ToString("X8"));
                    sb.AppendLine("Magic BE: 0x" + current.MagicBE.ToString("X8"));
                    sb.AppendLine("Detected: " + current.DetectedFormat);
                    sb.AppendLine("Endian Reversed: " + current.MagicEndianReversed.ToString());
                    sb.AppendLine("Carved Streams: " + current.Streams.Count.ToString());
                    sb.AppendLine();
                    sb.AppendLine("Notes:");
                    sb.AppendLine("- Reads RPF magic as LE, BE, or reversed bytes (RPF9 / 9FPR).");
                    sb.AppendLine("- TOC is not parsed for RPF9 yet; keys/layout are unknown.");
                    sb.AppendLine("- Streams listed here are found by scanning for zlib headers and inflating them.");
                    sb.AppendLine("- Export writes the decompressed bytes.");
                    return sb.ToString();
                }
                if (tag == "streams") return "Zlib streams found by scanning the file. Pick one to preview or export.";
                if (tag == "note") return "TOC is encrypted or unknown for this RPF9 sample. The list below comes from zlib carving, not TOC entries.";
            }
            CarvedStream s = node.Tag as CarvedStream;
            if (s != null)
            {
                StringBuilder sb = new StringBuilder();
                sb.AppendLine("Offset: 0x" + s.Offset.ToString("X") + " (" + s.Offset.ToString() + ")");
                sb.AppendLine("Kind: " + s.Kind);
                sb.AppendLine("Decompressed Size: " + s.DecompressedSize.ToString());
                sb.AppendLine("Preview: " + s.Preview);
                sb.AppendLine();
                sb.AppendLine("Hex Preview:");
                sb.Append(RpfScanner.HexPreview(s.Data, 512));
                if (s.Kind == "text")
                {
                    sb.AppendLine();
                    sb.AppendLine("Text Preview:");
                    string text = Encoding.UTF8.GetString(s.Data);
                    if (text.Length > 4000) text = text.Substring(0, 4000);
                    sb.AppendLine(text);
                }
                return sb.ToString();
            }
            return "";
        }

        private void ExportButton_Click(object sender, EventArgs e)
        {
            if (current == null || tree.SelectedNode == null) return;
            CarvedStream s = tree.SelectedNode.Tag as CarvedStream;
            if (s == null) { MessageBox.Show("Pick a stream first.", "OpenRPF9"); return; }
            FolderBrowserDialog dlg = new FolderBrowserDialog();
            dlg.Description = "Pick export folder";
            if (dlg.ShowDialog() != DialogResult.OK) return;
            string ext = s.Kind == "text" ? ".txt" : ".bin";
            string dest = Path.Combine(dlg.SelectedPath, "stream_0x" + s.Offset.ToString("X") + ext);
            File.WriteAllBytes(dest, s.Data);
            MessageBox.Show("Saved:\r\n" + dest, "OpenRPF9");
        }

        private void ExportAllButton_Click(object sender, EventArgs e)
        {
            if (current == null) return;
            FolderBrowserDialog dlg = new FolderBrowserDialog();
            dlg.Description = "Pick export folder";
            if (dlg.ShowDialog() != DialogResult.OK) return;
            string baseDir = Path.Combine(dlg.SelectedPath, Path.GetFileNameWithoutExtension(current.Path) + "_carved");
            Directory.CreateDirectory(baseDir);
            int count = 0;
            for (int i = 0; i < current.Streams.Count; i++)
            {
                CarvedStream s = current.Streams[i];
                string ext = s.Kind == "text" ? ".txt" : ".bin";
                string name = "stream_" + i.ToString("D4") + "_0x" + s.Offset.ToString("X") + ext;
                File.WriteAllBytes(Path.Combine(baseDir, name), s.Data);
                count++;
            }
            MessageBox.Show("Saved " + count.ToString() + " files to:\r\n" + baseDir, "OpenRPF9");
        }
    }

    internal static class RpfScanner
    {
        public static ArchiveInfo Open(string path, int maxStreams, int maxOutputBytes)
        {
            byte[] bytes = File.ReadAllBytes(path);
            if (bytes.Length < 4) throw new InvalidDataException("File too small.");
            ArchiveInfo info = new ArchiveInfo();
            info.Path = Path.GetFullPath(path);
            info.Size = bytes.Length;
            info.MagicAscii = Encoding.ASCII.GetString(bytes, 0, 4);
            info.MagicLE = BitConverter.ToUInt32(bytes, 0);
            info.MagicBE = ReadU32BE(bytes, 0);
            info.DetectedFormat = DetectFormat(bytes);
            info.MagicEndianReversed = IsReversedRpfMagic(info.MagicAscii);
            info.Streams = FindStreams(bytes, maxStreams, maxOutputBytes);
            return info;
        }

        private static uint ReadU32BE(byte[] b, int o)
        {
            return ((uint)b[o] << 24) | ((uint)b[o + 1] << 16) | ((uint)b[o + 2] << 8) | b[o + 3];
        }

        private static bool IsRpfMagic(uint v)
        {
            return v == 0x52504630u || v == 0x52504632u || v == 0x52504633u || v == 0x52504634u ||
                   v == 0x52504636u || v == 0x52504637u || v == 0x52504638u || v == 0x52504639u;
        }

        private static string MagicToName(uint v)
        {
            if (v == 0x52504630u) return "RPF0";
            if (v == 0x52504632u) return "RPF2";
            if (v == 0x52504633u) return "RPF3";
            if (v == 0x52504634u) return "RPF4";
            if (v == 0x52504636u) return "RPF6";
            if (v == 0x52504637u) return "RPF7";
            if (v == 0x52504638u) return "RPF8";
            if (v == 0x52504639u) return "RPF9";
            return "UNKNOWN";
        }

        private static bool IsReversedRpfMagic(string ascii)
        {
            if (ascii == null || ascii.Length < 4) return false;
            char[] arr = ascii.ToCharArray();
            Array.Reverse(arr);
            string rev = new string(arr);
            return rev.StartsWith("RPF");
        }

        private static string DetectFormat(byte[] bytes)
        {
            uint le = BitConverter.ToUInt32(bytes, 0);
            uint be = ReadU32BE(bytes, 0);
            if (IsRpfMagic(le)) return MagicToName(le) + " (LE magic)";
            if (IsRpfMagic(be)) return MagicToName(be) + " (BE magic)";
            string ascii = Encoding.ASCII.GetString(bytes, 0, 4);
            if (IsReversedRpfMagic(ascii))
            {
                char[] arr = ascii.ToCharArray();
                Array.Reverse(arr);
                return new string(arr) + " (reversed bytes on disk: " + ascii + ")";
            }
            return "Unknown (" + ascii + ")";
        }

        private static List<CarvedStream> FindStreams(byte[] bytes, int maxStreams, int maxOutputBytes)
        {
            List<CarvedStream> list = new List<CarvedStream>();
            int i = 0;
            while (i < bytes.Length - 2 && list.Count < maxStreams)
            {
                byte b = bytes[i];
                if (b != 0x78) { i++; continue; }
                byte b2 = bytes[i + 1];
                if (b2 != 0x01 && b2 != 0x5E && b2 != 0x9C && b2 != 0xDA) { i++; continue; }
                byte[] data;
                int consumed;
                if (!TryDecompress(bytes, i, maxOutputBytes, out data, out consumed)) { i++; continue; }
                if (data == null || data.Length == 0) { i++; continue; }
                CarvedStream s = new CarvedStream();
                s.Offset = i;
                s.Data = data;
                s.DecompressedSize = data.Length;
                s.Kind = DetectKind(data);
                s.Preview = MakePreview(data, s.Kind);
                list.Add(s);
                int next = i + consumed;
                next = (next + 0xF) & ~0xF;
                if (next <= i) next = i + 1;
                i = next;
            }
            return list;
        }

        private static bool TryDecompress(byte[] bytes, int offset, int maxOutputBytes, out byte[] data, out int consumed)
        {
            data = null;
            consumed = 0;
            try
            {
                MemoryStream ms = new MemoryStream(bytes, offset, bytes.Length - offset, false);
                try
                {
                    ms.ReadByte();
                    ms.ReadByte();
                    DeflateStream ds = new DeflateStream(ms, CompressionMode.Decompress, false);
                    try
                    {
                        MemoryStream outMs = new MemoryStream();
                        try
                        {
                            ds.CopyTo(outMs);
                            data = outMs.ToArray();
                            consumed = (int)ms.Position;
                            if (maxOutputBytes > 0 && data.Length > maxOutputBytes)
                            {
                                byte[] trimmed = new byte[maxOutputBytes];
                                Array.Copy(data, 0, trimmed, 0, maxOutputBytes);
                                data = trimmed;
                            }
                            return data.Length > 0;
                        }
                        finally { outMs.Dispose(); }
                    }
                    finally { ds.Dispose(); }
                }
                finally { ms.Dispose(); }
            }
            catch { return false; }
        }

        private static string DetectKind(byte[] data)
        {
            if (data.Length >= 3 && data[0] == 0xEF && data[1] == 0xBB && data[2] == 0xBF) return "text";
            int len = data.Length;
            if (len > 256) len = 256;
            int printable = 0;
            for (int i = 0; i < len; i++)
            {
                byte b = data[i];
                if ((b >= 32 && b <= 126) || b == 9 || b == 10 || b == 13) printable++;
            }
            if (len > 0 && ((double)printable / len) > 0.85) return "text";
            return "binary";
        }

        private static string MakePreview(byte[] data, string kind)
        {
            if (kind == "text")
            {
                string text = Encoding.UTF8.GetString(data);
                text = text.Replace("\r", " ").Replace("\n", " ").Trim();
                if (text.Length > 140) text = text.Substring(0, 140);
                return text;
            }
            int n = data.Length;
            if (n > 16) n = 16;
            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < n; i++)
            {
                if (i > 0) sb.Append(" ");
                sb.Append(data[i].ToString("X2"));
            }
            return sb.ToString();
        }

        public static string HexPreview(byte[] data, int maxBytes)
        {
            int len = data.Length;
            if (len > maxBytes) len = maxBytes;
            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < len; i += 16)
            {
                int lineLen = len - i;
                if (lineLen > 16) lineLen = 16;
                sb.Append(i.ToString("X8"));
                sb.Append("  ");
                for (int j = 0; j < 16; j++)
                {
                    if (j < lineLen) { sb.Append(data[i + j].ToString("X2")); sb.Append(" "); }
                    else sb.Append("   ");
                    if (j == 7) sb.Append(" ");
                }
                sb.Append(" ");
                for (int j = 0; j < lineLen; j++)
                {
                    byte b = data[i + j];
                    if (b >= 32 && b <= 126) sb.Append((char)b);
                    else sb.Append('.');
                }
                sb.AppendLine();
            }
            return sb.ToString();
        }
    }
}
