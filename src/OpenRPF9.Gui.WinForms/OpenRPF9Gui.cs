using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Media;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;
using System.Windows.Forms.Integration;

namespace OpenRPF9
{
    internal static class Program
    {
        [DllImport("kernel32.dll")]
        private static extern bool AttachConsole(int processId);

        [STAThread]
        private static int Main(string[] args)
        {
            try
            {
                if (args != null && args.Length > 0) AttachCliConsole();
                AppLog.Init();
                AppSettings startupSettings = AppSettings.Load();
                if (string.IsNullOrEmpty(startupSettings.FfmpegPath))
                {
                    string autoFfmpeg = Ffmpeg.TryAutoDetect();
                    if (!string.IsNullOrEmpty(autoFfmpeg)) { startupSettings.FfmpegPath = autoFfmpeg; startupSettings.Save(); }
                }
                Ffmpeg.Configure(startupSettings.FfmpegPath);
                if (args != null && args.Length > 1 && string.Equals(args[0], "--scan", StringComparison.OrdinalIgnoreCase)) { ScanCli(args[1]); return 0; }
                if (args != null && args.Length > 3 && string.Equals(args[0], "--dump-awc-stem", StringComparison.OrdinalIgnoreCase)) { DumpAwcCli(args[1], int.Parse(args[2]), args[3]); return 0; }
                if (args != null && args.Length > 1 && string.Equals(args[0], "--dump-awc", StringComparison.OrdinalIgnoreCase)) { DumpAwcCli(args[1]); return 0; }
                if (args != null && args.Length > 2 && string.Equals(args[0], "--extract-awc", StringComparison.OrdinalIgnoreCase)) { int n = AwcExtractTool.Run(args[1], args[2]); Console.WriteLine("extracted=" + n); return 0; }
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.Run(new MainForm());
                return 0;
            }
            catch (Exception ex)
            {
                try { AppLog.Line("FATAL: " + ex); } catch { }
                try { MessageBox.Show(ex.ToString(), "OpenRPF9 fatal error", MessageBoxButtons.OK, MessageBoxIcon.Error); } catch { }
                return 1;
            }
        }

        private static void AttachCliConsole()
        {
            try
            {
                if (!AttachConsole(-1)) return;
                Console.SetOut(new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true });
                Console.SetError(new StreamWriter(Console.OpenStandardError()) { AutoFlush = true });
            }
            catch { }
        }

        private static void ScanCli(string path)
        {
            try { ArchiveInfo info = RpfScanner.Scan(path, CancellationToken.None, delegate(long p, long t, int f) { Console.WriteLine(p + "/" + t + " found=" + f); }, null); Console.WriteLine("format=" + info.DetectedFormat); Console.WriteLine("entries=" + (info.Entries == null ? 0 : info.Entries.Count)); }
            catch (Exception ex) { Console.WriteLine(ex.ToString()); }
        }

        public static Icon LoadAppIcon() { try { string p = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "icon.ico"); if (File.Exists(p)) return new Icon(p); } catch { } return SystemIcons.Application; }

        private static void DumpAwcCli(string path) { DumpAwcCli(path, -1, null); }
        private static void DumpAwcCli(string path, int stem, string outDir)
        {
            try
            {
                byte[] data = File.ReadAllBytes(path);
                AwcFile awc = RpfScanner.ParseAwc(data);
                Console.WriteLine("file=" + path);
                Console.WriteLine("len=" + data.Length);
                Console.WriteLine("streams=" + awc.Streams.Count);
                if (stem >= 0 && outDir != null) { Directory.CreateDirectory(outDir); }
                for (int i = 0; i < awc.Streams.Count; i++)
                {
                    AwcStreamInfo s = awc.Streams[i];
                    if (stem >= 0 && i != stem) continue;
                    RpfScanner.EnsureDecoded(s);
                    string err = s.DecodeError ?? "";
                    Console.WriteLine("[" + i + "] codec=" + s.CodecName + "(" + s.Codec + ") hz=" + s.SampleRate + " ch=" + s.Channels + " samples=" + s.Samples + " off=0x" + s.Offset.ToString("X") + " sz=" + s.Size + " err=" + err);
                    if (outDir != null) { File.WriteAllBytes(Path.Combine(outDir, "stem_" + i + ".bin"), s.Data ?? new byte[0]); if (s.DecodedWav != null) File.WriteAllBytes(Path.Combine(outDir, "stem_" + i + ".wav"), s.DecodedWav); File.WriteAllText(Path.Combine(outDir, "stem_" + i + ".txt"), "codec=" + s.CodecName + "(" + s.Codec + ") hz=" + s.SampleRate + " ch=" + s.Channels + " samples=" + s.Samples + " err=" + err); }
                }
            }
            catch (Exception ex) { Console.WriteLine("err=" + ex.Message); }
        }
    }

    internal static class AppLog
    {
        private static readonly object Sync = new object();
        private static string logPath;
        public static void Init() { try { string dir = Path.Combine(Path.GetTempPath(), "OpenRPF9_logs"); if (!Directory.Exists(dir)) Directory.CreateDirectory(dir); logPath = Path.Combine(dir, "openrpf9.log"); } catch { } }
        public static string PathOnDisk { get { Init(); return logPath; } } public static void Line(string msg) { try { Init(); string line = "[" + DateTime.Now.ToString("HH:mm:ss.fff") + "] " + msg; lock (Sync) { if (!string.IsNullOrEmpty(logPath)) File.AppendAllText(logPath, line + Environment.NewLine); } } catch { } }
    }

    internal static class Ffmpeg
    {
        private static readonly object Sync = new object();
        private static string exePath;
        private static bool searched;
        private static string configuredPath;
        public static void Configure(string path) { lock (Sync) { configuredPath = (path ?? "").Trim(); searched = false; exePath = null; } }
        public static void Reset() { lock (Sync) { searched = false; exePath = null; } }
        public static string ExePath { get { lock (Sync) { if (!searched) { searched = true; exePath = ResolveExe(); AppLog.Line("FFmpeg path: " + (exePath ?? "not found")); } return exePath; } } }
        public static bool Available { get { return !string.IsNullOrEmpty(ExePath) && File.Exists(ExePath); } }
        private static string ResolveExe() { try { if (!string.IsNullOrEmpty(configuredPath) && File.Exists(configuredPath)) return configuredPath; } catch { } return TryAutoDetect(); }
        public static string TryAutoDetect()
        {
            try
            {
                string local = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ffmpeg.exe");
                if (File.Exists(local)) return local;
                string pathVar = Environment.GetEnvironmentVariable("PATH") ?? "";
                foreach (string dir in pathVar.Split(';'))
                {
                    if (string.IsNullOrEmpty(dir)) continue;
                    try { string candidate = Path.Combine(dir.Trim().Trim('"'), "ffmpeg.exe"); if (File.Exists(candidate)) return candidate; } catch { }
                }
            }
            catch { }
            return null;
        }
        public static byte[] DecodeToWav(byte[] input, string inputExt)
        {
            if (input == null || input.Length == 0) return null;
            if (!Available) throw new InvalidOperationException("FFmpeg is not configured. Set the ffmpeg.exe path in Settings.");
            string dir = Path.Combine(Path.GetTempPath(), "OpenRPF9_ffmpeg");
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
            string inPath = Path.Combine(dir, "in_" + Guid.NewGuid().ToString("N") + (string.IsNullOrEmpty(inputExt) ? ".bin" : inputExt));
            string outPath = Path.Combine(dir, "out_" + Guid.NewGuid().ToString("N") + ".wav");
            string errPath = Path.Combine(dir, "err_" + Guid.NewGuid().ToString("N") + ".txt");
            try
            {
                File.WriteAllBytes(inPath, input);
                int code = RunProcessCapture(ExePath, "-y -v error -i \"" + inPath + "\" -vn -acodec pcm_s16le -ac 2 -ar 48000 \"" + outPath + "\"", dir, 120000, errPath);
                if (code != 0 || !File.Exists(outPath))
                {
                    string err = TryReadSmall(errPath);
                    throw new InvalidOperationException("FFmpeg decode failed (exit " + code + ")" + (string.IsNullOrEmpty(err) ? "" : ": " + err));
                }
                byte[] wav = File.ReadAllBytes(outPath);
                if (!LooksLikeWavBytes(wav)) { string err = TryReadSmall(errPath); throw new InvalidOperationException("FFmpeg output was not a valid WAV" + (string.IsNullOrEmpty(err) ? "" : ": " + err)); }
                return wav;
            }
            finally
            {
                try { if (File.Exists(inPath)) File.Delete(inPath); } catch { }
                try { if (File.Exists(outPath)) File.Delete(outPath); } catch { }
                try { if (File.Exists(errPath)) File.Delete(errPath); } catch { }
            }
        }
        private static string TryReadSmall(string path) { try { if (!File.Exists(path)) return null; string s = File.ReadAllText(path); if (s == null) return null; s = s.Trim(); return s.Length > 400 ? s.Substring(0, 400) : s; } catch { return null; } }
        public static bool LooksLikeWavBytes(byte[] d) { return d != null && d.Length >= 12 && d[0]=='R' && d[1]=='I' && d[2]=='F' && d[3]=='F' && d[8]=='W' && d[9]=='A' && d[10]=='V' && d[11]=='E'; }
        private static int RunProcessCapture(string exe, string args, string workDir, int timeoutMs, string errPath)
        {
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo(); psi.FileName = exe; psi.Arguments = args; psi.WorkingDirectory = workDir;
                psi.UseShellExecute = false; psi.CreateNoWindow = true; psi.RedirectStandardOutput = true; psi.RedirectStandardError = true;
                using (Process p = Process.Start(psi))
                {
                    string stderr = p.StandardError.ReadToEnd();
                    p.StandardOutput.ReadToEnd();
                    if (!p.WaitForExit(timeoutMs)) { try { p.Kill(); } catch { } throw new InvalidOperationException("FFmpeg timed out"); }
                    try { File.WriteAllText(errPath, stderr ?? ""); } catch { }
                    return p.ExitCode;
                }
            }
            catch (Exception ex) { try { File.WriteAllText(errPath, ex.Message); } catch { } throw; }
        }
        private static int RunProcess(string exe, string args, string workDir, int timeoutMs) { ProcessStartInfo psi = new ProcessStartInfo(); psi.FileName = exe; psi.Arguments = args; psi.WorkingDirectory = workDir; psi.UseShellExecute = false; psi.CreateNoWindow = true; psi.RedirectStandardError = true; psi.RedirectStandardOutput = true; using (Process p = new Process()) { p.StartInfo = psi; StringBuilder err = new StringBuilder(); p.ErrorDataReceived += delegate(object s, DataReceivedEventArgs e) { if (e.Data != null) { lock (err) { if (err.Length < 4000) err.AppendLine(e.Data); } } }; p.OutputDataReceived += delegate(object s, DataReceivedEventArgs e) { }; p.Start(); p.BeginOutputReadLine(); p.BeginErrorReadLine(); if (!p.WaitForExit(timeoutMs)) { try { p.Kill(); } catch { } throw new InvalidOperationException("FFmpeg timed out"); } if (p.ExitCode != 0) { string msg = err.ToString().Trim(); if (msg.Length > 500) msg = msg.Substring(0, 500); AppLog.Line("FFmpeg error: " + msg); throw new InvalidOperationException("FFmpeg decode failed (exit " + p.ExitCode + ")" + (string.IsNullOrEmpty(msg) ? "" : ": " + msg)); } return p.ExitCode; } }
    }
    internal sealed class CarvedStream
    {
        public long Offset;
        public int DecompressedSize;
        public int DecompressedConsumed;
        public string Kind;
        public string Preview;
        public byte[] Data;
        public string Name;
        public int TextureSystemBytes;
    }

    internal sealed class AwcStreamInfo
    {
        public int Index;
        public uint StreamId;
        public int Codec;
        public string CodecName;
        public int SampleRate;
        public int Channels;
        public uint Samples;
        public uint Offset;
        public uint Size;
        public byte[] Data;
        public int BlockSize;
        public byte[] DecodedWav;
        public string DecodeError;
    }

    internal sealed class AwcFile
    {
        public bool IsBigEndian;
        public bool IsEncrypted;
        public bool IsMusic;
        public int StreamCount;
        public List<AwcStreamInfo> Streams = new System.Collections.Generic.List<AwcStreamInfo>();
    }

    internal sealed class FileEntry
    {
        public string Name;
        public long Offset;
        public int Size;
        public string Kind;
        public string Preview;
        public byte[] Data;
        public List<long> SourceOffsets = new System.Collections.Generic.List<long>();
        public bool IsMedia;
        public string MediaKind;
        public AwcFile Awc;
        public string ImageInfo;
        public string ModelInfo;
        public int TextureSystemBytes;
        public RageTextureDictionary TextureDict;
        public TtpfTexture Ttpf;
        public TtpfFile TtpfSource;
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
        public List<FileEntry> Entries = new System.Collections.Generic.List<FileEntry>();
    }

    internal sealed class AppSettings
    {
        public bool AutoPlayMedia = false;
        public bool ShowMediaPlayer = true;
        public string FfmpegPath = "";

        public static string SettingsPath()
        {
            string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "OpenRPF9");
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
            return Path.Combine(dir, "settings.txt");
        }

        public static AppSettings Load()
        {
            AppSettings s = new AppSettings();
            try
            {
                string p = SettingsPath();
                if (!File.Exists(p)) return s;
                foreach (string line in File.ReadAllLines(p))
                {
                    int eq = line.IndexOf('=');
                    if (eq <= 0) continue;
                    string k = line.Substring(0, eq).Trim();
                    string v = line.Substring(eq + 1).Trim();
                    if (k == "AutoPlayMedia") s.AutoPlayMedia = v == "1";
                    else if (k == "ShowMediaPlayer") s.ShowMediaPlayer = v == "1";
                    else if (k == "FfmpegPath") s.FfmpegPath = v;
                }
            }
            catch { }
            return s;
        }

        public void Save()
        {
            try
            {
                List<string> lines = new System.Collections.Generic.List<string>();
                lines.Add("AutoPlayMedia=" + (AutoPlayMedia ? "1" : "0"));
                lines.Add("ShowMediaPlayer=" + (ShowMediaPlayer ? "1" : "0"));
                lines.Add("FfmpegPath=" + (FfmpegPath ?? ""));
                File.WriteAllLines(SettingsPath(), lines.ToArray());
            }
            catch { }
        }
    }

    internal sealed class AwcStemsForm : Form
    {
        private FileEntry entry;
        private ListView list;
        private Button playBtn;
        private Button stopBtn;
        private Button exportRawBtn;
        private Button exportWavBtn;
        private Button exportAllBtn;
        private Button closeBtn;
        private Label status;
        private SoundPlayer player;

        public AwcStemsForm(FileEntry fileEntry)
        {
            entry = fileEntry;
            Text = "AWC Stems - " + (entry == null ? "" : entry.Name);
            Icon = Program.LoadAppIcon();
            Width = 980;
            Height = 560;
            StartPosition = FormStartPosition.CenterParent;

            list = new ListView();
            list.Dock = DockStyle.Fill;
            list.View = View.Details;
            list.FullRowSelect = true;
            list.HideSelection = false;
            list.MultiSelect = false;
            list.Columns.Add("Stem", 60);
            list.Columns.Add("Codec", 110);
            list.Columns.Add("Rate", 80);
            list.Columns.Add("Ch", 50);
            list.Columns.Add("Samples", 90);
            list.Columns.Add("Offset", 100);
            list.Columns.Add("Size", 90);
            list.Columns.Add("Status", 340);

            playBtn = new Button(); playBtn.Text = "Play"; playBtn.Width = 80; playBtn.Left = 12; playBtn.Top = 10;
            stopBtn = new Button(); stopBtn.Text = "Stop"; stopBtn.Width = 80; stopBtn.Left = 100; stopBtn.Top = 10;
            exportRawBtn = new Button(); exportRawBtn.Text = "Export Raw"; exportRawBtn.Width = 100; exportRawBtn.Left = 188; exportRawBtn.Top = 10;
            exportWavBtn = new Button(); exportWavBtn.Text = "Export WAV"; exportWavBtn.Width = 100; exportWavBtn.Left = 296; exportWavBtn.Top = 10;
            exportAllBtn = new Button(); exportAllBtn.Text = "Export All"; exportAllBtn.Width = 100; exportAllBtn.Left = 404; exportAllBtn.Top = 10;
            closeBtn = new Button(); closeBtn.Text = "Close"; closeBtn.Width = 90; closeBtn.Left = 512; closeBtn.Top = 10;
            status = new Label(); status.AutoSize = true; status.Left = 620; status.Top = 15; status.Text = "";

            Panel bottom = new Panel(); bottom.Dock = DockStyle.Bottom; bottom.Height = 44;
            bottom.Controls.Add(playBtn); bottom.Controls.Add(stopBtn); bottom.Controls.Add(exportRawBtn); bottom.Controls.Add(exportWavBtn); bottom.Controls.Add(exportAllBtn); bottom.Controls.Add(closeBtn); bottom.Controls.Add(status);
            Controls.Add(list); Controls.Add(bottom);

            playBtn.Click += delegate { PlaySelected(); };
            stopBtn.Click += delegate { StopPlayback(); };
            exportRawBtn.Click += delegate { ExportRawSelected(); };
            exportWavBtn.Click += delegate { ExportWavSelected(); };
            exportAllBtn.Click += delegate { ExportAll(); };
            closeBtn.Click += delegate { Close(); };
            list.DoubleClick += delegate { PlaySelected(); };
            FormClosing += delegate { StopPlayback(); };

            LoadStems();
        }

        private void LoadStems()
        {
            list.Items.Clear();
            if (entry == null || entry.Awc == null || entry.Awc.Streams == null) return;
            for (int i = 0; i < entry.Awc.Streams.Count; i++)
            {
                AwcStreamInfo s = entry.Awc.Streams[i];
                RpfScanner.EnsureDecoded(s);
                ListViewItem item = new ListViewItem(s.Index.ToString());
                item.SubItems.Add(string.IsNullOrEmpty(s.CodecName) ? "?" : s.CodecName);
                item.SubItems.Add(s.SampleRate > 0 ? s.SampleRate.ToString() : "?");
                item.SubItems.Add(s.Channels > 0 ? s.Channels.ToString() : "?");
                item.SubItems.Add(s.Samples > 0 ? s.Samples.ToString() : "?");
                item.SubItems.Add("0x" + s.Offset.ToString("X"));
                item.SubItems.Add(s.Size.ToString());
                string st = entry.Awc.IsEncrypted ? "encrypted" : "";
                if (s.Data == null || s.Data.Length == 0) st = (st.Length > 0 ? st + ", " : "") + "no data";
                if (s.DecodedWav != null && Ffmpeg.LooksLikeWavBytes(s.DecodedWav)) st = (st.Length > 0 ? st + ", " : "") + "WAV ready";
                else if (!string.IsNullOrEmpty(s.DecodeError)) st = (st.Length > 0 ? st + ", " : "") + s.DecodeError;
                else st = (st.Length > 0 ? st + ", " : "") + "raw only";
                item.SubItems.Add(st);
                item.Tag = s;
                list.Items.Add(item);
            }
        }

        private AwcStreamInfo SelectedStem()
        {
            if (list.SelectedItems.Count == 0) return null;
            return list.SelectedItems[0].Tag as AwcStreamInfo;
        }

        private void PlaySelected()
        {
            AwcStreamInfo s = SelectedStem();
            if (s == null) { MessageBox.Show(this, "Select a stem first.", "OpenRPF9"); return; }
            RpfScanner.EnsureDecoded(s);
            if (s.DecodedWav == null || !Ffmpeg.LooksLikeWavBytes(s.DecodedWav)) { MessageBox.Show(this, "Cannot play stem: " + (s.DecodeError ?? "Decoded WAV is invalid"), "OpenRPF9"); return; }
            try
            {
                StopPlayback();
                string dir = Path.Combine(Path.GetTempPath(), "OpenRPF9_media");
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                string p = Path.Combine(dir, "stem_" + s.StreamId.ToString("X8") + "_" + s.Index.ToString() + ".wav");
                File.WriteAllBytes(p, s.DecodedWav);
                player = new SoundPlayer(p);
                player.Play();
                status.Text = "Playing stem " + s.Index;
                AppLog.Line("Play stem " + s.Index + " codec=" + s.CodecName);
            }
            catch (Exception ex) { AppLog.Line("Play failed: " + ex.Message); MessageBox.Show(this, ex.Message, "OpenRPF9"); }
        }

        private void StopPlayback()
        {
            try { if (player != null) { player.Stop(); player.Dispose(); player = null; } } catch { }
            if (status != null) status.Text = "";
        }

        private void ExportRawSelected()
        {
            AwcStreamInfo s = SelectedStem();
            if (s == null) { MessageBox.Show(this, "Select a stem first.", "OpenRPF9"); return; }
            SaveFileDialog dlg = new SaveFileDialog();
            dlg.FileName = Path.GetFileNameWithoutExtension(RpfScanner.SafeFileName(entry.Name)) + "_stem_" + s.Index + ".bin";
            dlg.Filter = "Raw (*.bin)|*.bin|All Files (*.*)|*.*";
            if (dlg.ShowDialog(this) != DialogResult.OK) return;
            File.WriteAllBytes(dlg.FileName, s.Data ?? new byte[0]);
        }

        private void ExportWavSelected()
        {
            AwcStreamInfo s = SelectedStem();
            if (s == null) { MessageBox.Show(this, "Select a stem first.", "OpenRPF9"); return; }
            RpfScanner.EnsureDecoded(s);
            if (s.DecodedWav == null || !Ffmpeg.LooksLikeWavBytes(s.DecodedWav)) { MessageBox.Show(this, "WAV not available: " + (s.DecodeError ?? "Decoded WAV is invalid"), "OpenRPF9"); return; }
            SaveFileDialog dlg = new SaveFileDialog();
            dlg.FileName = Path.GetFileNameWithoutExtension(RpfScanner.SafeFileName(entry.Name)) + "_stem_" + s.Index + ".wav";
            dlg.Filter = "WAV (*.wav)|*.wav|All Files (*.*)|*.*";
            if (dlg.ShowDialog(this) != DialogResult.OK) return;
            File.WriteAllBytes(dlg.FileName, s.DecodedWav);
        }

        private void ExportAll()
        {
            if (entry == null || entry.Awc == null || entry.Awc.Streams.Count == 0) { MessageBox.Show(this, "No stems.", "OpenRPF9"); return; }
            FolderBrowserDialog dlg = new FolderBrowserDialog();
            dlg.Description = "Select export folder";
            if (dlg.ShowDialog(this) != DialogResult.OK) return;
            string baseDir = Path.Combine(dlg.SelectedPath, Path.GetFileNameWithoutExtension(RpfScanner.SafeFileName(entry.Name)) + "_stems");
            Directory.CreateDirectory(baseDir);
            int wavCount = 0;
            for (int i = 0; i < entry.Awc.Streams.Count; i++)
            {
                AwcStreamInfo s = entry.Awc.Streams[i];
                string name = "stem_" + s.Index.ToString("D2") + "_" + RpfScanner.SafeBaseName(s.CodecName);
                File.WriteAllBytes(Path.Combine(baseDir, name + ".bin"), s.Data ?? new byte[0]);
                RpfScanner.EnsureDecoded(s);
                if (s.DecodedWav != null && Ffmpeg.LooksLikeWavBytes(s.DecodedWav)) { File.WriteAllBytes(Path.Combine(baseDir, name + ".wav"), s.DecodedWav); wavCount++; }
            }
            MessageBox.Show(this, "Exported " + entry.Awc.Streams.Count + " stems (" + wavCount + " WAV).", "OpenRPF9");
        }
    }

    internal sealed class ScanProgressForm : Form
    {
        private Label label;
        private ProgressBar bar;
        private Button cancel;
        public bool CancelRequested;
        public ScanProgressForm()
        {
            Text = "Opening archive";
            Width = 560;
            Height = 140;
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ControlBox = false;
            label = new Label(); label.Left = 12; label.Top = 12; label.Width = 520; label.Height = 40; label.Text = "Preparing scan...";
            bar = new ProgressBar(); bar.Left = 12; bar.Top = 56; bar.Width = 420; bar.Height = 20; bar.Minimum = 0; bar.Maximum = 1000;
            cancel = new Button(); cancel.Text = "Cancel"; cancel.Left = 442; cancel.Top = 54; cancel.Width = 90;
            cancel.Click += delegate { CancelRequested = true; cancel.Enabled = false; label.Text = "Cancelling..."; };
            Controls.Add(label); Controls.Add(bar); Controls.Add(cancel);
        }
        public void UpdateProgress(long pos, long total, int found)
        {
            if (IsDisposed) return;
            int v = total > 0 ? (int)Math.Max(0, Math.Min(1000, (pos * 1000L) / total)) : 0;
            bar.Value = v;
            label.Text = "Scanned " + pos.ToString() + " / " + total.ToString() + " bytes (" + (v / 10).ToString() + "%), found " + found.ToString();
        }
    }

    internal sealed class SettingsForm : Form
    {
        private CheckBox autoPlay;
        private CheckBox showPlayer;
        private TextBox ffmpegBox;
        private Button browseFfmpeg;
        private Label ffmpegStatus;
        private Button ok;
        private Button cancel;
        private AppSettings settings;
        public SettingsForm(AppSettings s)
        {
            settings = s;
            Text = "Settings";
            Width = 560;
            Height = 250;
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false; MinimizeBox = false;

            autoPlay = new CheckBox(); autoPlay.Text = "Auto play media"; autoPlay.Left = 12; autoPlay.Top = 12; autoPlay.Width = 300; autoPlay.Checked = s.AutoPlayMedia;
            showPlayer = new CheckBox(); showPlayer.Text = "Show media player"; showPlayer.Left = 12; showPlayer.Top = 38; showPlayer.Width = 300; showPlayer.Checked = s.ShowMediaPlayer;

            Label ffLabel = new Label(); ffLabel.Text = "FFmpeg path (optional, for MP3/AWC/XMA decode):"; ffLabel.Left = 12; ffLabel.Top = 72; ffLabel.Width = 520;
            ffmpegBox = new TextBox(); ffmpegBox.Left = 12; ffmpegBox.Top = 94; ffmpegBox.Width = 420; ffmpegBox.Text = s.FfmpegPath ?? "";
            browseFfmpeg = new Button(); browseFfmpeg.Text = "Browse..."; browseFfmpeg.Left = 440; browseFfmpeg.Top = 92; browseFfmpeg.Width = 90;
            ffmpegStatus = new Label(); ffmpegStatus.Left = 12; ffmpegStatus.Top = 122; ffmpegStatus.Width = 520; ffmpegStatus.Height = 34; ffmpegStatus.ForeColor = Color.DimGray;
            UpdateFfmpegStatus();

            browseFfmpeg.Click += delegate
            {
                using (OpenFileDialog ofd = new OpenFileDialog())
                {
                    ofd.Title = "Select ffmpeg.exe";
                    ofd.Filter = "ffmpeg.exe|ffmpeg.exe;ffmpeg|All files|*.*";
                    if (File.Exists(ffmpegBox.Text)) { try { ofd.InitialDirectory = Path.GetDirectoryName(ffmpegBox.Text); } catch { } }
                    if (ofd.ShowDialog(this) == DialogResult.OK)
                    {
                        ffmpegBox.Text = ofd.FileName;
                        UpdateFfmpegStatus();
                        settings.FfmpegPath = (ffmpegBox.Text ?? "").Trim();
                        settings.Save();
                        Ffmpeg.Configure(settings.FfmpegPath);
                    }
                }
            };

            ok = new Button(); ok.Text = "OK"; ok.Left = 350; ok.Top = 170; ok.DialogResult = DialogResult.OK;
            cancel = new Button(); cancel.Text = "Cancel"; cancel.Left = 440; cancel.Top = 170; cancel.DialogResult = DialogResult.Cancel;
            ok.Click += delegate
            {
                settings.AutoPlayMedia = autoPlay.Checked;
                settings.ShowMediaPlayer = showPlayer.Checked;
                settings.FfmpegPath = (ffmpegBox.Text ?? "").Trim();
                settings.Save();
                Ffmpeg.Configure(settings.FfmpegPath); Ffmpeg.Reset();
            };
            Controls.Add(autoPlay); Controls.Add(showPlayer); Controls.Add(ffLabel); Controls.Add(ffmpegBox); Controls.Add(browseFfmpeg); Controls.Add(ffmpegStatus); Controls.Add(ok); Controls.Add(cancel);
            AcceptButton = ok; CancelButton = cancel;
        }

        private void UpdateFfmpegStatus()
        {
            string p = (ffmpegBox.Text ?? "").Trim();
            if (string.IsNullOrEmpty(p)) { ffmpegStatus.Text = "No FFmpeg configured. MP3/XMA/unsupported AWC decode will be unavailable until configured."; return; }
            ffmpegStatus.Text = File.Exists(p) ? "Selected: " + p : "File not found: " + p;
        }
    }
    internal sealed class MainForm : Form
    {
        private MenuStrip menu;
        private ToolStrip toolStrip;
        private ToolStripTextBox pathBox;
        private ToolStripTextBox searchBox;
        private ToolStripComboBox filterBox;
        private SplitContainer split;
        private TreeView tree;
        private ListView fileList;
        private TabControl previewTabs;
        private TabPage detailsTab, imageTab, modelTab, mediaTab, stemsTab;
        private Label previewHeaderLabel;
        private PictureBox previewImageBox;
        private Label imageInfoLabel, modelInfoLabel;
        private Panel imageControlPanel;
        private ComboBox imageFormatBox;
        private TextBox imageWidthBox, imageHeightBox;
        private Button imageApplyBtn;
        private ComboBox imageTextureBox;
        private Button texturePickBtn;
        private Label imageTextureLabel;
        private Button saveDdsBtn;
        private ElementHost modelViewportHost;
        private System.Windows.Controls.Viewport3D modelViewport;
        private FileEntry currentPreviewEntry;
        private bool suppressTextureSelection;
        private RichTextBox details;
        private ElementHost mediaHost;
        private System.Windows.Controls.MediaElement mediaElement;
        private System.Windows.Media.MediaPlayer wpfMediaPlayer;
        private System.Windows.Forms.Panel mediaControls;
        private Button playBtn;
        private Button pauseBtn;
        private Button stopBtn;
        private TrackBar volumeBar;
        private Label mediaLabel;
        private bool mediaPlayerActive;
        private bool mediaElementActive;
        private object wmpCom;
        private bool wmpAudioActive;
        private bool soundPlayerActive;
        private SoundPlayer wavPreviewPlayer;
        private ListView stemsList;
        private Button exportStemButton;
        private Button exportAllStemsButton;
        private StatusStrip status;
        private ToolStripStatusLabel statusLabel;
        private ToolStripStatusLabel countLabel;

        private ArchiveInfo current;
        private AppSettings settings;
        private string currentMediaPath;
        private readonly List<FileEntry> allEntries = new System.Collections.Generic.List<FileEntry>();
        private CancellationTokenSource scanCts;
        private ScanProgressForm scanProgress;

        public MainForm()
        {
            settings = AppSettings.Load();
            Ffmpeg.Configure(settings.FfmpegPath);
            Text = "OpenRPF9";
            Width = 1360;
            Height = 860;
            StartPosition = FormStartPosition.CenterScreen;
            Icon = Program.LoadAppIcon();

            BuildUi();
            Load += delegate { AppLog.Line("MainForm loaded"); };
            FormClosing += delegate { CleanupMedia(); };
        }

        private void BuildUi()
        {
            menu = new MenuStrip();
            menu.Padding = new Padding(6, 2, 0, 2);
            ToolStripMenuItem fileMenu = new ToolStripMenuItem("File");
            fileMenu.DropDownItems.Add("Open...", null, delegate { OpenFile(); });
            fileMenu.DropDownItems.Add(new ToolStripSeparator());
            fileMenu.DropDownItems.Add("Export Selected...", null, delegate { ExportSelected(); });
            fileMenu.DropDownItems.Add("Export All...", null, delegate { ExportAll(); });
            fileMenu.DropDownItems.Add(new ToolStripSeparator());
            fileMenu.DropDownItems.Add("Exit", null, delegate { Close(); });
            ToolStripMenuItem viewMenu = new ToolStripMenuItem("View");
            viewMenu.DropDownItems.Add("Settings...", null, delegate { ShowSettings(); });
            viewMenu.DropDownItems.Add("Open Log", null, delegate { OpenLog(); });
            menu.Items.Add(fileMenu);
            menu.Items.Add(viewMenu);

            toolStrip = new ToolStrip();
            toolStrip.GripStyle = ToolStripGripStyle.Hidden;
            toolStrip.Padding = new Padding(6, 4, 6, 4);
            toolStrip.Items.Add(new ToolStripButton("Open", null, delegate { OpenFile(); }));
            toolStrip.Items.Add(new ToolStripButton("Export Selected", null, delegate { ExportSelected(); }));
            toolStrip.Items.Add(new ToolStripButton("Export All", null, delegate { ExportAll(); }));
            toolStrip.Items.Add(new ToolStripSeparator());
            toolStrip.Items.Add(new ToolStripLabel("Path"));
            pathBox = new ToolStripTextBox(); pathBox.AutoSize = false; pathBox.Width = 420; pathBox.ReadOnly = true; pathBox.BorderStyle = BorderStyle.FixedSingle;
            toolStrip.Items.Add(pathBox);
            toolStrip.Items.Add(new ToolStripSeparator());
            toolStrip.Items.Add(new ToolStripLabel("Search"));
            searchBox = new ToolStripTextBox(); searchBox.AutoSize = false; searchBox.Width = 200; searchBox.BorderStyle = BorderStyle.FixedSingle;
            searchBox.TextChanged += delegate { ApplyFilter(); };
            toolStrip.Items.Add(searchBox);
            toolStrip.Items.Add(new ToolStripLabel("Filter"));
            filterBox = new ToolStripComboBox(); filterBox.DropDownStyle = ComboBoxStyle.DropDownList; filterBox.AutoSize = false; filterBox.Width = 130;
            filterBox.Items.AddRange(new object[] { "All files", "Audio / AWC", "Video / BK2", "Images", "Text / XML", "Binary" });
            filterBox.SelectedIndex = 0;
            filterBox.SelectedIndexChanged += delegate { ApplyFilter(); };
            toolStrip.Items.Add(filterBox);

            split = new SplitContainer();
            split.Dock = DockStyle.Fill;
            split.SplitterDistance = 900;
            split.BorderStyle = BorderStyle.FixedSingle;

            tree = new TreeView();
            tree.Dock = DockStyle.Fill;
            tree.HideSelection = false;
            tree.BorderStyle = BorderStyle.None;
            tree.FullRowSelect = true;
            tree.ShowLines = false;
            tree.ItemHeight = 22;
            tree.Font = new Font("Segoe UI", 9f);
            tree.AfterSelect += Tree_AfterSelect;

            fileList = new ListView();
            fileList.Dock = DockStyle.Fill;
            fileList.View = View.Details;
            fileList.FullRowSelect = true;
            fileList.HideSelection = false;
            fileList.MultiSelect = false;
            fileList.BorderStyle = BorderStyle.None;
            fileList.GridLines = false;
            fileList.Font = new Font("Segoe UI", 9f);
            fileList.Columns.Add("Name", 260);
            fileList.Columns.Add("Type", 80);
            fileList.Columns.Add("Size", 90);
            fileList.Columns.Add("Offset", 90);
            fileList.Columns.Add("Info", 260);
            fileList.SelectedIndexChanged += FileList_SelectedIndexChanged;
            fileList.DoubleClick += FileList_DoubleClick;

            previewTabs = new TabControl();
            previewTabs.Dock = DockStyle.Fill;
            previewTabs.Font = new Font("Segoe UI", 9f);
            previewTabs.Appearance = TabAppearance.FlatButtons;
            previewTabs.SizeMode = TabSizeMode.Fixed;
            previewTabs.ItemSize = new Size(1, 1);
            previewTabs.Multiline = false;
            detailsTab = new TabPage("Details");
            imageTab = new TabPage("Image");
            modelTab = new TabPage("Model");
            mediaTab = new TabPage("Media");
            stemsTab = new TabPage("AWC Stems");
            details = new RichTextBox();
            details.Dock = DockStyle.Fill;
            details.Multiline = true;
            details.ReadOnly = true;
            details.ScrollBars = RichTextBoxScrollBars.Both;
            details.WordWrap = false;
            details.BorderStyle = BorderStyle.None;
            details.BackColor = Color.White;
            details.Font = new Font("Consolas", 9.5f);
            detailsTab.Controls.Add(details);

            previewImageBox = new PictureBox();
            previewImageBox.Dock = DockStyle.Fill;
            previewImageBox.BackColor = Color.FromArgb(30, 30, 30);
            previewImageBox.SizeMode = PictureBoxSizeMode.Zoom;
            imageInfoLabel = new Label();
            imageInfoLabel.Dock = DockStyle.Bottom;
            imageInfoLabel.Height = 24;
            imageInfoLabel.Padding = new Padding(6, 4, 6, 4);
            imageInfoLabel.BackColor = Color.FromArgb(45, 45, 48);
            imageInfoLabel.ForeColor = Color.Gainsboro;
            imageInfoLabel.Text = "No image loaded.";
            imageControlPanel = new Panel();
            imageControlPanel.Dock = DockStyle.Bottom;
            imageControlPanel.Height = 64;
            imageControlPanel.BackColor = Color.FromArgb(250,250,250);
            imageFormatBox = new ComboBox(); imageFormatBox.DropDownStyle = ComboBoxStyle.DropDownList; imageFormatBox.Left=8; imageFormatBox.Top=6; imageFormatBox.Width=120;
            imageFormatBox.Items.AddRange(new object[]{"Auto","RGBA8","BGRA8","RGB8","DXT1","DXT5","BC7","BC6H","BC6H-S"});
            imageFormatBox.SelectedIndex = 0;
            imageWidthBox = new TextBox(); imageWidthBox.Left=136; imageWidthBox.Top=7; imageWidthBox.Width=60; imageWidthBox.Text="1024";
            imageHeightBox = new TextBox(); imageHeightBox.Left=202; imageHeightBox.Top=7; imageHeightBox.Width=60; imageHeightBox.Text="1024";
            imageApplyBtn = new Button(); imageApplyBtn.Text="Apply"; imageApplyBtn.Left=270; imageApplyBtn.Top=5; imageApplyBtn.Width=70;
            imageApplyBtn.Click += delegate { TryLoadImagePreview(currentPreviewEntry, true); };
            imageTextureLabel = new Label(); imageTextureLabel.Text = "Texture:"; imageTextureLabel.Left = 8; imageTextureLabel.Top = 39; imageTextureLabel.Width = 56;
            imageTextureBox = new ComboBox(); imageTextureBox.DropDownStyle = ComboBoxStyle.DropDownList; imageTextureBox.Visible = false;
            imageTextureBox.SelectedIndexChanged += delegate { UpdateTexturePickText(); if (!suppressTextureSelection && currentPreviewEntry != null && currentPreviewEntry.TextureDict != null) TryLoadImagePreview(currentPreviewEntry, false); };
            texturePickBtn = new Button(); texturePickBtn.Left = 64; texturePickBtn.Top = 34; texturePickBtn.Width = 480; texturePickBtn.Height = 25; texturePickBtn.TextAlign = ContentAlignment.MiddleLeft; texturePickBtn.AutoEllipsis = true; texturePickBtn.Visible = false;
            texturePickBtn.Click += delegate { PickTexture(); };
            saveDdsBtn = new Button(); saveDdsBtn.Text = "Save PNG"; saveDdsBtn.Left = 550; saveDdsBtn.Top = 35; saveDdsBtn.Width = 90; saveDdsBtn.Visible = false;
            saveDdsBtn.Click += delegate { SaveTextureDds(); };
            imageControlPanel.Controls.Add(imageFormatBox); imageControlPanel.Controls.Add(imageWidthBox); imageControlPanel.Controls.Add(imageHeightBox); imageControlPanel.Controls.Add(imageApplyBtn);
            imageControlPanel.Controls.Add(imageTextureLabel); imageControlPanel.Controls.Add(texturePickBtn); imageControlPanel.Controls.Add(saveDdsBtn);
            imageTab.Controls.Add(previewImageBox);
            imageTab.Controls.Add(imageControlPanel);
            imageTab.Controls.Add(imageInfoLabel);

            modelViewport = new System.Windows.Controls.Viewport3D();
            modelViewportHost = new ElementHost(); modelViewportHost.Dock = DockStyle.Fill;
            modelViewportHost.Child = BuildModelViewport(modelViewport);
            modelInfoLabel = new Label();
            modelInfoLabel.Dock = DockStyle.Bottom;
            modelInfoLabel.Height = 24;
            modelInfoLabel.Padding = new Padding(6, 4, 6, 4);
            modelInfoLabel.BackColor = Color.FromArgb(45, 45, 48);
            modelInfoLabel.ForeColor = Color.Gainsboro;
            modelInfoLabel.Text = "No model loaded.";
            modelTab.Controls.Add(modelViewportHost);
            modelTab.Controls.Add(modelInfoLabel);

            mediaElement = new System.Windows.Controls.MediaElement();
            mediaElement.LoadedBehavior = System.Windows.Controls.MediaState.Manual;
            mediaElement.UnloadedBehavior = System.Windows.Controls.MediaState.Manual;
            mediaElement.Volume = 0.8;
            mediaElement.MediaFailed += MediaElement_MediaFailed;

            mediaHost = new ElementHost(); mediaHost.Dock = DockStyle.Fill; mediaHost.Child = mediaElement;
            mediaControls = new System.Windows.Forms.Panel(); mediaControls.Dock = DockStyle.Bottom; mediaControls.Height = 48; mediaControls.Padding = new Padding(8, 8, 8, 8);
            playBtn = new Button(); playBtn.Text = "Play"; playBtn.Left = 8; playBtn.Top = 10; playBtn.Width = 70; playBtn.Click += delegate { PlayCurrentMedia(); };
            pauseBtn = new Button(); pauseBtn.Text = "Pause"; pauseBtn.Left = 84; pauseBtn.Top = 10; pauseBtn.Width = 70; pauseBtn.Click += delegate { PauseCurrentMedia(); };
            stopBtn = new Button(); stopBtn.Text = "Stop"; stopBtn.Left = 160; stopBtn.Top = 10; stopBtn.Width = 70; stopBtn.Click += delegate { StopCurrentMedia(); };
            volumeBar = new TrackBar(); volumeBar.Left = 240; volumeBar.Top = 8; volumeBar.Width = 140; volumeBar.Minimum = 0; volumeBar.Maximum = 100; volumeBar.Value = 80; volumeBar.TickStyle = TickStyle.None; volumeBar.Scroll += delegate { SetCurrentMediaVolume(volumeBar.Value); };
            mediaLabel = new Label(); mediaLabel.Left = 390; mediaLabel.Top = 14; mediaLabel.Width = 700; mediaLabel.Text = "No media selected.";
            mediaControls.Controls.Add(playBtn); mediaControls.Controls.Add(pauseBtn); mediaControls.Controls.Add(stopBtn); mediaControls.Controls.Add(volumeBar); mediaControls.Controls.Add(mediaLabel);
            mediaTab.Controls.Add(mediaHost); mediaTab.Controls.Add(mediaControls);

            stemsList = new ListView(); stemsList.Dock = DockStyle.Fill; stemsList.View = View.Details; stemsList.FullRowSelect = true; stemsList.HideSelection = false; stemsList.MultiSelect = false; stemsList.BorderStyle = BorderStyle.None; stemsList.Font = new Font("Segoe UI", 9f);
            stemsList.Columns.Add("Stem", 60); stemsList.Columns.Add("Codec", 110); stemsList.Columns.Add("Rate", 80); stemsList.Columns.Add("Ch", 50); stemsList.Columns.Add("Samples", 90); stemsList.Columns.Add("Offset", 90); stemsList.Columns.Add("Size", 90); stemsList.Columns.Add("Status", 300);
            Panel stemsBottom = new Panel(); stemsBottom.Dock = DockStyle.Bottom; stemsBottom.Height = 48; stemsBottom.Padding = new Padding(8, 8, 8, 8);
            exportStemButton = new Button(); exportStemButton.Text = "Open Stem Window"; exportStemButton.Left = 8; exportStemButton.Top = 10; exportStemButton.Width = 140; exportStemButton.Click += delegate { OpenStemsWindow(); };
            exportAllStemsButton = new Button(); exportAllStemsButton.Text = "Export All Stems"; exportAllStemsButton.Left = 154; exportAllStemsButton.Top = 10; exportAllStemsButton.Width = 130; exportAllStemsButton.Click += delegate { ExportAllStems(); };
            stemsBottom.Controls.Add(exportStemButton); stemsBottom.Controls.Add(exportAllStemsButton);
            stemsTab.Controls.Add(stemsList); stemsTab.Controls.Add(stemsBottom);

            previewTabs.TabPages.Add(detailsTab);
            previewTabs.TabPages.Add(imageTab);
            previewTabs.TabPages.Add(modelTab);
            previewTabs.TabPages.Add(mediaTab);
            previewTabs.TabPages.Add(stemsTab);

            Panel previewPanel = new Panel(); previewPanel.Dock = DockStyle.Fill;
            previewHeaderLabel = new Label(); previewHeaderLabel.Dock = DockStyle.Top; previewHeaderLabel.Height = 28; previewHeaderLabel.Text = "  Preview"; previewHeaderLabel.TextAlign = ContentAlignment.MiddleLeft; previewHeaderLabel.Font = new Font("Segoe UI", 9f, FontStyle.Bold); previewHeaderLabel.BackColor = Color.FromArgb(245,245,245);
            previewPanel.Controls.Add(previewTabs); previewPanel.Controls.Add(previewHeaderLabel);

            Panel browserPanel = new Panel(); browserPanel.Dock = DockStyle.Fill;
            Label browserHeader = new Label(); browserHeader.Dock = DockStyle.Top; browserHeader.Height = 28; browserHeader.Text = "  Files"; browserHeader.TextAlign = ContentAlignment.MiddleLeft; browserHeader.Font = new Font("Segoe UI", 9f, FontStyle.Bold); browserHeader.BackColor = Color.FromArgb(245,245,245);
            browserPanel.Controls.Add(fileList);
            browserPanel.Controls.Add(browserHeader);

            split.Panel1.Controls.Add(previewPanel);
            split.Panel2.Controls.Add(browserPanel);
            status = new StatusStrip();
            status.SizingGrip = false;
            statusLabel = new ToolStripStatusLabel("");
            countLabel = new ToolStripStatusLabel("");
            status.Items.Add(statusLabel);
            ToolStripStatusLabel spring = new ToolStripStatusLabel(); spring.Spring = true; status.Items.Add(spring);
            status.Items.Add(countLabel);

            Controls.Add(split);
            Controls.Add(toolStrip);
            Controls.Add(menu);
            Controls.Add(status);
            MainMenuStrip = menu;        }

        private void OpenLog()
        {
            try { string lp = AppLog.PathOnDisk; if (!string.IsNullOrEmpty(lp) && File.Exists(lp)) Process.Start("notepad.exe", lp); } catch (Exception ex) { MessageBox.Show(this, ex.Message, "OpenRPF9"); }
        }

        private void ShowSettings()
        {
            using (SettingsForm f = new SettingsForm(settings))
            {
                if (f.ShowDialog(this) == DialogResult.OK)
                {
                    settings.Save();
                    ApplyFilter();
                    if (!settings.ShowMediaPlayer) ClearMedia("Media player hidden in settings.");
                }
            }
        }

        private void OpenFile()
        {
            OpenFileDialog dlg = new OpenFileDialog();
            dlg.Filter = "RPF / TTPF (*.rpf;*.ttpf)|*.rpf;*.ttpf|RPF Archives (*.rpf)|*.rpf|TTPF Files (*.ttpf)|*.ttpf|All Files (*.*)|*.*";
            dlg.Title = "Open RPF or TTPF";
            if (dlg.ShowDialog(this) != DialogResult.OK) return;
            OpenPath(dlg.FileName);
        }

        private void OpenPath(string path)
        {
            try
            {
                CleanupMedia();
                current = null;
                allEntries.Clear();
                tree.Nodes.Clear();
                fileList.Items.Clear();
                details.Text = "";
                stemsList.Items.Clear();
                pathBox.Text = path;
                statusLabel.Text = "Opening " + Path.GetFileName(path) + "...";
                countLabel.Text = "";
                Refresh();

                scanCts = new CancellationTokenSource();
                scanProgress = new ScanProgressForm();
                scanProgress.Show(this);
                scanProgress.UpdateProgress(0, 1, 0);

                string scanPath = path;
                ThreadPool.QueueUserWorkItem(delegate
                {
                    try
                    {
                        ArchiveInfo info = RpfScanner.Scan(scanPath, scanCts.Token, delegate(long pos, long total, int found)
                        {
                            try { BeginInvoke((Action)delegate { if (scanProgress != null && !scanProgress.IsDisposed) { if (scanProgress.CancelRequested) scanCts.Cancel(); scanProgress.UpdateProgress(pos, total, found); } }); } catch { }
                        }, delegate(FileEntry e)
                        {
                            try { BeginInvoke((Action)delegate { AddEntryLive(e); }); } catch { }
                        });
                        try { BeginInvoke((Action)delegate { ScanFinished(info, null); }); } catch { }
                    }
                    catch (Exception ex)
                    {
                        try { BeginInvoke((Action)delegate { ScanFinished(null, ex); }); } catch { }
                    }
                });
            }
            catch (Exception ex)
            {
                AppLog.Line("Open failed: " + ex);
                MessageBox.Show(this, ex.Message, "OpenRPF9");
            }
        }

        private void AddEntryLive(FileEntry e)
        {
            if (e == null) return;
            allEntries.Add(e);
            EnsureTreeNode(e);
            if (PassFilter(e)) AddFileListItem(e);
            countLabel.Text = allEntries.Count.ToString() + " items";
        }

        private void ScanFinished(ArchiveInfo info, Exception ex)
        {
            try { if (scanProgress != null && !scanProgress.IsDisposed) scanProgress.Close(); } catch { }
            scanProgress = null;
            if (scanCts != null) { scanCts.Dispose(); scanCts = null; }

            bool cancelled = ex is OperationCanceledException;
            if (ex != null && !cancelled)
            {
                AppLog.Line("Scan failed: " + ex);
                MessageBox.Show(this, ex.Message, "OpenRPF9");
                statusLabel.Text = "Scan failed: " + ex.Message;
                return;
            }
            if (info != null) current = info;
            if (info != null && info.Entries != null && allEntries.Count < info.Entries.Count)
            {
                for (int i = allEntries.Count; i < info.Entries.Count; i++)
                {
                    FileEntry e = info.Entries[i];
                    allEntries.Add(e);
                    EnsureTreeNode(e);
                }
            }
            ApplyFilter();
            if (current != null)
            {
                string prefix = cancelled ? "Scan cancelled" : current.DetectedFormat;
                statusLabel.Text = prefix + " | " + allEntries.Count + " items | " + current.Path;
            }
            else
            {
                statusLabel.Text = cancelled ? "Scan cancelled. " + allEntries.Count + " items found." : "Scan finished.";
            }
            countLabel.Text = allEntries.Count.ToString() + " items";
        }
        private void EnsureTreeNode(FileEntry e)
        {
            string group = GroupForEntry(e);
            TreeNode root = null;
            foreach (TreeNode n in tree.Nodes) if (n.Text == group) { root = n; break; }
            if (root == null) { root = new TreeNode(group); root.Tag = "group:" + group; tree.Nodes.Add(root); }
            if (e.Kind == "xml" && e.Name.StartsWith("xml/", StringComparison.OrdinalIgnoreCase))
            {
                string sub = "xml";
                TreeNode parent = root;
                foreach (TreeNode n in root.Nodes) if (n.Text == sub) { parent = n; break; }
                if (parent == root) { parent = new TreeNode(sub); parent.Tag = "group:xml"; root.Nodes.Add(parent); }
            }
        }

        private string GroupForEntry(FileEntry e)
        {
            if (e == null) return "Other";
            if (e.Awc != null || e.Kind == "awc" || e.Kind == "audio") return "Audio";
            if (e.Kind == "video") return "Video";
            if (e.Kind == "image") return "Images";
            if (e.Kind == "xml" || e.Kind == "text" || e.Kind == "meta" || e.Kind == "ps" ||
                e.Kind == "json" || e.Kind == "csv") return "Text";
            if (e.Kind == "texture" || e.Kind == "ytd") return "Textures";
            if (e.Kind == "rsc" || e.Kind == "pso") return "Resources";
            return "Binary";
        }

        private bool PassFilter(FileEntry e)
        {
            if (e == null) return false;
            string q = searchBox.Text ?? "";
            if (q.Length > 0)
            {
                string hay = (e.Name ?? "") + " " + (e.Kind ?? "") + " " + (e.Preview ?? "");
                if (hay.IndexOf(q, StringComparison.OrdinalIgnoreCase) < 0) return false;
            }
            string f = filterBox.SelectedItem as string ?? "All files";
            if (f == "All files") return true;
            if (f == "Audio / AWC") return e.Awc != null || e.Kind == "awc" || e.Kind == "audio";
            if (f == "Video / BK2") return e.Kind == "video";
            if (f == "Images") return e.Kind == "image";
            if (f == "Text / XML") return e.Kind == "xml" || e.Kind == "text" || e.Kind == "meta" || e.Kind == "json" || e.Kind == "csv" || e.Kind == "ps";
            if (f == "Binary") return e.Kind == "binary" || e.Kind == "rsc" || e.Kind == "pso" || e.Kind == "ytd";
            return true;
        }

        private void ApplyFilter()
        {
            fileList.BeginUpdate();
            fileList.Items.Clear();
            for (int i = 0; i < allEntries.Count; i++) if (PassFilter(allEntries[i])) AddFileListItem(allEntries[i]);
            fileList.EndUpdate();
            countLabel.Text = fileList.Items.Count + " / " + allEntries.Count + " items";
        }

        private void AddFileListItem(FileEntry e)
        {
            ListViewItem item = new ListViewItem(e.Name);
            item.SubItems.Add(e.Kind ?? "");
            item.SubItems.Add(e.Size.ToString());
            item.SubItems.Add(e.Offset >= 0 ? "0x" + e.Offset.ToString("X") : "");
            string info = RpfScanner.MakeListPreview(e.Data, e.Kind); if (string.IsNullOrEmpty(info)) info = e.Preview ?? "";
            if (e.Awc != null) info = (e.Awc.IsEncrypted ? "encrypted " : "") + e.Awc.Streams.Count + " stems";
            item.SubItems.Add(info);
            item.Tag = e;
            fileList.Items.Add(item);
        }

        private void Tree_AfterSelect(object sender, TreeViewEventArgs e)
        {
            ApplyFilter();
        }

        private FileEntry GetSelectedFileEntry()
        {
            if (fileList.SelectedItems.Count == 0) return null;
            return fileList.SelectedItems[0].Tag as FileEntry;
        }

        private void FileList_SelectedIndexChanged(object sender, EventArgs e)
        {
            FileEntry entry = GetSelectedFileEntry();
            ShowDetails(entry);
            LoadMediaForEntry(entry);
            PopulateStems(entry);
        }

        private void FileList_DoubleClick(object sender, EventArgs e)
        {
            FileEntry entry = GetSelectedFileEntry();
            if (entry == null) return;
            if (entry.Awc != null)
            {
                using (AwcStemsForm dlg = new AwcStemsForm(entry)) dlg.ShowDialog(this);
                return;
            }
        }

        private void OpenStemsWindow()
        {
            FileEntry entry = GetSelectedFileEntry();
            if (entry == null || entry.Awc == null) { MessageBox.Show(this, "Select an AWC file first.", "OpenRPF9"); return; }
            using (AwcStemsForm dlg = new AwcStemsForm(entry)) dlg.ShowDialog(this);
        }

        private void ExportAllStems()
        {
            FileEntry entry = GetSelectedFileEntry();
            if (entry == null || entry.Awc == null) { MessageBox.Show(this, "Select an AWC file first.", "OpenRPF9"); return; }
            if (entry.Awc.Streams.Count == 0) { MessageBox.Show(this, "No stems.", "OpenRPF9"); return; }
            FolderBrowserDialog dlg = new FolderBrowserDialog();
            if (dlg.ShowDialog(this) != DialogResult.OK) return;
            string baseDir = Path.Combine(dlg.SelectedPath, Path.GetFileNameWithoutExtension(RpfScanner.SafeFileName(entry.Name)) + "_stems");
            Directory.CreateDirectory(baseDir);
            int wavCount = 0;
            for (int i = 0; i < entry.Awc.Streams.Count; i++)
            {
                AwcStreamInfo s = entry.Awc.Streams[i];
                string name = "stem_" + s.Index.ToString("D2") + "_" + RpfScanner.SafeBaseName(s.CodecName);
                File.WriteAllBytes(Path.Combine(baseDir, name + ".bin"), s.Data ?? new byte[0]);
                RpfScanner.EnsureDecoded(s);
                if (s.DecodedWav != null && Ffmpeg.LooksLikeWavBytes(s.DecodedWav)) { File.WriteAllBytes(Path.Combine(baseDir, name + ".wav"), s.DecodedWav); wavCount++; }
            }
            MessageBox.Show(this, "Exported " + entry.Awc.Streams.Count + " stems (" + wavCount + " WAV).", "OpenRPF9");
        }

        private void SetDetailsText(string text, string kind)
        {
            details.SuspendLayout();
            details.Clear();
            details.Text = text ?? "";
            if (kind == "xml" || kind == "meta") HighlightXml(details);
            else if (kind == "json") HighlightJson(details);
            else if (kind == "text" || kind == "csv" || kind == "ps") HighlightConfig(details);
            details.Select(0, 0);
            details.ScrollToCaret();
            details.ResumeLayout();
        }

        private static void ColorRange(RichTextBox box, int start, int len, Color color)
        {
            if (start < 0 || len <= 0 || start + len > box.TextLength) return;
            box.Select(start, len);
            box.SelectionColor = color;
        }

        private static void HighlightXml(RichTextBox box)
        {
            string t = box.Text;
            int i = 0;
            while (i < t.Length)
            {
                if (t[i] == '<')
                {
                    int end = t.IndexOf('>', i);
                    if (end < 0) end = t.Length - 1;
                    ColorRange(box, i, end - i + 1, Color.FromArgb(0, 102, 204));
                    int j = i + 1;
                    while (j < end)
                    {
                        while (j < end && char.IsWhiteSpace(t[j])) j++;
                        if (j >= end) break;
                        if (t[j] == '/' || t[j] == '?' || t[j] == '!') { j++; continue; }
                        int nameStart = j;
                        while (j < end && t[j] != '=' && !char.IsWhiteSpace(t[j]) && t[j] != '/' && t[j] != '>') j++;
                        if (j > nameStart && j < end && t[j] == '=')
                        {
                            ColorRange(box, nameStart, j - nameStart, Color.FromArgb(128, 0, 128));
                            j++;
                            if (j < end && (t[j] == '"' || t[j] == '\''))
                            {
                                char q = t[j]; int qs = j; j++;
                                while (j < end && t[j] != q) j++;
                                if (j <= end) { ColorRange(box, qs, j - qs + 1, Color.FromArgb(163, 21, 21)); j++; }
                            }
                        }
                        else j++;
                    }
                    i = end + 1;
                }
                else i++;
            }
            box.SelectionColor = box.ForeColor;
        }

        private static void HighlightJson(RichTextBox box)
        {
            string t = box.Text;
            for (int i = 0; i < t.Length; i++)
            {
                if (t[i] == '"')
                {
                    int j = i + 1;
                    while (j < t.Length) { if (t[j] == '\\') { j += 2; continue; } if (t[j] == '"') break; j++; }
                    if (j < t.Length) { ColorRange(box, i, j - i + 1, Color.FromArgb(163, 21, 21)); i = j; }
                }
                else if (char.IsDigit(t[i]) || (t[i] == '-' && i + 1 < t.Length && char.IsDigit(t[i + 1])))
                {
                    int j = i; if (t[j] == '-') j++; while (j < t.Length && (char.IsDigit(t[j]) || t[j] == '.' || t[j] == 'e' || t[j] == 'E' || t[j] == '+' || t[j] == '-')) j++;
                    ColorRange(box, i, j - i, Color.FromArgb(9, 134, 88)); i = j - 1;
                }
            }
            box.SelectionColor = box.ForeColor;
        }

        private static void HighlightConfig(RichTextBox box)
        {
            string t = box.Text;
            int lineStart = 0;
            for (int i = 0; i <= t.Length; i++)
            {
                if (i == t.Length || t[i] == '\n')
                {
                    string line = t.Substring(lineStart, i - lineStart);
                    string trim = line.TrimStart();
                    int lead = line.Length - trim.Length;
                    if (trim.StartsWith("#") || trim.StartsWith("//") || trim.StartsWith(";"))
                        ColorRange(box, lineStart + lead, trim.Length, Color.FromArgb(0, 128, 0));
                    else
                    {
                        int eq = line.IndexOf('=');
                        if (eq >= 0)
                        {
                            ColorRange(box, lineStart, eq, Color.FromArgb(0, 102, 204));
                            ColorRange(box, lineStart + eq + 1, line.Length - eq - 1, Color.FromArgb(163, 21, 21));
                        }
                    }
                    lineStart = i + 1;
                }
            }
            box.SelectionColor = box.ForeColor;
        }
        private void ClearImagePreview()
        {
            try { if (previewImageBox.Image != null) { var old = previewImageBox.Image; previewImageBox.Image = null; old.Dispose(); } } catch { }
            try { if (imageInfoLabel != null) imageInfoLabel.Text = "No image loaded."; } catch { }
            try
            {
                if (texturePickBtn != null) { texturePickBtn.Visible = false; }
                if (imageTextureLabel != null) imageTextureLabel.Visible = false;
                if (saveDdsBtn != null) saveDdsBtn.Visible = false;
            }
            catch { }
        }

        private void ClearModelPreview()
        {
            try
            {
                if (modelViewport != null)
                {
                    modelViewport.Children.Clear();
                    modelViewport.Children.Add(new System.Windows.Media.Media3D.ModelVisual3D { Content = new System.Windows.Media.Media3D.AmbientLight(System.Windows.Media.Color.FromRgb(120,120,120)) });
                    var dl = new System.Windows.Media.Media3D.DirectionalLight(System.Windows.Media.Color.FromRgb(220,220,220), new System.Windows.Media.Media3D.Vector3D(-1,-1,-2));
                    modelViewport.Children.Add(new System.Windows.Media.Media3D.ModelVisual3D { Content = dl });
                }
                if (modelInfoLabel != null) modelInfoLabel.Text = "No model loaded.";
            }
            catch { }
        }

        private System.Windows.Controls.Viewport3D BuildModelViewport(System.Windows.Controls.Viewport3D vp)
        {
            var cam = new System.Windows.Media.Media3D.PerspectiveCamera();
            cam.Position = new System.Windows.Media.Media3D.Point3D(0, 0, 5);
            cam.LookDirection = new System.Windows.Media.Media3D.Vector3D(0, 0, -5);
            cam.UpDirection = new System.Windows.Media.Media3D.Vector3D(0, 1, 0);
            cam.FieldOfView = 60;
            vp.Camera = cam;
            vp.Children.Add(new System.Windows.Media.Media3D.ModelVisual3D { Content = new System.Windows.Media.Media3D.AmbientLight(System.Windows.Media.Color.FromRgb(110,110,110)) });
            var dl = new System.Windows.Media.Media3D.DirectionalLight(System.Windows.Media.Color.FromRgb(230,230,230), new System.Windows.Media.Media3D.Vector3D(-1,-1,-3));
            vp.Children.Add(new System.Windows.Media.Media3D.ModelVisual3D { Content = dl });
            return vp;
        }

                private void TryLoadImagePreview(FileEntry entry) { TryLoadImagePreview(entry, false); }
        private void TryLoadImagePreview(FileEntry entry, bool forceManual)
        {
            ClearImagePreview();
            if (entry != null && entry.Ttpf != null && entry.TtpfSource != null) { LoadTtpfPreview(entry); return; }
            if (entry == null || entry.Data == null) return;
            try
            {
                Bitmap bmp = null;
                string info = "";
                bool known = RpfScanner.LooksLikeDds(entry.Data) || RpfScanner.LooksLikePng(entry.Data) || RpfScanner.LooksLikeJpg(entry.Data);
                if (RpfScanner.LooksLikeDds(entry.Data))
                {
                    bmp = DdsDecoder.DecodeToBitmap(entry.Data, out info);
                }
                else if (RpfScanner.LooksLikePng(entry.Data) || RpfScanner.LooksLikeJpg(entry.Data))
                {
                    using (var ms = new MemoryStream(entry.Data))
                    using (var img = Image.FromStream(ms, false, false))
                    {
                        bmp = new Bitmap(img);
                        info = img.Width + "x" + img.Height + " " + img.PixelFormat;
                    }
                }
                else if (entry.TextureDict != null && entry.TextureDict.Textures.Count > 0)
                {
                    byte[] system, graphics;
                    if (RpfScanner.TryGetTextureSections(entry, out system, out graphics))
                    {
                        PopulateTextureBox(entry);
                        int index = imageTextureBox != null ? imageTextureBox.SelectedIndex : 0;
                        if (index < 0 || index >= entry.TextureDict.Textures.Count) index = 0;
                        RageTextureInfo tex = entry.TextureDict.Textures[index];
                        try
                        {
                            int level = tex.PreviewMipLevel;
                            byte[] dds = RageTextureTools.CreateDdsMip(system, graphics, index, level);
                            bmp = DdsDecoder.DecodeToBitmap(dds, out info);
                            info = entry.TextureDict.Textures.Count + " textures | [" + index + "] " + tex.Name + " " + tex.Width + "x" + tex.Height + " " + tex.FormatName + " | " + info;
                        }
                        catch (Exception texEx)
                        {
                            info = entry.TextureDict.Textures.Count + " textures | [" + index + "] " + tex.Name + " " + tex.Width + "x" + tex.Height + " " + tex.FormatName + " | preview unsupported: " + texEx.Message;
                        }
                    }
                }
                else if (forceManual)
                {
                    bmp = TryDecodeRawImage(entry.Data, true, out info);
                }

                if (bmp != null)
                {
                    var old = previewImageBox.Image;
                    previewImageBox.Image = bmp;
                    if (old != null) old.Dispose();
                    imageInfoLabel.Text = (entry.Name ?? "image") + " | " + info;
                    entry.ImageInfo = info;
                }
                else
                {
                    imageInfoLabel.Text = (entry.Name ?? "image") + (known ? " | decode failed" : (entry.TextureDict != null ? " | " + info : " | no confirmed image format. Use Image controls to force-decode raw data."));
                }
            }
            catch (Exception ex)
            {
                imageInfoLabel.Text = "Image preview failed: " + ex.Message;
                AppLog.Line(imageInfoLabel.Text);
            }
        }
        private void LoadTtpfPreview(FileEntry entry)
        {
            try
            {
                string error;
                byte[] png = entry.TtpfSource.RenderPng(entry.Ttpf, out error);
                if (png == null || png.Length == 0)
                {
                    imageInfoLabel.Text = (entry.Name ?? "image") + " | preview unsupported: " + error;
                    return;
                }
                using (MemoryStream ms = new MemoryStream(png))
                using (Image img = Image.FromStream(ms, false, false))
                {
                    Bitmap bmp = new Bitmap(img);
                    Image old = previewImageBox.Image;
                    previewImageBox.Image = bmp;
                    if (old != null) old.Dispose();
                    string info = img.Width + "x" + img.Height + " " + entry.Ttpf.FormatName;
                    imageInfoLabel.Text = (entry.Name ?? "image") + " | " + info + (string.IsNullOrEmpty(error) ? "" : " | " + error);
                    entry.ImageInfo = info;
                }
            }
            catch (Exception ex)
            {
                imageInfoLabel.Text = "TTPF preview failed: " + ex.Message;
                AppLog.Line("TTPF preview failed: " + ex.Message);
            }
        }

        private void PopulateTextureBox(FileEntry entry)
        {
            if (imageTextureBox == null) return;
            if (object.ReferenceEquals(imageTextureBox.Tag, entry) && imageTextureBox.Items.Count > 0)
            {
                if (texturePickBtn != null) texturePickBtn.Visible = true;
                if (imageTextureLabel != null) imageTextureLabel.Visible = true;
                if (saveDdsBtn != null) saveDdsBtn.Visible = true;
                UpdateTexturePickText();
                return;
            }
            suppressTextureSelection = true;
            imageTextureBox.BeginUpdate();
            try
            {
                imageTextureBox.Items.Clear();
                imageTextureBox.Tag = entry;
                if (entry != null && entry.TextureDict != null)
                {
                    for (int i = 0; i < entry.TextureDict.Textures.Count; i++)
                    {
                        RageTextureInfo t = entry.TextureDict.Textures[i];
                        imageTextureBox.Items.Add("[" + i + "] " + t.Name + "  " + t.Width + "x" + t.Height + "  " + t.FormatName);
                    }
                    if (imageTextureBox.Items.Count > 0) imageTextureBox.SelectedIndex = ChooseDefaultTexture(entry.TextureDict);
                }
            }
            finally { imageTextureBox.EndUpdate(); suppressTextureSelection = false; }
            bool show = entry != null && entry.TextureDict != null && entry.TextureDict.Textures.Count > 0;
            if (texturePickBtn != null) texturePickBtn.Visible = show;
            if (imageTextureLabel != null) imageTextureLabel.Visible = show;
            if (saveDdsBtn != null) saveDdsBtn.Visible = show;
            UpdateTexturePickText();
        }

        private void UpdateTexturePickText()
        {
            if (texturePickBtn == null) return;
            RageTextureDictionary dict = currentPreviewEntry != null ? currentPreviewEntry.TextureDict : null;
            int index = imageTextureBox != null ? imageTextureBox.SelectedIndex : -1;
            if (dict == null || index < 0 || index >= dict.Textures.Count)
            {
                texturePickBtn.Text = "Select texture...";
                return;
            }
            RageTextureInfo t = dict.Textures[index];
            texturePickBtn.Text = "[" + index + "] " + (string.IsNullOrEmpty(t.Name) ? "(unnamed)" : t.Name) +
                "   " + t.Width + "x" + t.Height + "   " + t.FormatName +
                "   - " + dict.Textures.Count + " textures, click to browse";
        }

        private void PickTexture()
        {
            FileEntry entry = currentPreviewEntry;
            if (entry == null || entry.TextureDict == null || entry.TextureDict.Textures == null || entry.TextureDict.Textures.Count == 0) return;
            if (imageTextureBox == null) return;
            using (TexturePickerForm dlg = new TexturePickerForm(entry.TextureDict.Textures, imageTextureBox.SelectedIndex))
            {
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                if (dlg.SelectedIndex < 0 || dlg.SelectedIndex >= entry.TextureDict.Textures.Count) return;
                if (dlg.SelectedIndex == imageTextureBox.SelectedIndex)
                {
                    TryLoadImagePreview(entry, false);
                    return;
                }
                imageTextureBox.SelectedIndex = dlg.SelectedIndex;
            }
        }

        private static int ChooseDefaultTexture(RageTextureDictionary dictionary)
        {
            int best = 0;
            long bestPixels = -1;
            for (int i = 0; i < dictionary.Textures.Count; i++)
            {
                RageTextureInfo t = dictionary.Textures[i];
                long pixels = (long)t.Width * t.Height;
                if (pixels > bestPixels) { bestPixels = pixels; best = i; }
            }
            return best;
        }

        private void SaveTextureDds()
        {
            FileEntry entry = currentPreviewEntry;
            if (entry == null || entry.TextureDict == null || imageTextureBox == null || imageTextureBox.SelectedIndex < 0)
            {
                MessageBox.Show(this, "No texture dictionary is selected.", "OpenRPF9", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            byte[] system, graphics;
            if (!RpfScanner.TryGetTextureSections(entry, out system, out graphics))
            {
                MessageBox.Show(this, "The texture dictionary sections are not available.", "OpenRPF9", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            int index = imageTextureBox.SelectedIndex;
            RageTextureInfo tex = entry.TextureDict.Textures[index];
            SaveFileDialog dlg = new SaveFileDialog();
            dlg.FileName = RpfScanner.SafeFileName(tex.Name) + ".png";
            dlg.Filter = "PNG Image (*.png)|*.png";
            dlg.DefaultExt = "png";
            if (dlg.ShowDialog(this) != DialogResult.OK) return;
            try
            {
                byte[] dds = RageTextureTools.CreateDds(system, graphics, index);
                string info;
                using (Bitmap bmp = DdsDecoder.DecodeToBitmap(dds, out info))
                using (MemoryStream ms = new MemoryStream())
                {
                    bmp.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
                    File.WriteAllBytes(dlg.FileName, ms.ToArray());
                    statusLabel.Text = "Exported " + Path.GetFileName(dlg.FileName) + " at " + bmp.Width + "x" + bmp.Height;
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "PNG export failed: " + ex.Message, "OpenRPF9", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private Bitmap TryDecodeRawImage(byte[] data, bool forceManual, out string info)
        {
            info = "";
            if (data == null || data.Length < 16) return null;
            string fmt = imageFormatBox != null ? (imageFormatBox.SelectedItem as string ?? "Auto") : "Auto";
            int w = 0, h = 0;
            int.TryParse(imageWidthBox != null ? imageWidthBox.Text : "", out w);
            int.TryParse(imageHeightBox != null ? imageHeightBox.Text : "", out h);
            bool manual = forceManual && fmt != "Auto" && w > 0 && h > 0;
            if (manual) return RawImageDecoder.Decode(data, 0, w, h, fmt, out info);

            int[] sizes = new int[] { 4096, 2048, 1536, 1024, 768, 512, 384, 256, 128, 64 };
            foreach (int ww in sizes)
            foreach (int hh in sizes)
            {
                int px = ww * hh;
                if (data.Length == px * 4) { var b = RawImageDecoder.Decode(data, 0, ww, hh, "RGBA8", out info); if (b != null) { info += " (heuristic)"; return b; } }
                if (data.Length == px) { var b = RawImageDecoder.DecodeGray(data, 0, ww, hh, out info); if (b != null) { info += " (heuristic)"; return b; } }
                if (ww % 4 == 0 && hh % 4 == 0 && data.Length == (ww / 4) * (hh / 4) * 16) { var b = RawImageDecoder.Decode(data, 0, ww, hh, "DXT5", out info); if (b != null) { info += " (heuristic)"; return b; } }
                if (ww % 4 == 0 && hh % 4 == 0 && data.Length == (ww / 4) * (hh / 4) * 8) { var b = RawImageDecoder.Decode(data, 0, ww, hh, "DXT1", out info); if (b != null) { info += " (heuristic)"; return b; } }
            }
            return null;
        }
private void TryLoadModelPreview(FileEntry entry)
        {
            ClearModelPreview();
            if (entry == null || entry.Data == null) return;
            try
            {
                SimpleModel model = SimpleModelLoader.TryLoad(entry);
                if (model == null || model.Positions.Count == 0 || model.Triangles.Count == 0)
                {
                    if (modelInfoLabel != null) modelInfoLabel.Text = "No renderable model geometry detected.";
                    return;
                }
                RenderSimpleModel(model);
                if (modelInfoLabel != null) modelInfoLabel.Text = (entry.Name ?? "model") + " | verts=" + model.Positions.Count + " tris=" + (model.Triangles.Count / 3) + " " + model.Info;
            }
            catch (Exception ex)
            {
                if (modelInfoLabel != null) modelInfoLabel.Text = "Model preview failed: " + ex.Message;
                AppLog.Line("Model preview failed: " + ex);
            }
        }

        private void RenderSimpleModel(SimpleModel model)
        {
            var mesh = new System.Windows.Media.Media3D.MeshGeometry3D();
            foreach (var p in model.Positions) mesh.Positions.Add(p);
            foreach (var n in model.Normals) mesh.Normals.Add(n);
            foreach (var t in model.Triangles) mesh.TriangleIndices.Add(t);
            if (mesh.Normals.Count != mesh.Positions.Count) mesh.Normals.Clear();
            var mat = new System.Windows.Media.Media3D.DiffuseMaterial(new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(180, 190, 210)));
            var geo = new System.Windows.Media.Media3D.GeometryModel3D(mesh, mat);
            geo.BackMaterial = mat;
            var group = new System.Windows.Media.Media3D.Model3DGroup();
            group.Children.Add(geo);
            var mv = new System.Windows.Media.Media3D.ModelVisual3D(); mv.Content = group;
            modelViewport.Children.Clear();
            modelViewport.Children.Add(new System.Windows.Media.Media3D.ModelVisual3D { Content = new System.Windows.Media.Media3D.AmbientLight(System.Windows.Media.Color.FromRgb(100,100,100)) });
            var dl = new System.Windows.Media.Media3D.DirectionalLight(System.Windows.Media.Color.FromRgb(240,240,240), new System.Windows.Media.Media3D.Vector3D(-1,-2,-3));
            modelViewport.Children.Add(new System.Windows.Media.Media3D.ModelVisual3D { Content = dl });
            modelViewport.Children.Add(mv);

            var bounds = model.Bounds;
            var center = new System.Windows.Media.Media3D.Point3D((bounds.X+bounds.SizeX/2), (bounds.Y+bounds.SizeY/2), (bounds.Z+bounds.SizeZ/2));
            double radius = Math.Max(bounds.SizeX, Math.Max(bounds.SizeY, bounds.SizeZ));
            if (radius < 0.0001) radius = 1;
            var cam = modelViewport.Camera as System.Windows.Media.Media3D.PerspectiveCamera;
            if (cam != null)
            {
                cam.Position = new System.Windows.Media.Media3D.Point3D(center.X, center.Y, center.Z + radius * 2.2);
                cam.LookDirection = new System.Windows.Media.Media3D.Vector3D(center.X - cam.Position.X, center.Y - cam.Position.Y, center.Z - cam.Position.Z);
                cam.UpDirection = new System.Windows.Media.Media3D.Vector3D(0, 1, 0);
            }
        }
        private void ShowDetails(FileEntry entry)
        {
            currentPreviewEntry = entry;
            if (entry == null) { details.Text = ""; ClearImagePreview(); ClearModelPreview(); SelectPreviewTab(null); return; }
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("Name: " + entry.Name);
            sb.AppendLine("Kind: " + entry.Kind);
            sb.AppendLine("Offset: " + (entry.Offset >= 0 ? "0x" + entry.Offset.ToString("X") : ""));
            sb.AppendLine("Size: " + entry.Size);
            if (entry.Ttpf != null && entry.TtpfSource != null)
            {
                sb.AppendLine(entry.TtpfSource.Describe(entry.Ttpf));
                SetDetailsText(sb.ToString(), entry.Kind);
                TryLoadImagePreview(entry);
                TryLoadModelPreview(entry);
                SelectPreviewTab(entry);
                return;
            }
            if (entry.Awc != null)
            {
                sb.AppendLine("AWC: yes");
                sb.AppendLine("Encrypted: " + entry.Awc.IsEncrypted);
                sb.AppendLine("Streams: " + entry.Awc.Streams.Count);
                for (int i = 0; i < entry.Awc.Streams.Count; i++)
                {
                    AwcStreamInfo s = entry.Awc.Streams[i];
                    sb.AppendLine("  [" + i + "] id=0x" + s.StreamId.ToString("X8") + " " + s.CodecName + " " + s.SampleRate + "Hz " + s.Channels + "ch samples=" + s.Samples + " size=" + s.Size + (s.DecodedWav != null ? " wav" : (string.IsNullOrEmpty(s.DecodeError) ? "" : " err=" + s.DecodeError)));
                }
            }
            sb.AppendLine();
            string text = entry.Preview ?? "";
            if (IsTextKind(entry.Kind) && entry.Data != null)
            {
                try
                {
                    text = Encoding.UTF8.GetString(entry.Data);
                    if (text.Length > 60000) text = text.Substring(0, 60000) + "\n... (truncated)";
                }
                catch { }
            }
            else if (string.IsNullOrWhiteSpace(text) && entry.Data != null)
            {
                try { text = Encoding.UTF8.GetString(entry.Data); }
                catch { text = ""; }
            }
            sb.AppendLine(text);
            SetDetailsText(sb.ToString(), entry.Kind);
            TryLoadImagePreview(entry);
            TryLoadModelPreview(entry);
            SelectPreviewTab(entry);
        }

        private void SelectPreviewTab(FileEntry entry)
        {
            if (previewTabs == null) return;
            if (entry == null) { previewTabs.SelectedTab = detailsTab; SetPreviewType("Preview"); return; }
            string kind = entry.Kind ?? "";
            bool texture = entry.TextureDict != null && entry.TextureDict.Textures != null && entry.TextureDict.Textures.Count > 0;
            if (texture || kind == "image" || kind == "ytd")
            {
                previewTabs.SelectedTab = imageTab;
                SetPreviewType("Image");
            }
            else if (entry.Awc != null || kind == "awc")
            {
                previewTabs.SelectedTab = stemsTab;
                SetPreviewType("Stem");
            }
            else if (IsTextKind(kind))
            {
                previewTabs.SelectedTab = detailsTab;
                SetPreviewType("Text");
            }
            else if (kind == "video")
            {
                previewTabs.SelectedTab = mediaTab;
                SetPreviewType("Video");
            }
            else if (kind == "audio")
            {
                previewTabs.SelectedTab = mediaTab;
                SetPreviewType("Audio");
            }
            else if (kind == "model")
            {
                previewTabs.SelectedTab = modelTab;
                SetPreviewType("Model");
            }
            else
            {
                previewTabs.SelectedTab = detailsTab;
                SetPreviewType("Details");
            }
        }

        private void SetPreviewType(string type)
        {
            if (previewHeaderLabel != null) previewHeaderLabel.Text = "  " + type;
        }

        private static bool IsTextKind(string kind)
        {
            return kind == "text" || kind == "xml" || kind == "meta" || kind == "ps" || kind == "json" || kind == "csv";
        }

        private void LoadMediaForEntry(FileEntry entry)
        {
            try
            {
                if (entry == null || !settings.ShowMediaPlayer)
                {
                    ClearMedia(entry == null ? "No media selected." : "Media player hidden in settings.");
                    return;
                }
                bool isAwc = entry.Awc != null;
                bool isMedia = entry.Kind == "audio" || entry.Kind == "video" || isAwc;
                if (!isMedia)
                {
                    ClearMedia("No media selected.");
                    return;
                }
                byte[] data = entry.Data;
                string ext = RpfScanner.GetMediaExtension(entry);
                string label = entry.Name;
                if (isAwc)
                {
                    AwcStreamInfo playStem = null;
                    for (int i = 0; i < entry.Awc.Streams.Count; i++)
                    {
                        AwcStreamInfo s = entry.Awc.Streams[i];
                        RpfScanner.EnsureDecoded(s);
                        if (s.DecodedWav != null) { playStem = s; break; }
                    }
                    if (playStem != null && Ffmpeg.LooksLikeWavBytes(playStem.DecodedWav)) { data = playStem.DecodedWav; ext = ".wav"; label = entry.Name + " [decoded stem]"; } else if (playStem != null) { MessageBox.Show(this, "Cannot play stem: " + (playStem.DecodeError ?? "Decoded WAV is invalid"), "OpenRPF9"); return; }
                    else { mediaLabel.Text = entry.Name + " [AWC - no decodable stem; use Stems tab]"; ClearMediaPlayerOnly(); return; }
                }

                string dir = Path.Combine(Path.GetTempPath(), "OpenRPF9_media");
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                string name = Path.GetFileNameWithoutExtension(RpfScanner.SafeFileName(entry.Name));
                if (string.IsNullOrEmpty(name)) name = "media";
                string path = Path.Combine(dir, name + "_" + ((uint)entry.GetHashCode()).ToString("X8") + ext);
                using (FileStream fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read))
                {
                    byte[] bytes = data ?? new byte[0];
                    fs.Write(bytes, 0, bytes.Length);
                    fs.Flush(true);
                }
                currentMediaPath = path;
                mediaLabel.Text = label;
                PlayMediaPath(path, ext, settings.AutoPlayMedia);
            }
            catch (Exception ex)
            {
                AppLog.Line("Media load failed: " + ex.Message);
                ClearMedia("Media load failed: " + ex.Message);
            }
        }

        private static bool IsAudioExtension(string ext)
        {
            string e = (ext ?? "").ToLowerInvariant();
            return e == ".mp3" || e == ".wav" || e == ".wma" || e == ".m4a" || e == ".aac" || e == ".ogg";
        }

        private static bool IsVideoExtension(string ext)
        {
            string e = (ext ?? "").ToLowerInvariant();
            return e == ".mp4" || e == ".wmv" || e == ".avi" || e == ".mkv" || e == ".mov" || e == ".bk2";
        }

        private void PlayMediaPath(string path, string ext, bool autoPlay)
        {
            string e = (ext ?? "").ToLowerInvariant();
            if (IsAudioExtension(e))
            {
                PlayAudioWithFallbacks(path, e, autoPlay);
                return;
            }

            try
            {
                PlayMediaElement(path, autoPlay);
            }
            catch (Exception ex)
            {
                AppLog.Line("MediaElement failed: " + ex.Message);
                try
                {
                    PlayWmpComAudio(path, autoPlay);
                    mediaLabel.Text = Path.GetFileName(path) + " [WMP fallback]";
                }
                catch (Exception ex2)
                {
                    AppLog.Line("WMP fallback failed: " + ex2.Message);
                    throw new InvalidOperationException(ex.Message + " | " + ex2.Message);
                }
            }
        }

        private void PlayAudioWithFallbacks(string path, string ext, bool autoPlay)
        {
            List<string> errors = new System.Collections.Generic.List<string>();
            if (ext == ".wav")
            {
                try { PlayWavSoundPlayer(path, autoPlay); return; }
                catch (Exception ex) { errors.Add("SoundPlayer: " + ex.Message); AppLog.Line("SoundPlayer failed: " + ex.Message); }
            }
            try { PlayWmpComAudio(path, autoPlay); mediaLabel.Text = Path.GetFileName(path) + " [WMP]"; return; }
            catch (Exception ex) { errors.Add("WMP: " + ex.Message); AppLog.Line("WMP audio failed: " + ex.Message); }
            try { PlayWpfAudio(path, autoPlay); return; }
            catch (Exception ex) { errors.Add("WPF: " + ex.Message); AppLog.Line("WPF MediaPlayer audio failed: " + ex.Message); }
            try { PlayMediaElement(path, autoPlay); mediaLabel.Text = Path.GetFileName(path) + " [MediaElement]"; return; }
            catch (Exception ex) { errors.Add("MediaElement: " + ex.Message); AppLog.Line("MediaElement audio fallback failed: " + ex.Message); }
            try
            {
                byte[] wav = Ffmpeg.DecodeToWav(File.ReadAllBytes(path), ext);
                if (wav != null && wav.Length > 44)
                {
                    string ffPath = Path.Combine(Path.GetDirectoryName(path), Path.GetFileNameWithoutExtension(path) + "_ffmpeg.wav");
                    File.WriteAllBytes(ffPath, wav);
                    PlayWavSoundPlayer(ffPath, autoPlay);
                    mediaLabel.Text = Path.GetFileName(path) + " [FFmpeg]";
                    return;
                }
                errors.Add("FFmpeg: no output");
            }
            catch (Exception ex) { errors.Add("FFmpeg: " + ex.Message); AppLog.Line("FFmpeg audio fallback failed: " + ex.Message); }
            throw new InvalidOperationException(string.Join(" | ", errors.ToArray()));
        }
        private void PlayWavSoundPlayer(string path, bool autoPlay)
        {
            CloseActivePlayback();
            try { if (wavPreviewPlayer != null) { wavPreviewPlayer.Stop(); wavPreviewPlayer.Dispose(); wavPreviewPlayer = null; } } catch { }
            wavPreviewPlayer = new SoundPlayer(path);
            wavPreviewPlayer.Load();
            soundPlayerActive = true;
            if (autoPlay) wavPreviewPlayer.Play();
        }
        private void PlayWpfAudio(string path, bool autoPlay)
        {
            CloseActivePlayback();
            EnsureWpfMediaPlayer();
            wpfMediaPlayer.Volume = volumeBar.Value / 100.0;
            wpfMediaPlayer.Open(new Uri(path, UriKind.Absolute));
            mediaPlayerActive = true;
            if (autoPlay)
            {
                wpfMediaPlayer.Play();
            }
        }

        private void EnsureWpfMediaPlayer()
        {
            if (wpfMediaPlayer != null) return;
            wpfMediaPlayer = new System.Windows.Media.MediaPlayer();
            wpfMediaPlayer.MediaFailed += WpfMediaPlayer_MediaFailed;
            wpfMediaPlayer.MediaEnded += WpfMediaPlayer_MediaEnded;
        }

        private void RunOnUi(Action action)
        {
            if (action == null) return;
            try
            {
                if (IsDisposed) return;
                if (InvokeRequired) BeginInvoke(action);
                else action();
            }
            catch { }
        }

        private void WpfMediaPlayer_MediaFailed(object sender, System.Windows.Media.ExceptionEventArgs e)
        {
            try
            {
                string msg = e.ErrorException == null ? "Unknown error" : e.ErrorException.Message;
                Exception err = e.ErrorException;
                AppLog.Line("WPF MediaPlayer failed: " + err);
                RunOnUi(delegate
                {
                    if (!mediaPlayerActive) return;
                    string path = currentMediaPath;
                    if (!string.IsNullOrEmpty(path) && File.Exists(path))
                    {
                        try
                        {
                            PlayMediaElement(path, settings.AutoPlayMedia);
                            mediaLabel.Text = Path.GetFileName(path) + " [MediaElement fallback]";
                            return;
                        }
                        catch (Exception ex)
                        {
                            AppLog.Line("MediaElement fallback after WPF fail failed: " + ex.Message);
                            try
                            {
                                PlayWmpComAudio(path, settings.AutoPlayMedia);
                                mediaLabel.Text = Path.GetFileName(path) + " [WMP fallback]";
                                return;
                            }
                            catch (Exception ex2)
                            {
                                AppLog.Line("WMP fallback after WPF fail failed: " + ex2.Message);
                                msg = msg + " | " + ex.Message + " | " + ex2.Message;
                            }
                        }
                    }
                    mediaLabel.Text = "Media failed: " + msg;
                });
            }
            catch { }
        }

        private void WpfMediaPlayer_MediaEnded(object sender, EventArgs e)
        {
            try
            {
                RunOnUi(delegate
                {
                    if (mediaPlayerActive && wpfMediaPlayer != null)
                    {
                        try { wpfMediaPlayer.Position = TimeSpan.Zero; } catch { }
                        try { wpfMediaPlayer.Stop(); } catch { }
                    }
                });
            }
            catch { }
        }

        private void MediaElement_MediaFailed(object sender, System.Windows.ExceptionRoutedEventArgs e)
        {
            try
            {
                string msg = e.ErrorException == null ? "Unknown error" : e.ErrorException.Message;
                Exception err = e.ErrorException;
                AppLog.Line("Media failed: " + err);
                RunOnUi(delegate
                {
                    if (!mediaElementActive) return;
                    string path = currentMediaPath;
                    if (!string.IsNullOrEmpty(path) && File.Exists(path))
                    {
                        try
                        {
                            PlayWmpComAudio(path, settings.AutoPlayMedia);
                            mediaLabel.Text = Path.GetFileName(path) + " [WMP fallback]";
                            return;
                        }
                        catch (Exception ex)
                        {
                            AppLog.Line("WMP fallback failed: " + ex.Message);
                            msg = msg + " | " + ex.Message;
                        }
                    }
                    mediaLabel.Text = "Media failed: " + msg;
                });
            }
            catch { }
        }

        private void PlayMediaElement(string path, bool autoPlay)
        {
            CloseActivePlayback();
            mediaElementActive = true;
            mediaElement.Volume = volumeBar.Value / 100.0;
            mediaElement.Source = new Uri(path, UriKind.Absolute);
            if (autoPlay)
            {
                mediaElement.Play();
            }
        }

        private void PlayCurrentMedia()
        {
            try
            {
                AppLog.Line("PlayCurrentMedia path=" + (currentMediaPath ?? "") + " wmp=" + wmpAudioActive + " wpf=" + mediaPlayerActive + " me=" + mediaElementActive + " wav=" + soundPlayerActive);
                if (wmpAudioActive) { WmpInvoke("controls", "play", null); return; }
                if (mediaPlayerActive && wpfMediaPlayer != null) { wpfMediaPlayer.Play(); return; }
                if (mediaElementActive) { mediaElement.Play(); return; }
                if (soundPlayerActive && wavPreviewPlayer != null) { wavPreviewPlayer.Play(); return; }
                if (!string.IsNullOrEmpty(currentMediaPath) && File.Exists(currentMediaPath))
                {
                    PlayMediaPath(currentMediaPath, Path.GetExtension(currentMediaPath), true);
                    return;
                }
                mediaLabel.Text = "No media loaded.";
                AppLog.Line("PlayCurrentMedia: no media loaded");
            }
            catch (Exception ex) { AppLog.Line("Play failed: " + ex.Message); mediaLabel.Text = "Play failed: " + ex.Message; }
        }
        private void PauseCurrentMedia()
        {
            try
            {
                if (mediaPlayerActive && wpfMediaPlayer != null) { wpfMediaPlayer.Pause(); return; }
                if (mediaElementActive) { mediaElement.Pause(); return; }
                if (soundPlayerActive && wavPreviewPlayer != null) { wavPreviewPlayer.Stop(); return; }
            }
            catch (Exception ex) { AppLog.Line("Pause failed: " + ex.Message); }
        }

        private void StopCurrentMedia()
        {
            try
            {
                if (wmpAudioActive)
                {
                    WmpInvoke("controls", "stop", null);
                    try { WmpSetProperty("controls", "currentPosition", 0d); } catch { }
                    return;
                }
                if (mediaPlayerActive && wpfMediaPlayer != null) { wpfMediaPlayer.Stop(); try { wpfMediaPlayer.Position = TimeSpan.Zero; } catch { } return; }
                if (mediaElementActive) { mediaElement.Stop(); try { mediaElement.Position = TimeSpan.Zero; } catch { } return; }
                if (soundPlayerActive && wavPreviewPlayer != null) { wavPreviewPlayer.Stop(); return; }
            }
            catch (Exception ex) { AppLog.Line("Stop failed: " + ex.Message); }
        }
        private void SetCurrentMediaVolume(int value)
        {
            try
            {
                int vol = Math.Max(0, Math.Min(100, value));
                mediaElement.Volume = vol / 100.0;
                if (wpfMediaPlayer != null) wpfMediaPlayer.Volume = vol / 100.0;
                if (wmpAudioActive && wmpCom != null)
                {
                    try
                    {
                        object settingsObj = wmpCom.GetType().InvokeMember("settings", System.Reflection.BindingFlags.GetProperty, null, wmpCom, null);
                        if (settingsObj != null) settingsObj.GetType().InvokeMember("volume", System.Reflection.BindingFlags.SetProperty, null, settingsObj, new object[] { vol });
                    }
                    catch { }
                }
            }
            catch { }
        }

        private void PlayWmpComAudio(string path, bool autoPlay)
        {
            CloseActivePlayback();
            Type t = Type.GetTypeFromProgID("WMPlayer.OCX.7");
            if (t == null) t = Type.GetTypeFromProgID("WMPlayer.OCX");
            if (t == null) throw new InvalidOperationException("WMP COM not available");
            object wmp = Activator.CreateInstance(t);
            if (wmp == null) throw new InvalidOperationException("WMP COM create failed");
            wmpCom = wmp;
            wmpAudioActive = true;
            try
            {
                object settingsObj = wmpCom.GetType().InvokeMember("settings", System.Reflection.BindingFlags.GetProperty, null, wmpCom, null);
                if (settingsObj != null)
                {
                    int vol = Math.Max(0, Math.Min(100, volumeBar.Value));
                    settingsObj.GetType().InvokeMember("volume", System.Reflection.BindingFlags.SetProperty, null, settingsObj, new object[] { vol });
                    settingsObj.GetType().InvokeMember("autoStart", System.Reflection.BindingFlags.SetProperty, null, settingsObj, new object[] { false });
                }
            }
            catch { }
            wmpCom.GetType().InvokeMember("URL", System.Reflection.BindingFlags.SetProperty, null, wmpCom, new object[] { path });
            if (autoPlay) WmpInvoke("controls", "play", null);
            else WmpInvoke("controls", "stop", null);
        }

        private void WmpInvoke(string objectProperty, string method, object[] args)
        {
            try
            {
                if (wmpCom == null) { AppLog.Line("WMP invoke skipped: null com"); return; }
                object target = wmpCom;
                if (!string.IsNullOrEmpty(objectProperty))
                {
                    target = wmpCom.GetType().InvokeMember(objectProperty, System.Reflection.BindingFlags.GetProperty, null, wmpCom, null);
                }
                if (target == null) { AppLog.Line("WMP invoke skipped: null target " + objectProperty + "." + method); return; }
                target.GetType().InvokeMember(method, System.Reflection.BindingFlags.InvokeMethod, null, target, args);
            }
            catch (Exception ex) { AppLog.Line("WMP invoke failed " + objectProperty + "." + method + ": " + ex.Message); throw; }
        }

        private void WmpSetProperty(string objectProperty, string property, object value)
        {
            try
            {
                if (wmpCom == null) return;
                object target = wmpCom;
                if (!string.IsNullOrEmpty(objectProperty))
                {
                    target = wmpCom.GetType().InvokeMember(objectProperty, System.Reflection.BindingFlags.GetProperty, null, wmpCom, null);
                }
                if (target == null) return;
                target.GetType().InvokeMember(property, System.Reflection.BindingFlags.SetProperty, null, target, new object[] { value });
            }
            catch (Exception ex) { AppLog.Line("WMP set failed " + objectProperty + "." + property + ": " + ex.Message); throw; }
        }
        private void CloseWmpComAudio()
        {
            if (wmpCom == null) { wmpAudioActive = false; return; }
            try { WmpInvoke("controls", "stop", null); } catch { }
            try { wmpCom.GetType().InvokeMember("close", System.Reflection.BindingFlags.InvokeMethod, null, wmpCom, null); } catch { }
            try { System.Runtime.InteropServices.Marshal.FinalReleaseComObject(wmpCom); } catch { }
            wmpCom = null;
            wmpAudioActive = false;
        }

        private void CloseWpfMediaPlayer()
        {
            if (wpfMediaPlayer == null) { mediaPlayerActive = false; return; }
            try { wpfMediaPlayer.Stop(); } catch { }
            try { wpfMediaPlayer.Close(); } catch { }
            mediaPlayerActive = false;
        }

        private void CloseMediaElement()
        {
            mediaElementActive = false;
            try { mediaElement.Stop(); mediaElement.Source = null; } catch { }
        }

        private void CloseActivePlayback()
        {
            CloseWmpComAudio();
            CloseWpfMediaPlayer();
            CloseMediaElement();
        }

        private void ClearMediaPlayerOnly()
        {
            CloseActivePlayback();
        }

        private void ClearMedia(string msg)
        {
            ClearMediaPlayerOnly();
            mediaLabel.Text = msg;
        }

        private void CleanupMedia()
        {
            ClearMediaPlayerOnly();
            currentMediaPath = null;
        }

        private void PopulateStems(FileEntry entry)
        {
            stemsList.Items.Clear();
            if (entry == null || entry.Awc == null) return;
            for (int i = 0; i < entry.Awc.Streams.Count; i++)
            {
                AwcStreamInfo s = entry.Awc.Streams[i];
                RpfScanner.EnsureDecoded(s);
                ListViewItem item = new ListViewItem(s.Index.ToString());
                item.SubItems.Add(string.IsNullOrEmpty(s.CodecName) ? "?" : s.CodecName);
                item.SubItems.Add(s.SampleRate > 0 ? s.SampleRate.ToString() : "?");
                item.SubItems.Add(s.Channels > 0 ? s.Channels.ToString() : "?");
                item.SubItems.Add(s.Samples > 0 ? s.Samples.ToString() : "?");
                item.SubItems.Add("0x" + s.Offset.ToString("X"));
                item.SubItems.Add(s.Size.ToString());
                item.SubItems.Add(s.DecodedWav != null ? "WAV ready" : (s.DecodeError ?? "raw only"));
                item.Tag = s;
                stemsList.Items.Add(item);
            }
        }

        private void ExportSelected()
        {
            FileEntry entry = GetSelectedFileEntry();
            if (entry == null) { MessageBox.Show(this, "Select a file first.", "OpenRPF9", MessageBoxButtons.OK, MessageBoxIcon.Information); return; }
            bool texture = entry.Kind == "texture" && entry.TextureDict != null &&
                entry.TextureDict.Textures != null && entry.TextureDict.Textures.Count > 0;
            bool imagePng = texture || entry.Ttpf != null || (entry.Data != null && RpfScanner.LooksLikeDds(entry.Data));
            SaveFileDialog dlg = new SaveFileDialog();
            string clean = Path.GetFileNameWithoutExtension(RpfScanner.SafeFileName(entry.Name ?? ""));
            if (string.IsNullOrEmpty(clean)) clean = imagePng ? "texture" : "file";
            if (imagePng)
            {
                dlg.FileName = clean + ".png";
                dlg.Filter = "PNG Image (*.png)|*.png";
                dlg.DefaultExt = "png";
            }
            else
            {
                dlg.FileName = Path.GetFileName(RpfScanner.SafeFileName(entry.Name ?? ""));
                dlg.Filter = "All Files (*.*)|*.*";
            }
            if (dlg.ShowDialog(this) != DialogResult.OK) return;
            int index = -1;
            if (texture && imageTextureBox != null && object.ReferenceEquals(imageTextureBox.Tag, entry))
                index = imageTextureBox.SelectedIndex;
            byte[] bytes = RpfScanner.ExportBytes(entry, index);
            if (bytes == null)
            {
                MessageBox.Show(this, "Texture export failed: the image could not be decoded.", "OpenRPF9", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            File.WriteAllBytes(dlg.FileName, bytes);
            string dims = RpfScanner.PngSize(bytes);
            statusLabel.Text = "Exported " + Path.GetFileName(dlg.FileName) + (dims.Length > 0 ? " at " + dims : "");
        }

        private void ExportAll()
        {
            if (allEntries.Count == 0) { MessageBox.Show(this, "No files to export.", "OpenRPF9", MessageBoxButtons.OK, MessageBoxIcon.Information); return; }
            FolderBrowserDialog dlg = new FolderBrowserDialog();
            dlg.Description = "Select export folder";
            if (dlg.ShowDialog(this) != DialogResult.OK) return;
            string baseDir = Path.Combine(dlg.SelectedPath, "OpenRPF9_export");
            Directory.CreateDirectory(baseDir);
            int exported = 0;
            int failed = 0;
            for (int i = 0; i < allEntries.Count; i++)
            {
                FileEntry e = allEntries[i];
                byte[] bytes = RpfScanner.ExportBytes(e, -1);
                if (bytes == null) { failed++; continue; }
                string rel = RpfScanner.SafeFileName(e.Name);
                if (RpfScanner.LooksLikePng(bytes) && !rel.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
                    rel = Path.GetFileNameWithoutExtension(rel) + ".png";
                string outPath = Path.Combine(baseDir, rel.Replace('/', Path.DirectorySeparatorChar));
                string dir = Path.GetDirectoryName(outPath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
                if (File.Exists(outPath)) outPath = Path.Combine(dir, Path.GetFileNameWithoutExtension(outPath) + "_" + i + Path.GetExtension(outPath));
                File.WriteAllBytes(outPath, bytes);
                exported++;
            }
            string msg = "Exported " + exported + " files to:\n" + baseDir;
            if (failed > 0) msg += "\nSkipped " + failed + " textures that could not be decoded.";
            MessageBox.Show(this, msg, "OpenRPF9", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
    }

    internal static class RpfScanner
    {
        public static ArchiveInfo Scan(string path, CancellationToken token, Action<long, long, int> progress, Action<FileEntry> onEntry)
        {
            ArchiveInfo info = new ArchiveInfo();
            info.Path = Path.GetFullPath(path);
            AppLog.Line("Scan start " + info.Path);
            using (FileStream fs = new FileStream(info.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                info.Size = fs.Length;
                byte[] hdr = new byte[16];
                int got = fs.Read(hdr, 0, hdr.Length);
                info.MagicAscii = got >= 4 ? Encoding.ASCII.GetString(hdr, 0, 4) : "";
                info.MagicLE = got >= 4 ? BitConverter.ToUInt32(hdr, 0) : 0;
                info.MagicBE = got >= 4 ? ReadU32BE(hdr, 0) : 0;
                info.DetectedFormat = DetectFormat(hdr, got);
                info.MagicEndianReversed = IsReversedRpfMagic(info.MagicAscii);
                AppLog.Line("Scan format=" + info.DetectedFormat + " size=" + info.Size);

                if (TtpfFile.HasMagic(hdr, got))
                {
                    info.DetectedFormat = "TTPF (RDS5/ETPF)";
                    info.Entries = BuildTtpfEntries(info.Path, token, progress);
                    AppLog.Line("Scan TTPF textures=" + info.Entries.Count);
                    return info;
                }

                List<CarvedStream> streams = FindStreams(fs, token, progress);
                AppLog.Line("Scan streams=" + streams.Count);
                streams = RecoverTextureDictionaries(fs, streams, token);
                AppLog.Line("Scan streams after texture recovery=" + streams.Count);
                List<FileEntry> entries = BuildEntries(streams);
                info.Entries = entries;
                AppLog.Line("Scan entries=" + entries.Count);
                for (int i = 0; i < entries.Count; i++)
                {
                    if (token.IsCancellationRequested) break;
                    if (onEntry != null) onEntry(entries[i]);
                }
            }
            return info;
        }
        public static uint ReadU32BE(byte[] b, int o)
        {
            return ((uint)b[o] << 24) | ((uint)b[o + 1] << 16) | ((uint)b[o + 2] << 8) | b[o + 3];
        }


        private static int MpegFrameSize(uint hdr)
        {
            if (((hdr >> 21) & 0x7FF) != 0x7FF) return 0;
            int version = (int)((hdr >> 19) & 3);
            int layer = (int)((hdr >> 17) & 3);
            int bitrateIndex = (int)((hdr >> 12) & 0xF);
            int sampleRateIndex = (int)((hdr >> 10) & 3);
            int padding = (int)((hdr >> 9) & 1);
            if (version == 1 || layer == 0 || bitrateIndex == 0 || bitrateIndex == 15 || sampleRateIndex == 3) return 0;
            int[,] bitrates = { { 0,0,0,0,0 }, { 32,32,32,32,8 }, { 64,48,40,48,16 }, { 96,56,48,56,24 }, { 128,64,56,64,32 }, { 160,80,64,80,40 }, { 192,96,80,96,48 }, { 224,112,96,112,56 }, { 256,128,112,128,64 }, { 288,160,128,144,80 }, { 320,192,160,160,96 }, { 352,224,192,176,112 }, { 384,256,224,192,128 }, { 416,320,256,224,144 }, { 448,384,320,256,160 } };
            int sr;
            if (version == 3) sr = sampleRateIndex == 0 ? 44100 : sampleRateIndex == 1 ? 48000 : sampleRateIndex == 2 ? 32000 : 0;
            else if (version == 2) sr = sampleRateIndex == 0 ? 22050 : sampleRateIndex == 1 ? 24000 : sampleRateIndex == 2 ? 16000 : 0;
            else sr = sampleRateIndex == 0 ? 11025 : sampleRateIndex == 1 ? 12000 : sampleRateIndex == 2 ? 8000 : 0;
            int br = 0;
            if (version == 3 && layer == 3) br = bitrates[bitrateIndex, 0];
            else if (version == 3 && layer == 2) br = bitrates[bitrateIndex, 1];
            else if (version == 3 && layer == 1) br = bitrates[bitrateIndex, 2];
            else if (version != 3 && layer == 3) br = bitrates[bitrateIndex, 3];
            else br = bitrates[bitrateIndex, 4];
            if (br <= 0 || sr <= 0) return 0;
            if (layer == 3) return (12 * br * 1000 / sr + padding) * 4;
            return 144 * br * 1000 / sr + padding;
        }
        private static bool IsRpfMagic(uint v)
        {
            return v == 0x52504630u || v == 0x52504632u || v == 0x52504633u || v == 0x52504634u || v == 0x52504636u || v == 0x52504637u || v == 0x52504638u || v == 0x52504639u;
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
            char[] arr = ascii.ToCharArray(); Array.Reverse(arr);
            string rev = new string(arr);
            return rev.StartsWith("RPF");
        }

        private static string DetectFormat(byte[] bytes, int got)
        {
            if (got < 4) return "Unknown";
            uint le = BitConverter.ToUInt32(bytes, 0);
            uint be = ReadU32BE(bytes, 0);
            if (IsRpfMagic(le)) return MagicToName(le) + " (LE magic)";
            if (IsRpfMagic(be)) return MagicToName(be) + " (BE magic)";
            string ascii = Encoding.ASCII.GetString(bytes, 0, 4);
            if (string.Equals(ascii, "RDS5", StringComparison.Ordinal)) return "TTPF (RDS5/ETPF)";
            if (IsReversedRpfMagic(ascii)) { char[] arr = ascii.ToCharArray(); Array.Reverse(arr); return new string(arr) + " (reversed bytes)"; }
            return "Unknown (" + ascii + ")";
        }

        private static List<CarvedStream> FindStreams(FileStream fs, CancellationToken token, Action<long, long, int> progress)
        {
            List<CarvedStream> list = new System.Collections.Generic.List<CarvedStream>();
            long len = fs.Length; long scanLen = len; string limEnv = Environment.GetEnvironmentVariable("OPENRPF9_SCAN_MAX"); if (!string.IsNullOrEmpty(limEnv)) { long mb; if (long.TryParse(limEnv, out mb) && mb > 0) scanLen = Math.Min(len, mb * 1024L * 1024L); } AppLog.Line("FindStreams len="+len+" scanLen="+scanLen);
            byte[] buf = new byte[1024 * 1024];
            byte[] tail = new byte[15];
            int tailLen = 0;
            long pos = 0;
            long lastProgress = 0;
            int found = 0;
            int zlibAttempts = 0;
            int zlibOk = 0;
            while (pos < scanLen)
            {
                if (token.IsCancellationRequested) break;
                fs.Position = pos;
                int want = (int)Math.Min(buf.Length, scanLen - pos);
                int read = fs.Read(buf, 0, want);
                if (read <= 0) break;

                for (int i = 0; i < read; i++)
                {
                    long abs = pos + i;
                    byte b = buf[i];

                    if (b == 0x78 && i + 1 < read)
                    {
                        byte b2 = buf[i + 1];
                        if (b2 == 0x01 || b2 == 0x5E || b2 == 0x9C || b2 == 0xDA)
                        {
                            zlibAttempts++;
                            CarvedStream s = TryDecompressAt(fs, abs);
                            if (s != null && s.Data != null && s.Data.Length > 0)
                            {
                                zlibOk++;
                                list.Add(s);
                                found++;
                                long next = AlignUp(abs + s.DecompressedConsumed, 0x10);
                                if (next <= abs) next = abs + 1;
                                if (next > pos + i)
                                {
                                    long skip = next - (pos + i);
                                    if (skip < read - i)
                                    {
                                        int target = (int)(i + skip);
                                        i = target - 1;
                                        tailLen = 0;
                                        if (target >= 3) { tail[0] = buf[target - 3]; tail[1] = buf[target - 2]; tail[2] = buf[target - 1]; tailLen = 3; }
                                        continue;
                                    }
                                    else
                                    {
                                        if (next >= 3)
                                        {
                                            byte[] pre = ReadRawBlockStatic(fs, next - 3, 3);
                                            if (pre.Length == 3) { tail[0] = pre[0]; tail[1] = pre[1]; tail[2] = pre[2]; tailLen = 3; }
                                        }
                                        else tailLen = 0;
                                        pos = next;
                                        goto nextRead;
                                    }
                                }
                            }
                        }
                    }

                    bool adat = tailLen >= 3 && tail[tailLen - 3] == (byte)'A' && tail[tailLen - 2] == (byte)'D' && tail[tailLen - 1] == (byte)'A' && b == (byte)'T';
                    bool tada = tailLen >= 3 && tail[tailLen - 3] == (byte)'T' && tail[tailLen - 2] == (byte)'A' && tail[tailLen - 1] == (byte)'D' && b == (byte)'A';
                    if ((adat || tada) && ((abs - 3) % 16) == 0)
                    {
                        long hit = abs - 3;
                        byte[] block = ReadRawBlockStatic(fs, hit, 1024 * 1024);
                        long declared = ComputeAwcEnd(block, fs.Length - hit);
                        if (declared > 0)
                        {
                            long take = declared;
                            if (take > int.MaxValue) take = int.MaxValue;
                            if (take > block.Length) block = ReadRawBlockStatic(fs, hit, (int)take);
                            if (block.Length > take) Array.Resize(ref block, (int)take);
                            CarvedStream s = new CarvedStream();
                            s.Offset = hit;
                            s.Data = block;
                            s.DecompressedSize = block.Length;
                            s.DecompressedConsumed = 0;
                            s.Kind = "awc";
                            s.Preview = "AWC " + (adat ? "ADAT" : "TADA") + " " + block.Length + " bytes";
                            list.Add(s);
                            found++;
                            long awcNext = AlignUp(hit + block.Length, 0x10);
                            if (awcNext > pos + i)
                            {
                                long awcSkip = awcNext - (pos + i);
                                if (awcSkip < read - i)
                                {
                                    int target = (int)(i + awcSkip);
                                    i = target - 1;
                                    tailLen = 0;
                                    if (target >= 3) { tail[0] = buf[target - 3]; tail[1] = buf[target - 2]; tail[2] = buf[target - 1]; tailLen = 3; }
                                    continue;
                                }
                                if (awcNext >= 3)
                                {
                                    byte[] pre = ReadRawBlockStatic(fs, awcNext - 3, 3);
                                    if (pre.Length == 3) { tail[0] = pre[0]; tail[1] = pre[1]; tail[2] = pre[2]; tailLen = 3; }
                                }
                                else tailLen = 0;
                                pos = awcNext;
                                goto nextRead;
                            }
                        }
                    }

                    bool xmlStart = b == (byte)'l' && tailLen >= 4 &&
                                    tail[tailLen - 4] == (byte)'<' && tail[tailLen - 3] == (byte)'?' &&
                                    tail[tailLen - 2] == (byte)'x' && tail[tailLen - 1] == (byte)'m';
                    if (xmlStart && ((abs - 4) % 16) == 0)
                    {
                        long xmlHit = abs - 4;
                        byte[] probe = ReadRawBlockStatic(fs, xmlHit, 64 * 1024);
                        int xmlLength = XmlCarver.MeasureDocument(probe);
                        if (xmlLength <= 0 && probe.Length >= 64 * 1024)
                        {
                            probe = ReadRawBlockStatic(fs, xmlHit, 16 * 1024 * 1024);
                            xmlLength = XmlCarver.MeasureDocument(probe);
                        }
                        if (xmlLength > 0)
                        {
                            byte[] document = new byte[xmlLength];
                            Array.Copy(probe, 0, document, 0, xmlLength);
                            if (XmlCarver.IsWellFormed(document))
                            {
                                CarvedStream s = new CarvedStream();
                                s.Offset = xmlHit;
                                s.Data = document;
                                s.DecompressedSize = document.Length;
                                s.DecompressedConsumed = 0;
                                s.Kind = "text";
                                s.Preview = MakePreview(document, "text");
                                list.Add(s);
                                found++;
                            }
                        }
                    }

                    bool kb2 = tailLen >= 3 && tail[tailLen - 3] == (byte)'K' && tail[tailLen - 2] == (byte)'B' && tail[tailLen - 1] == (byte)'2';
                    if (kb2 && ((abs - 3) % 16) == 0)
                    {
                        long hit = abs - 3;
                        byte[] block = ReadRawBlockStatic(fs, hit, 64 * 1024 * 1024);
                        if (LooksLikeBk2(block))
                        {
                            CarvedStream s = new CarvedStream();
                            s.Offset = hit;
                            s.Data = block;
                            s.DecompressedSize = block.Length;
                            s.DecompressedConsumed = 0;
                            s.Kind = "video";
                            s.Preview = "BK2 " + block.Length + " bytes";
                            list.Add(s);
                            found++;
                        }
                    }

                    if (tailLen < 15) tail[tailLen++] = b;
                    else { Array.Copy(tail, 1, tail, 0, 14); tail[14] = b; }
                }

                pos += read;
                if (progress != null && (pos - lastProgress >= 8 * 1024 * 1024 || pos >= scanLen))
                {
                    lastProgress = pos;
                    progress(pos, scanLen, found);
                }
            nextRead: ;
            }
            AppLog.Line("FindStreams zlibAttempts=" + zlibAttempts + " zlibOk=" + zlibOk + " found=" + found);
            return list;
        }

        private static long AlignUp(long value, int align)
        {
            if (align <= 1) return value;
            long rem = value % align;
            return rem == 0 ? value : value + (align - rem);
        }

        private static List<CarvedStream> RecoverTextureDictionaries(FileStream fs, List<CarvedStream> streams, CancellationToken token)
        {
            if (streams == null || streams.Count == 0) return streams;
            streams.Sort(delegate(CarvedStream a, CarvedStream b) { return a.Offset.CompareTo(b.Offset); });
            Dictionary<long, CarvedStream> byOffset = new Dictionary<long, CarvedStream>();
            for (int i = 0; i < streams.Count; i++)
            {
                if (!byOffset.ContainsKey(streams[i].Offset)) byOffset[streams[i].Offset] = streams[i];
            }
            HashSet<long> consumed = new HashSet<long>();
            HashSet<long> attempted = new HashSet<long>();
            List<CarvedStream> outList = new List<CarvedStream>(streams.Count);
            for (int i = 0; i < streams.Count; i++)
            {
                if (token.IsCancellationRequested) break;
                CarvedStream s = streams[i];
                if (consumed.Contains(s.Offset)) continue;
                if (s.Data == null || !RageTextureTools.LooksLikeDictionary(s.Data)) { outList.Add(s); continue; }

                List<CarvedStream> blocks = new List<CarvedStream>();
                blocks.Add(s);
                byte[] system = s.Data;
                bool ok = true;

                while (true)
                {
                    int requiredSystem;
                    if (!RageTextureTools.TryGetMinimumSystemBytes(system, out requiredSystem)) { ok = false; break; }
                    if (requiredSystem <= system.Length) break;
                    CarvedStream next = FindNextZlibBlock(fs, byOffset, blocks[blocks.Count - 1], attempted, token);
                    if (next == null) { ok = false; break; }
                    blocks.Add(next);
                    system = ConcatBytes(system, next.Data);
                    if (system.Length > RageTextureTools.MaxSystemBytes) { ok = false; break; }
                }
                if (!ok) { outList.Add(s); continue; }

                int sysPages, gfxPages;
                if (!RageTextureTools.TryGetResourcePageCounts(system, out sysPages, out gfxPages)) { outList.Add(s); continue; }
                int requiredGraphics;
                if (!RageTextureTools.TryGetRequiredGraphicsBytes(system, out requiredGraphics)) { outList.Add(s); continue; }

                MemoryStream gfxStream = new MemoryStream();
                while (gfxStream.Length < requiredGraphics)
                {
                    if (token.IsCancellationRequested) { ok = false; break; }
                    CarvedStream next = FindNextZlibBlock(fs, byOffset, blocks[blocks.Count - 1], attempted, token);
                    if (next == null) { ok = false; break; }
                    blocks.Add(next);
                    gfxStream.Write(next.Data, 0, next.Data.Length);
                    if (gfxStream.Length > RageTextureTools.MaxGraphicsBytes) { ok = false; break; }
                }
                if (!ok || gfxStream.Length == 0) { outList.Add(s); continue; }

                byte[] graphics = gfxStream.ToArray();
                RageTextureDictionary dictionary = RageTextureTools.Inspect(system, graphics);
                if (dictionary == null || dictionary.Textures.Count == 0) { outList.Add(s); continue; }

                for (int b = 0; b < blocks.Count; b++) consumed.Add(blocks[b].Offset);

                CarvedStream dict = new CarvedStream();
                dict.Offset = s.Offset;
                dict.Data = ConcatBytes(system, graphics);
                dict.DecompressedSize = dict.Data.Length;
                CarvedStream lastBlock = blocks[blocks.Count - 1];
                int lastPacked = GetExactPackedLength(fs, lastBlock);
                dict.DecompressedConsumed = (int)Math.Min(int.MaxValue, (lastBlock.Offset + lastPacked) - s.Offset);
                dict.TextureSystemBytes = system.Length;
                dict.Kind = "texture";
                StringBuilder sb = new StringBuilder();
                sb.AppendLine("Texture dictionary: " + dictionary.Textures.Count + " textures (system=" + system.Length + ", graphics=" + graphics.Length + " bytes)");
                int show = Math.Min(dictionary.Textures.Count, 64);
                for (int t = 0; t < show; t++) sb.AppendLine("  " + dictionary.Textures[t].Describe());
                if (dictionary.Textures.Count > show) sb.AppendLine("  ... " + (dictionary.Textures.Count - show) + " more");
                dict.Preview = sb.ToString().TrimEnd();
                outList.Add(dict);
            }
            return outList;
        }

        private static byte[] ConcatBytes(byte[] a, byte[] b)
        {
            byte[] result = new byte[a.Length + b.Length];
            Buffer.BlockCopy(a, 0, result, 0, a.Length);
            Buffer.BlockCopy(b, 0, result, a.Length, b.Length);
            return result;
        }

        private static int GetExactPackedLength(FileStream fs, CarvedStream s)
        {
            if (s == null) return 0;
            int guess = s.DecompressedConsumed;
            if (s.Data == null || s.Data.Length == 0 || guess < 6) return guess;
            try
            {
                int max = (int)Math.Min((long)guess + 4, fs.Length - s.Offset);
                if (max < 6) return guess;
                byte[] raw = ReadRawBlockStatic(fs, s.Offset, max);
                if (raw.Length < 6) return guess;
                uint adler = Adler32(s.Data);
                byte c0 = (byte)(adler >> 24), c1 = (byte)(adler >> 16), c2 = (byte)(adler >> 8), c3 = (byte)adler;
                for (int p = raw.Length - 4; p >= 2; p--)
                {
                    if (raw[p] != c0 || raw[p + 1] != c1 || raw[p + 2] != c2 || raw[p + 3] != c3) continue;
                    int packed = p + 4;
                    if (packed >= 6 && PackedStreamMatches(fs, s.Offset, packed, s.Data)) return packed;
                }
            }
            catch { }
            return guess;
        }

        private static bool PackedStreamMatches(FileStream fs, long offset, int packed, byte[] expected)
        {
            try
            {
                using (OffsetStream os = new OffsetStream(fs, offset, packed, true))
                {
                    os.ReadByte();
                    os.ReadByte();
                    using (DeflateStream ds = new DeflateStream(os, CompressionMode.Decompress, true))
                    using (MemoryStream ms = new MemoryStream())
                    {
                        byte[] buf = new byte[65536];
                        int n;
                        while ((n = ds.Read(buf, 0, buf.Length)) > 0) ms.Write(buf, 0, n);
                        if (ms.Length != expected.Length) return false;
                        byte[] got = ms.ToArray();
                        for (int i = 0; i < got.Length; i++) if (got[i] != expected[i]) return false;
                        return true;
                    }
                }
            }
            catch { return false; }
        }

        private static CarvedStream FindNextZlibBlock(FileStream fs, Dictionary<long, CarvedStream> byOffset, CarvedStream previous, HashSet<long> attempted, CancellationToken token)
        {
            if (previous == null) return null;
            int packed = GetExactPackedLength(fs, previous);
            if (packed < 6) return null;
            long end = previous.Offset + packed;
            long next = AlignUp(end, 16);
            if (next <= previous.Offset || next + 6 > fs.Length) return null;
            if (next > end)
            {
                int padLength = (int)(next - end);
                byte[] pad = ReadRawBlockStatic(fs, end, padLength);
                if (pad.Length != padLength) return null;
                for (int i = 0; i < pad.Length; i++) if (pad[i] != 0) return null;
            }
            CarvedStream found;
            if (byOffset.TryGetValue(next, out found) && found.Data != null && found.Data.Length > 0) return found;
            if (!attempted.Add(next)) return null;
            token.ThrowIfCancellationRequested();
            CarvedStream created = TryDecompressAt(fs, next);
            if (created == null || created.Data == null || created.Data.Length == 0) return null;
            byOffset[next] = created;
            return created;
        }

        private static int CountTextureDictTextures(byte[] sys)
        {
            try { if (!LooksLikeTextureDictionary(sys)) return 0; return U16(sys, 0x38); } catch { return 0; }
        }
private static CarvedStream TryDecompressAt(FileStream fs, long offset)
        {
            long save = -1;
            try
            {
                save = fs.Position;
                fs.Position = offset;
                byte[] hdr = new byte[2];
                if (fs.Read(hdr, 0, 2) != 2) return null;
                if (hdr[0] != 0x78 || (hdr[1] != 0x01 && hdr[1] != 0x5E && hdr[1] != 0x9C && hdr[1] != 0xDA)) return null;
                fs.Position = offset;
                using (OffsetStream os = new OffsetStream(fs, offset, fs.Length - offset, true))
                {
                    os.ReadByte();
                    os.ReadByte();
                    using (DeflateStream ds = new DeflateStream(os, CompressionMode.Decompress, true))
                    using (MemoryStream outMs = new MemoryStream())
                    {
                        byte[] buf = new byte[65536];
                        int n;
                        while ((n = ds.Read(buf, 0, buf.Length)) > 0) outMs.Write(buf, 0, n);
                        int consumed = (int)os.Position;
                        if (outMs.Length <= 0) return null;
                        byte[] carved = outMs.ToArray();
                        if (!HasValidZlibChecksum(fs, offset, consumed, carved)) return null;
                        CarvedStream s = new CarvedStream();
                        s.Offset = offset;
                        s.Data = carved;
                        s.DecompressedSize = s.Data.Length;
                        s.DecompressedConsumed = consumed;
                        s.Kind = DetectKind(s.Data);
                        s.Preview = MakePreview(s.Data, s.Kind);
                        return s;
                    }
                }
            }
            catch
            {
                return null;
            }
            finally
            {
                try { if (save >= 0) fs.Position = save; } catch { }
            }
        }
        private static uint Adler32(byte[] data)
        {
            uint a = 1, b = 0;
            for (int i = 0; i < data.Length; i++)
            {
                a = (a + data[i]) % 65521u;
                b = (b + a) % 65521u;
            }
            return (b << 16) | a;
        }

        private static bool HasValidZlibChecksum(FileStream fs, long offset, int consumed, byte[] output)
        {
            if (output == null || output.Length == 0 || consumed < 6) return false;
            uint adler = Adler32(output);
            long available = fs.Length - offset;
            int want = (int)Math.Min((long)consumed + 4, Math.Min(available, int.MaxValue));
            if (want < 6) return false;
            byte[] raw = ReadRawBlockStatic(fs, offset, want);
            if (raw.Length < 6) return false;
            byte c0 = (byte)(adler >> 24), c1 = (byte)(adler >> 16), c2 = (byte)(adler >> 8), c3 = (byte)adler;
            for (int p = raw.Length - 4; p >= 2; p--)
            {
                if (raw[p] == c0 && raw[p + 1] == c1 && raw[p + 2] == c2 && raw[p + 3] == c3) return true;
            }
            return false;
        }

        public static byte[] ReadRawBlockStatic(FileStream fs, long offset, int max)
        {
            try
            {
                long save = fs.Position;
                fs.Position = offset;
                int len = (int)Math.Min(max, fs.Length - offset);
                if (len <= 0) { fs.Position = save; return new byte[0]; }
                byte[] data = new byte[len];
                int got = 0;
                while (got < len)
                {
                    int n = fs.Read(data, got, len - got);
                    if (n <= 0) break;
                    got += n;
                }
                if (got != len) { byte[] t = new byte[got]; Array.Copy(data, 0, t, 0, got); data = t; }
                fs.Position = save;
                return data;
            }
            catch { return new byte[0]; }
        }

        private sealed class OffsetStream : Stream
        {
            private FileStream fs;
            private long start;
            private long length;
            private long pos;
            private bool leaveOpen;
            public OffsetStream(FileStream fs, long start, long length, bool leaveOpen) { this.fs = fs; this.start = start; this.length = length < 0 ? 0 : length; this.pos = 0; this.leaveOpen = leaveOpen; fs.Position = start; }
            public override bool CanRead { get { return true; } }
            public override bool CanSeek { get { return true; } }
            public override bool CanWrite { get { return false; } }
            public override long Length { get { return length; } }
            public override long Position { get { return pos; } set { Seek(value, SeekOrigin.Begin); } }
            public override int Read(byte[] buffer, int offset, int count)
            {
                if (pos >= length) return 0;
                if (count > length - pos) count = (int)(length - pos);
                fs.Position = start + pos;
                int n = fs.Read(buffer, offset, count);
                if (n > 0) pos += n;
                return n;
            }
            public override long Seek(long offset, SeekOrigin origin)
            {
                long target = origin == SeekOrigin.Begin ? offset : origin == SeekOrigin.Current ? pos + offset : length + offset;
                if (target < 0) target = 0; if (target > length) target = length;
                pos = target; fs.Position = start + pos; return pos;
            }
            public override void Flush() { }
            public override void SetLength(long value) { throw new NotSupportedException(); }
            public override void Write(byte[] buffer, int offset, int count) { throw new NotSupportedException(); }
            protected override void Dispose(bool disposing) { base.Dispose(disposing); }
        }

        private static List<FileEntry> BuildEntries(List<CarvedStream> streams)
        {
            List<FileEntry> entries = new System.Collections.Generic.List<FileEntry>();
            if (streams == null) return entries;

            List<CarvedStream> ordered = new System.Collections.Generic.List<CarvedStream>(streams);
            ordered.Sort(delegate(CarvedStream a, CarvedStream b) { return a.Offset.CompareTo(b.Offset); });
            List<CarvedStream> doc = new System.Collections.Generic.List<CarvedStream>();
            for (int i = 0; i < ordered.Count; i++)
            {
                CarvedStream s = ordered[i];
                if (IsXmlFragment(s))
                {
                    if (doc.Count > 0 &&
                        (XmlCarver.StartsWithDeclaration(s.Data) || XmlCarver.IsStandaloneDocument(s.Data)))
                    {
                        FlushXmlEntry(entries, doc);
                        doc.Clear();
                    }
                    doc.Add(s);
                    if (XmlCarver.MayEndDocument(s.Data, doc[0].Data) &&
                        XmlCarver.IsWellFormed(MergeXmlParts(doc)))
                    {
                        FlushXmlEntry(entries, doc);
                        doc.Clear();
                    }
                    continue;
                }
                if (doc.Count > 0)
                {
                    FlushXmlEntry(entries, doc);
                    doc.Clear();
                }
                entries.Add(MakeBinaryEntry(s, entries.Count));
            }
            if (doc.Count > 0) FlushXmlEntry(entries, doc);

            entries.Sort(delegate(FileEntry a, FileEntry b) { return a.Offset.CompareTo(b.Offset); });
            FileNaming.Assign(entries);
            return entries;
        }

        private static List<FileEntry> BuildTtpfEntries(string path, CancellationToken token, Action<long, long, int> progress)
        {
            List<FileEntry> entries = new List<FileEntry>();
            TtpfFile ttpf;
            try { ttpf = TtpfFile.Open(path); }
            catch (Exception ex)
            {
                AppLog.Line("TTPF open failed: " + ex.Message);
                throw new InvalidDataException("TTPF could not be parsed: " + ex.Message);
            }
            string stem = SafeBaseName(Path.GetFileNameWithoutExtension(path));
            for (int i = 0; i < ttpf.Textures.Count; i++)
            {
                if (token.IsCancellationRequested) break;
                TtpfTexture t = ttpf.Textures[i];
                FileEntry entry = new FileEntry();
                entry.Offset = t.BaseOffset;
                entry.Size = t.AllocatedBytes > int.MaxValue ? int.MaxValue : (int)t.AllocatedBytes;
                entry.Kind = "image";
                entry.Ttpf = t;
                entry.TtpfSource = ttpf;
                entry.Preview = t.Width + "x" + t.Height + " " + t.FormatName + " " + t.Shape + (string.IsNullOrEmpty(t.Problem) ? "" : " | " + t.Problem);
                entry.Name = "textures/" + stem + "_" + i.ToString("00000") + "_" + t.Width + "x" + t.Height + "_" + t.FormatName + ".png";
                entries.Add(entry);
                if (progress != null) progress(i + 1, ttpf.Textures.Count, i + 1);
            }
            return entries;
        }

        private static bool IsXmlFragment(CarvedStream s)
        {
            if (s == null || s.Data == null || s.Data.Length < 2) return false;
            if (s.Kind == "text") return XmlCarver.IsXml(s.Data);
            if (s.Kind == "binary" && XmlCarver.HasRsc7Header(s.Data)) return XmlCarver.IsXml(s.Data);
            return false;
        }

        private static byte[] MergeXmlParts(List<CarvedStream> parts)
        {
            System.Collections.Generic.List<byte[]> data = new System.Collections.Generic.List<byte[]>(parts.Count);
            for (int i = 0; i < parts.Count; i++) data.Add(parts[i].Data);
            return XmlCarver.Merge(data);
        }

        private static void FlushXmlEntry(List<FileEntry> entries, List<CarvedStream> parts)
        {
            if (parts == null || parts.Count == 0) return;
            byte[] merged = MergeXmlParts(parts);
            if (parts.Count == 1 || (merged.Length > 0 && XmlCarver.IsWellFormed(merged)))
            {
                FileEntry e = new FileEntry();
                e.Offset = parts[0].Offset;
                e.Size = merged.Length;
                e.Data = merged;
                e.Kind = DetectTextKind(SafeUtf8(merged));
                e.Preview = MakePreview(merged, "text");
                for (int i = 0; i < parts.Count; i++) e.SourceOffsets.Add(parts[i].Offset);
                entries.Add(e);
                return;
            }
            for (int i = 0; i < parts.Count; i++) entries.Add(MakeBinaryEntry(parts[i], entries.Count));
        }

        private static FileEntry MakeBinaryEntry(CarvedStream s, int idx)
        {
            FileEntry e = new FileEntry();
            e.Offset = s.Offset;
            e.Size = s.Data.Length;
            e.Data = s.Data;
            e.Preview = s.Preview;
            e.TextureSystemBytes = s.TextureSystemBytes;
            e.SourceOffsets.Add(s.Offset);
            ClassifyEntry(e, s);
            return e;
        }

        private static void ClassifyEntry(FileEntry e, CarvedStream s)
        {
            byte[] d = s.Data;
            if (string.Equals(s.Kind, "texture", StringComparison.OrdinalIgnoreCase) || LooksLikeTextureDictionary(d))
            {
                e.Kind = "texture";
                e.TextureSystemBytes = s.TextureSystemBytes;
                RageTextureDictionary dictionary = null;
                try
                {
                    if (e.TextureSystemBytes > 0 && e.TextureSystemBytes < d.Length)
                    {
                        byte[] system = new byte[e.TextureSystemBytes];
                        byte[] graphics = new byte[d.Length - e.TextureSystemBytes];
                        Buffer.BlockCopy(d, 0, system, 0, system.Length);
                        Buffer.BlockCopy(d, e.TextureSystemBytes, graphics, 0, graphics.Length);
                        dictionary = RageTextureTools.Inspect(system, graphics);
                    }
                }
                catch { dictionary = null; }
                e.TextureDict = dictionary;
                if (!string.IsNullOrEmpty(s.Preview) && string.Equals(s.Kind, "texture", StringComparison.OrdinalIgnoreCase))
                    e.Preview = s.Preview;
                else if (dictionary != null)
                    e.Preview = "Texture dictionary: " + dictionary.Textures.Count + " textures (system=" + dictionary.SystemBytes + ", graphics=" + dictionary.GraphicsBytes + " bytes)";
                else
                    e.Preview = "Texture dictionary (" + CountTextureDictTextures(d) + " textures, " + d.Length + " bytes)";
                return;
            }
            if (LooksLikeAwc(d))
            {
                e.Kind = "awc"; e.IsMedia = true; e.MediaKind = "audio";
                e.Awc = ParseAwc(d);
                e.Preview = "AWC " + e.Awc.Streams.Count + " streams" + (e.Awc.IsEncrypted ? " encrypted" : "");
                return;
            }
            if (LooksLikeBk2(d)) { e.Kind = "video"; e.IsMedia = true; e.MediaKind = "video"; e.Preview = "BK2 video"; return; }
            if (LooksLikeWav(d)) { e.Kind = "audio"; e.IsMedia = true; e.MediaKind = "audio"; e.Preview = "WAV audio"; return; }
            if (LooksLikeMp3(d)) { e.Kind = "audio"; e.IsMedia = true; e.MediaKind = "audio"; e.Preview = "MP3 audio"; return; }
            if (LooksLikeOgg(d)) { e.Kind = "audio"; e.IsMedia = true; e.MediaKind = "audio"; e.Preview = "OGG audio"; return; }
            if (LooksLikePng(d)) { e.Kind = "image"; e.Preview = "PNG image"; return; }
            if (LooksLikeJpg(d)) { e.Kind = "image"; e.Preview = "JPEG image"; return; }
            if (LooksLikeDds(d)) { e.Kind = "image"; e.Preview = "DDS image"; return; }
            if (LooksLikeYtd(d)) { e.Kind = "ytd"; e.Preview = "Texture dictionary (YTD)"; return; }
            if (LooksLikeRsc(d)) { e.Kind = "rsc"; e.Preview = DescribeRscResource(d); if (LooksLikeRscTextureDictionary(d)) e.Kind = "texture"; return; }
            if (LooksLikePso(d)) { e.Kind = "pso"; e.Preview = "PSO/Meta resource"; return; }

            if (s.Kind == "text" || LooksLikeText(d))
            {
                string text = SafeUtf8(d);
                string kind = DetectTextKind(text);
                e.Kind = kind;
                e.Preview = MakePreview(d, kind);
                return;
            }
            e.Kind = "binary";
            e.Preview = MakePreview(d, "binary");
        }

        private static bool LooksLikeText(byte[] d)
        {
            if (d == null || d.Length == 0) return false;
            int len = Math.Min(d.Length, 512);
            int printable = 0; int bad = 0;
            for (int i = 0; i < len; i++)
            {
                byte b = d[i];
                if (b == 0) { bad++; continue; }
                if ((b >= 32 && b <= 126) || b == 9 || b == 10 || b == 13) printable++;
            }
            return bad == 0 && len > 0 && ((double)printable / len) > 0.85;
        }

        private static string SafeUtf8(byte[] d)
        {
            if (d == null) return "";
            try { return Encoding.UTF8.GetString(d).Replace("\r\n", "\n").Trim('\uFEFF'); } catch { return ""; }
        }

        private static string DetectTextKind(string text)
        {
            if (string.IsNullOrEmpty(text)) return "text";
            string t = text.TrimStart();
            if (t.StartsWith("<?xml", StringComparison.OrdinalIgnoreCase) || t.StartsWith("<")) return LooksLikeMetaXml(t) ? "meta" : "xml";
            if (t.StartsWith("{") || t.StartsWith("[")) return "json";
            if (t.IndexOf(",") >= 0 && t.IndexOf("\n") >= 0 && t.IndexOf("=") < 0) return "csv";
            return "text";
        }

        private static bool LooksLikeMetaXml(string t)
        {
            if (string.IsNullOrEmpty(t)) return false;
            string s = t.Length > 400 ? t.Substring(0, 400) : t;
            return s.IndexOf("<C", StringComparison.OrdinalIgnoreCase) >= 0 || s.IndexOf("Meta", StringComparison.OrdinalIgnoreCase) >= 0 || s.IndexOf("<Item", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static bool LooksLikeYtd(byte[] d)
        {
            return d != null && d.Length >= 4 && ((d[0] == 0x59 && d[1] == 0x54 && d[2] == 0x44) || (d[0] == 0x44 && d[1] == 0x54 && d[2] == 0x59));
        }

                                public static string DescribeRscResource(byte[] d)
        {
            if (d == null || d.Length < 16) return "RSC resource";
            uint magic = BitConverter.ToUInt32(d, 0);
            string tag = Encoding.ASCII.GetString(d, 0, 4);
            int sysPages = (int)(magic & 0x7FF);
            int gfxPages = (int)((magic >> 11) & 0x7FF);
            int ver = (int)((magic >> 28) & 0xF);
            string v = (tag == "RSC7" || tag == "RSC8" || tag == "RSC\u0005" || tag.StartsWith("RSC")) ? tag : "RSC?";
            if (LooksLikeRscTextureDictionary(d)) return v + " texture dictionary/resource (sysPages=" + sysPages + " gfxPages=" + gfxPages + " ver=" + ver + ", " + d.Length + " bytes)";
            return v + " resource (sysPages=" + sysPages + " gfxPages=" + gfxPages + " ver=" + ver + ", " + d.Length + " bytes)";
        }

        public static bool LooksLikeRscTextureDictionary(byte[] d)
        {
            if (!LooksLikeRsc(d)) return false;
            try
            {
                string ascii = Encoding.ASCII.GetString(d, 0, Math.Min(d.Length, 4096));
                if (ascii.IndexOf("Texture", StringComparison.OrdinalIgnoreCase) >= 0) return true;
                if (ascii.IndexOf("txd", StringComparison.OrdinalIgnoreCase) >= 0) return true;
                for (int i = 0; i < Math.Min(d.Length - 4, 65536); i++)
                {
                    if (d[i] == 'D' && d[i+1] == 'D' && d[i+2] == 'S' && d[i+3] == ' ') return true;
                }
            } catch { }
            return false;
        }
public static bool LooksLikeRawImage(byte[] d, out string info)
        {
            info = "";
            if (d == null) return false;
            int len = d.Length;
            int[] sizes = new int[] { 4096,2048,1536,1024,768,512,384,256,128,64 };
            foreach (int w in sizes)
            foreach (int h in sizes)
            {
                int px = w * h;
                if (len == px * 4) { info = w + "x" + h + " RGBA8/BGRA8"; return true; }
                if (len == px) { info = w + "x" + h + " GRAY8"; return true; }
                if (w % 4 == 0 && h % 4 == 0 && len == (w / 4) * (h / 4) * 8) { info = w + "x" + h + " DXT1/BC1"; return true; }
                if (w % 4 == 0 && h % 4 == 0 && len == (w / 4) * (h / 4) * 16) { info = w + "x" + h + " DXT5/BC3"; return true; }
            }
            if (len >= 65536 && len <= 64 * 1024 * 1024)
            {
                int sq = (int)Math.Sqrt(len / 4);
                if (sq * sq * 4 == len && sq >= 64 && sq <= 4096) { info = sq + "x" + sq + " raw32"; return true; }
            }
            return false;
        }
public static bool LooksLikeRsc(byte[] d)
        {
            return d != null && d.Length >= 4 && d[0] == 0x52 && d[1] == 0x53 && d[2] == 0x43;
        }

        private static bool LooksLikePso(byte[] d)
        {
            return d != null && d.Length >= 4 && d[0] == 0x50 && d[1] == 0x53 && d[2] == 0x4F;
        }
        private static ushort U16(byte[] d, int o) { return (ushort)(d[o] | (d[o+1] << 8)); }
        private static uint U32le(byte[] d, int o) { return (uint)(d[o] | (d[o+1]<<8) | (d[o+2]<<16) | (d[o+3]<<24)); }
        private static ulong U64le(byte[] d, int o) { return BitConverter.ToUInt64(d, o); }
        private static bool IsSystemPtr(ulong v) { return v != 0 && ((v >> 28) & 0xF) == 0x5; }
        private static int VOff(ulong ptr) { return (int)(ptr & 0x0FFFFFFF); }
        public static bool LooksLikeTextureDictionary(byte[] sys)
        {
            try
            {
                if (sys == null || sys.Length < 64) return false;
                int count = U16(sys, 0x38);
                if (count <= 0 || count > 4096) return false;
                if (U16(sys, 0x3A) < count) return false;
                if (U16(sys, 0x28) != count) return false;
                if (!IsSystemPtr(U64le(sys, 0x20))) return false;
                if (!IsSystemPtr(U64le(sys, 0x30))) return false;
                return true;
            } catch { return false; }
        }
        public static bool TryGetTextureDictPageCounts(byte[] sys, out int systemPages, out int graphicsPages)
        {
            systemPages = 0; graphicsPages = 0;
            try
            {
                if (!LooksLikeTextureDictionary(sys)) return false;
                int pageInfo = VOff(U64le(sys, 8));
                if (pageInfo < 0 || pageInfo + 16 > sys.Length) return false;
                systemPages = sys[pageInfo + 8];
                graphicsPages = sys[pageInfo + 9];
                return systemPages >= 1 && systemPages <= 128 && graphicsPages >= 0 && graphicsPages <= 4096;
            }
            catch { return false; }
        }
        public static bool TryGetTextureSections(FileEntry entry, out byte[] system, out byte[] graphics)
        {
            system = null;
            graphics = null;
            if (entry == null || entry.Data == null) return false;
            int systemBytes = entry.TextureSystemBytes;
            if (systemBytes <= 0 || systemBytes >= entry.Data.Length) return false;
            system = new byte[systemBytes];
            graphics = new byte[entry.Data.Length - systemBytes];
            Buffer.BlockCopy(entry.Data, 0, system, 0, system.Length);
            Buffer.BlockCopy(entry.Data, systemBytes, graphics, 0, graphics.Length);
            return true;
        }
        public static bool LooksLikeAwc(byte[] d)
        {
            if (d == null || d.Length < 16) return false;
            uint m = BitConverter.ToUInt32(d, 0);
            return m == 0x54414441u || m == 0x41444154u;
        }

        private static long ComputeAwcEnd(byte[] d, long available)
        {
            if (d == null || d.Length < 16) return 0;
            uint magic = BitConverter.ToUInt32(d, 0);
            bool be;
            if (magic == 0x54414441u) be = false;
            else if (magic == 0x41444154u) be = true;
            else return 0;
            uint flags = ReadU32(d, 4, be);
            int streamCount = unchecked((int)ReadU32(d, 8, be));
            int headerLength = unchecked((int)ReadU32(d, 12, be));
            if (streamCount <= 0 || streamCount > 100000 || headerLength < 16) return 0;
            bool rpf9 = IsRpf9AudioAwc(flags) || ((flags >> 16) & 0xFF) >= 0x21;
            if (rpf9)
            {
                if ((flags & 0xFF00FFFFu) != 0xFF000001u) return 0;
                if ((flags & 0x00980000u) != 0) return 0;
            }
            int pos = 16;
            if (rpf9)
            {
                if ((flags & 0x00400000u) != 0) pos += 4;
                if ((flags & 0x00010000u) != 0) pos += streamCount * 2;
                if ((flags & 0x00200000u) != 0) pos += (streamCount + 1) * 4;
            }
            else if ((flags & 1u) != 0) pos += streamCount * 2;
            if (pos < 0 || pos + (long)streamCount * 4 > d.Length) return 0;
            long tagTable = pos + (long)streamCount * 4;
            long tagCount = 0;
            for (int i = 0; i < streamCount; i++)
            {
                uint info = ReadU32(d, pos + i * 4, be);
                tagCount += (info >> 29) & 7u;
            }
            if (tagTable + tagCount * 8 > d.Length) return 0;
            long end = headerLength;
            long p = tagTable;
            for (long i = 0; i < tagCount; i++)
            {
                ulong raw = ReadU64(d, (int)p, be); p += 8;
                uint size = (uint)((raw >> 28) & 0x0FFFFFFFu);
                uint offset = (uint)(raw & 0x0FFFFFFFu);
                if (size == 0) continue;
                long chunkEnd = (long)offset + size;
                if (chunkEnd > end) end = chunkEnd;
            }
            if (end > available) end = available;
            if (end < headerLength) end = headerLength;
            return end;
        }
        private static bool LooksLikeBk2(byte[] d) { return d != null && d.Length >= 16 && d[0] == 'K' && d[1] == 'B' && d[2] == '2'; }
        private static bool LooksLikeWav(byte[] d) { return d != null && d.Length >= 12 && d[0]=='R'&&d[1]=='I'&&d[2]=='F'&&d[3]=='F'&&d[8]=='W'&&d[9]=='A'&&d[10]=='V'&&d[11]=='E'; }
        private static bool LooksLikeMp3(byte[] d) { return d != null && d.Length >= 3 && ((d[0]==0xFF && (d[1]&0xE0)==0xE0) || (d[0]=='I'&&d[1]=='D'&&d[2]=='3')); }
        private static bool LooksLikeOgg(byte[] d) { return d != null && d.Length >= 4 && d[0]=='O'&&d[1]=='g'&&d[2]=='g'&&d[3]=='S'; }
        public static bool LooksLikePng(byte[] d) { return d != null && d.Length >= 8 && d[0]==0x89&&d[1]==0x50&&d[2]==0x4E&&d[3]==0x47; }
        public static bool LooksLikeJpg(byte[] d) { return d != null && d.Length >= 3 && d[0]==0xFF&&d[1]==0xD8&&d[2]==0xFF; }
        public static bool LooksLikeDds(byte[] d) { return d != null && d.Length >= 4 && d[0]=='D'&&d[1]=='D'&&d[2]=='S'&&d[3]==' '; }
        private static bool LooksLikeXmlBytes(byte[] d)
        {
            if (d == null || d.Length < 5) return false;
            string s = Encoding.UTF8.GetString(d, 0, Math.Min(256, d.Length));
            return s.IndexOf("<?xml", StringComparison.OrdinalIgnoreCase) >= 0 || s.IndexOf("<", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        public static string SafeFileName(string name)
        {
            if (string.IsNullOrEmpty(name)) return "file.bin";
            string n = name.Replace('\\', '/');
            char[] bad = Path.GetInvalidFileNameChars();
            StringBuilder sb = new StringBuilder(n.Length);
            for (int i = 0; i < n.Length; i++)
            {
                char c = n[i];
                bool ok = c == '/';
                if (!ok)
                {
                    ok = true;
                    for (int j = 0; j < bad.Length; j++) if (bad[j] == c) { ok = false; break; }
                }
                sb.Append(ok ? c : '_');
            }
            string s = sb.ToString();
            if (s.Length == 0) return "file.bin";
            return s;
        }

        public static string SafeBaseName(string name)
        {
            if (string.IsNullOrEmpty(name)) return "file";
            StringBuilder sb = new StringBuilder(name.Length);
            for (int i = 0; i < name.Length; i++)
            {
                char c = name[i];
                if ((c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c == '_' || c == '-' || c == '.') sb.Append(c);
                else sb.Append('_');
            }
            string s = sb.ToString().Trim('_');
            if (s.Length == 0) s = "file";
            return s;
        }

        public static byte[] ExportBytes(FileEntry e)
        {
            return ExportBytes(e, -1);
        }

        public static byte[] ExportBytes(FileEntry e, int textureIndex)
        {
            if (e == null) return new byte[0];
            if (e.Ttpf != null && e.TtpfSource != null)
            {
                string error;
                byte[] png = e.TtpfSource.RenderPng(e.Ttpf, out error);
                if (png == null) AppLog.Line("TTPF export failed: " + error);
                return png;
            }
            if (e.Kind == "texture" && e.TextureDict != null && e.TextureDict.Textures != null && e.TextureDict.Textures.Count > 0)
                return TexturePng(e, textureIndex);
            if (LooksLikeDds(e.Data)) return DdsToPng(e.Data);
            return e.Data ?? new byte[0];
        }

        private static byte[] DdsToPng(byte[] dds)
        {
            try
            {
                string info;
                using (Bitmap bmp = DdsDecoder.DecodeToBitmap(dds, out info))
                using (MemoryStream ms = new MemoryStream())
                {
                    bmp.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
                    return ms.ToArray();
                }
            }
            catch (Exception ex)
            {
                AppLog.Line("DDS export failed: " + ex.Message);
                return dds;
            }
        }

        public static string PngSize(byte[] png)
        {
            if (!LooksLikePng(png) || png.Length < 24) return "";
            long w = ((long)png[16] << 24) | ((long)png[17] << 16) | ((long)png[18] << 8) | png[19];
            long h = ((long)png[20] << 24) | ((long)png[21] << 16) | ((long)png[22] << 8) | png[23];
            if (w <= 0 || h <= 0) return "";
            return w + "x" + h;
        }

        private static byte[] TexturePng(FileEntry e, int textureIndex)
        {
            byte[] system, graphics;
            if (!TryGetTextureSections(e, out system, out graphics)) return null;
            List<RageTextureInfo> textures = e.TextureDict.Textures;
            int index = textureIndex;
            if (index < 0 || index >= textures.Count)
            {
                index = 0;
                long best = -1;
                for (int i = 0; i < textures.Count; i++)
                {
                    long pixels = (long)textures[i].Width * textures[i].Height;
                    if (pixels > best) { best = pixels; index = i; }
                }
            }
            try
            {
                byte[] dds = RageTextureTools.CreateDdsMip(system, graphics, index, 0);
                string info;
                using (Bitmap bmp = DdsDecoder.DecodeToBitmap(dds, out info))
                using (MemoryStream ms = new MemoryStream())
                {
                    bmp.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
                    return ms.ToArray();
                }
            }
            catch
            {
                return null;
            }
        }

        public static string GetMediaExtension(FileEntry e)
        {
            if (e == null || e.Data == null) return ".bin";
            if (e.Kind == "video" || LooksLikeBk2(e.Data)) return ".bk2";
            if (LooksLikeWav(e.Data)) return ".wav";
            if (LooksLikeMp3(e.Data)) return ".mp3";
            if (LooksLikeOgg(e.Data)) return ".ogg";
            if (e.Kind == "awc") return ".awc";
            return ".bin";
        }

        private static string DetectKind(byte[] data)
        {
            if (data == null || data.Length == 0) return "binary";
            if (data.Length >= 3 && data[0] == 0xEF && data[1] == 0xBB && data[2] == 0xBF) return "text";
            int len = Math.Min(data.Length, 256);
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
            if (data == null) return "";
            if (kind == "text" || kind == "xml" || kind == "meta" || kind == "ps" || kind == "json" || kind == "csv")
            {
                string text = Encoding.UTF8.GetString(data);
                return text.Replace("\r\n", "\n").Trim('\uFEFF');
            }
            int n = Math.Min(data.Length, 16);
            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < n; i++) { if (i > 0) sb.Append(' '); sb.Append(data[i].ToString("X2")); }
            return sb.ToString();
        }

        public static string MakeListPreview(byte[] data, string kind)
        {
            if (data == null) return "";
            if (kind == "text" || kind == "xml" || kind == "meta" || kind == "ps" || kind == "json" || kind == "csv")
            {
                string text = Encoding.UTF8.GetString(data);
                text = text.Replace("\r", " ").Replace("\n", " ").Replace("\t", " ").Trim();
                if (text.Length > 140) text = text.Substring(0, 140) + "...";
                return text;
            }
            int n = Math.Min(data.Length, 16);
            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < n; i++) { if (i > 0) sb.Append(' '); sb.Append(data[i].ToString("X2")); }
            return sb.ToString();
        }

        public static AwcFile ParseAwc(byte[] data)
        {
            AwcFile awc = new AwcFile();
            if (data == null || data.Length < 16) return awc;
            uint magic = BitConverter.ToUInt32(data, 0);
            bool be;
            if (magic == 0x54414441u) be = false;
            else if (magic == 0x41444154u) be = true;
            else return awc;
            awc.IsBigEndian = be;
            uint flags = ReadU32(data, 4, be);
            int streamCount = unchecked((int)ReadU32(data, 8, be));
            int dataOffset = unchecked((int)ReadU32(data, 12, be));
            awc.StreamCount = streamCount;
            if (streamCount <= 0 || streamCount > 4096) return awc;
            if (dataOffset < 16 || dataOffset > data.Length) dataOffset = -1;

            awc.IsEncrypted = (flags & 2u) != 0 || (flags & 8u) != 0;
            awc.IsMusic = (flags & 4u) != 0;


            int ver = (int)((flags >> 16) & 0xFF);
            bool rpf9Audio = IsRpf9AudioAwc(flags);
            bool legacyFlags = (flags & 0xFF00FFFFu) == 0xFF000001u;
            bool knownFlagBits = (flags & 0x00F00000u) == 0;
            if (!rpf9Audio && legacyFlags && ver < 0x21 && knownFlagBits)
            {
                bool gtaParsed = false;
                try
                {
                    ParseAwcGta(data, awc, be, flags, streamCount, dataOffset);
                    SanitizeAwcStreams(awc, flags);
                    if (awc.Streams.Count > 0) gtaParsed = true;
                }
                catch (Exception ex) { AppLog.Line("ParseAwcGta failed: " + ex.Message); }
                if (gtaParsed)
                {
                    bool hasPlayable = false;
                    for (int si = 0; si < awc.Streams.Count; si++) { if (awc.Streams[si] != null && awc.Streams[si].Codec != -1) { hasPlayable = true; break; } }
                    if (hasPlayable) return awc;
                    awc.Streams.Clear();
                }
            }
            if (rpf9Audio || ver >= 0x21)
            {
                try
                {
                    ParseAwcRpf9(data, awc, be, flags, streamCount, dataOffset);
                    bool hasPlayable = false;
                    for (int si = 0; si < awc.Streams.Count; si++) { if (awc.Streams[si] != null && awc.Streams[si].Codec != -1) { hasPlayable = true; break; } }
                    if (awc.Streams.Count > 0 && hasPlayable) return awc;
                    if (awc.Streams.Count > 0) awc.Streams.Clear();
                }
                catch (Exception ex) { AppLog.Line("ParseAwcRpf9 failed: " + ex.Message); }
            }

            TryAwcFallbackForUnknown(data, awc, flags, dataOffset);
            return awc;
        }

        private static void TryAwcFallbackForUnknown(byte[] data, AwcFile awc, uint flags, int dataOffset)
        {
            awc.Streams.Clear();
            ScanEmbeddedMedia(data, awc);
            if (awc.Streams.Count > 0) return;
            if (dataOffset > 0 && dataOffset < data.Length)
            {
                int sz = data.Length - dataOffset;
                AwcStreamInfo si = new AwcStreamInfo();
                si.Index = 0; si.StreamId = 0; si.Codec = -1; si.CodecName = "AWC-DATA";
                si.Channels = 0; si.SampleRate = 0; si.Samples = 0;
                si.Offset = (uint)dataOffset; si.Size = (uint)sz;
                si.Data = new byte[sz]; Array.Copy(data, dataOffset, si.Data, 0, sz);
                si.DecodeError = "AWC layout not recognized; raw export available";
                awc.Streams.Add(si);
                return;
            }
            AwcStreamInfo raw = new AwcStreamInfo();
            raw.Index = 0; raw.StreamId = 0; raw.Codec = -1; raw.CodecName = "AWC-RAW";
            raw.Offset = 0; raw.Size = (uint)data.Length; raw.Data = data;
            raw.DecodeError = "AWC layout not recognized; raw export available";
            awc.Streams.Add(raw);
        }

        private static void SanitizeAwcStreams(AwcFile awc, uint flags)
        {
            if (awc == null || awc.Streams == null) return;
            bool legacyFlags = (flags & 0xFF00FFFFu) != 0xFF000001u;
            bool unknownVersion = ((flags >> 16) & 0xFF) >= 0x21;
            if (!legacyFlags && !unknownVersion) return;
            for (int i = awc.Streams.Count - 1; i >= 0; i--)
            {
                AwcStreamInfo s = awc.Streams[i];
                if (s == null) { awc.Streams.RemoveAt(i); continue; }
                bool codecKnown = s.Codec == 0 || s.Codec == 1 || s.Codec == 4 || s.Codec == 5 || s.Codec == 7 || s.Codec == 8 || s.Codec == 0x0B || s.Codec == 0x0C || s.Codec == 0x0D || s.Codec == 0x0F || s.Codec == 0x10 || s.Codec == 0x11 || s.Codec == -1;
                bool rateOk = s.SampleRate == 8000 || s.SampleRate == 11025 || s.SampleRate == 12000 || s.SampleRate == 16000 || s.SampleRate == 22050 || s.SampleRate == 24000 || s.SampleRate == 32000 || s.SampleRate == 44100 || s.SampleRate == 48000;
                if (!codecKnown || !rateOk || s.Channels <= 0 || s.Channels > 8) { awc.Streams.RemoveAt(i); continue; }
            }
        }

        private sealed class AwcChunkRef { public byte Type; public uint Size; public uint Offset; }

        private static AwcChunkRef FindChunk(List<AwcChunkRef> list, byte type)
        {
            if (list == null) return null;
            for (int i = 0; i < list.Count; i++) if (list[i].Type == type) return list[i];
            return null;
        }

        private static uint ReadU32(byte[] d, int o, bool be)
        {
            if (o < 0 || o + 4 > d.Length) return 0;
            if (!be) return BitConverter.ToUInt32(d, o);
            return ((uint)d[o] << 24) | ((uint)d[o + 1] << 16) | ((uint)d[o + 2] << 8) | d[o + 3];
        }
        private static ushort ReadU16(byte[] d, int o, bool be)
        {
            if (o < 0 || o + 2 > d.Length) return 0;
            if (!be) return BitConverter.ToUInt16(d, o);
            return (ushort)(((uint)d[o] << 8) | d[o + 1]);
        }
        private static ulong ReadU64(byte[] d, int o, bool be)
        {
            if (o < 0 || o + 8 > d.Length) return 0;
            if (!be) return BitConverter.ToUInt64(d, o);
            uint hi = ReadU32(d, o, true);
            uint lo = ReadU32(d, o + 4, true);
            return ((ulong)hi << 32) | lo;
        }

        private static void ParseAwcRpf9(byte[] data, AwcFile awc, bool be, uint flags, int streamCount, int dataOffset)
        {
            int pos = 16;
            if ((flags & 0x00400000u) != 0) pos += 4;
            if ((flags & 0x00010000u) != 0) pos += streamCount * 2;
            if ((flags & 0x00200000u) != 0) pos += (streamCount + 1) * 4;
            if (pos + streamCount * 4 > data.Length) return;
            uint[] infos = new uint[streamCount];
            for (int i = 0; i < streamCount; i++) { infos[i] = ReadU32(data, pos, be); pos += 4; }
            List<AwcChunkRef>[] per = new List<AwcChunkRef>[streamCount];
            for (int i = 0; i < streamCount; i++)
            {
                int tagCount = (int)((infos[i] >> 29) & 0x7u);
                List<AwcChunkRef> tags = new System.Collections.Generic.List<AwcChunkRef>();
                for (int t = 0; t < tagCount; t++)
                {
                    if (pos + 8 > data.Length) break;
                    ulong raw = ReadU64(data, pos, be); pos += 8;
                    AwcChunkRef r = new AwcChunkRef();
                    r.Type = (byte)((raw >> 56) & 0xFFu);
                    r.Size = (uint)((raw >> 28) & 0x0FFFFFFFu);
                    r.Offset = (uint)(raw & 0x0FFFFFFFu);
                    tags.Add(r);
                }
                per[i] = tags;
            }
            for (int i = 0; i < streamCount; i++)
            {
                uint streamId = infos[i] & 0x1FFFFFFFu;
                List<AwcChunkRef> tags = per[i];
                AwcChunkRef fmt = FindChunk(tags, 0xFA); if (fmt == null) fmt = FindChunk(tags, 0x76);
                AwcChunkRef fmt48 = FindChunk(tags, 0x48);
                AwcChunkRef dataTag = FindChunk(tags, 0x55); if (dataTag == null) dataTag = FindChunk(tags, 0x44);
                if (fmt48 != null)
                {
                    BuildRpf9Multi(data, awc, be, flags, streamId, fmt48, dataTag, tags);
                    continue;
                }
                AwcStreamInfo si = new AwcStreamInfo();
                si.Index = awc.Streams.Count; si.StreamId = streamId; si.Channels = 1;
                bool gta6 = IsGta6Awc(flags);
                if (fmt != null && fmt.Offset + 20 <= (uint)data.Length)
                {
                    int fo = (int)fmt.Offset;
                    si.Samples = ReadU32(data, fo + 0, be);
                    si.SampleRate = (int)ReadU16(data, fo + 8, be);
                    int codec = 0;
                    if (gta6 && fmt.Size >= 32 && fo + 0x20 <= data.Length)
                    {
                        codec = data[fo + 0x1C];
                        int ch = data[fo + 0x1D]; if (ch > 0 && ch <= 8) si.Channels = ch;
                    }
                    else if (fo + 0x14 <= data.Length) codec = data[fo + 0x13];
                    si.Codec = codec;
                }
                if (dataTag != null && dataTag.Offset < (uint)data.Length)
                {
                    uint sz = dataTag.Size; if (sz == 0 || dataTag.Offset + sz > (uint)data.Length) sz = (uint)(data.Length - dataTag.Offset);
                    si.Offset = dataTag.Offset; si.Size = sz; si.Data = new byte[sz]; Array.Copy(data, dataTag.Offset, si.Data, 0, sz);
                }
                else si.Data = new byte[0];
                si.CodecName = AwcCodecName(si.Codec, gta6);
                if (gta6) si.BlockSize = 0x80000;
                if (gta6 && si.Codec == 11 && si.Data.Length > 0)
                {
                    List<byte[]> pkts = new System.Collections.Generic.List<byte[]>();
                    bool packetsOk = CollectRpf9Simple2KPackets(si.Data, pkts);
                    if (packetsOk && pkts.Count > 0)
                    {
                        si.Data = BuildOggOpus(pkts, si.SampleRate > 0 ? si.SampleRate : 48000, Math.Max(1, si.Channels), 312);
                        si.Size = (uint)si.Data.Length;
                        si.CodecName = "OPUS";
                    }
                    else
                    {
                        si.DecodeError = "No valid Opus packets found; raw export available";
                        si.CodecName = "OPUS (unparsed)";
                    }
                }
                if (si.Data.Length > 0) awc.Streams.Add(si);
            }
        }

        private static byte[] DeblockRpf9Simple2K(byte[] data)
        {
            if (data == null || data.Length < 4) return data;
            bool looksBlocked = true;
            int max = Math.Min(data.Length, 0x800 * 8);
            for (int off = 0; off + 2 <= max; off += 0x800)
            {
                ushort len = BitConverter.ToUInt16(data, off);
                if (len == 0 || len > 0x7FE) { looksBlocked = false; break; }
            }
            if (!looksBlocked) return data;
            List<byte> outb = new System.Collections.Generic.List<byte>();
            for (int off = 0; off + 2 <= data.Length; off += 0x800)
            {
                int subEnd = Math.Min(data.Length, off + 0x800); int pos = off;
                while (pos + 2 <= subEnd)
                {
                    ushort len = BitConverter.ToUInt16(data, pos); pos += 2;
                    if (len == 0) break;
                    if (len > subEnd - pos) break;
                    for (int k = 0; k < len; k++) outb.Add(data[pos + k]);
                    pos += len;
                    if (pos < subEnd && data[pos] == 0) break;
                }
            }
            return outb.Count > 0 ? outb.ToArray() : data;
        }

        private static byte[] DeblockRpf9Data(byte[] data, int blockSizeHint)
        {
            if (data == null || data.Length < 0x1000) return data;
            int bs = blockSizeHint > 0 ? blockSizeHint : 0x80000;
            if (data.Length < bs / 2) bs = 0;
            if (bs == 0x80000)
            {
                int hs = DetectRpf9BlockHeaderSize(data, bs);
                List<byte> outb = new System.Collections.Generic.List<byte>();
                int total = (data.Length + bs - 1) / bs;
                for (int b = 0; b < total; b++)
                {
                    int bo = b * bs; if (bo >= data.Length) break;
                    int blen = Math.Min(bs, data.Length - bo);
                    int pos = bo + Math.Min(hs, blen); int end = bo + blen; int sub = 0x800;
                    while (pos + 2 <= end)
                    {
                        int subEnd = Math.Min(end, pos + sub);
                        while (pos + 2 <= subEnd)
                        {
                            ushort len = BitConverter.ToUInt16(data, pos); pos += 2;
                            if (len == 0) { pos = subEnd; break; }
                            if (len > 1500 || pos + len > subEnd) { pos = subEnd; break; }
                            for (int k = 0; k < len; k++) outb.Add(data[pos + k]);
                            pos += len;
                            if (pos < subEnd && data[pos] == 0) { pos = subEnd; break; }
                        }
                        if (pos < subEnd) pos = subEnd;
                    }
                }
                if (outb.Count > 0) return outb.ToArray();
            }
            return data;
        }

        private static int DetectRpf9BlockHeaderSize(byte[] data, int blockSize)
        {
            int[] candidates = new int[] { 0x800, 0x1000, 0x2000, 0x4000 };
            int best = 0x800; int bestScore = -1;
            for (int ci = 0; ci < candidates.Length; ci++)
            {
                int hs = candidates[ci]; if (hs >= blockSize) continue; int score = 0;
                int blocks = Math.Min(2, (data.Length + blockSize - 1) / blockSize);
                for (int b = 0; b < blocks; b++)
                {
                    int bo = b * blockSize; int blen = Math.Min(blockSize, data.Length - bo);
                    int pos = bo + hs; int end = bo + blen; int sub = 0x800;
                    while (pos + 2 <= end)
                    {
                        int subEnd = Math.Min(end, pos + sub);
                        while (pos + 2 <= subEnd)
                        {
                            ushort len = BitConverter.ToUInt16(data, pos); pos += 2;
                            if (len == 0) { pos = subEnd; break; }
                            if (len > 1500 || pos + len > subEnd) { pos = subEnd; break; }
                            score++; pos += len; if (pos < subEnd && data[pos] == 0) { pos = subEnd; break; }
                        }
                        if (pos < subEnd) pos = subEnd;
                    }
                }
                if (score > bestScore || (score == bestScore && hs > best)) { bestScore = score; best = hs; }
            }
            return best;
        }

        private static byte[] BuildOggOpus(List<byte[]> packets, int sampleRate, int channels, int preSkip)
        {
            if (packets == null) packets = new System.Collections.Generic.List<byte[]>();
            if (sampleRate <= 0) sampleRate = 48000; if (channels <= 0) channels = 1;
            MemoryStream ms = new MemoryStream();
            uint serial = 1; uint seq = 0;
            byte[] head = BuildOpusHead(channels, (ushort)Math.Max(0, preSkip), (uint)sampleRate);
            WriteOggPage(ms, head, 0, serial, seq++, 0x02);
            byte[] tags = BuildOpusTags();
            WriteOggPage(ms, tags, 0, serial, seq++, 0x00);
            long granule = preSkip;
            for (int i = 0; i < packets.Count; i++)
            {
                byte[] pkt = packets[i]; if (pkt == null || pkt.Length == 0) continue;
                granule += OpusPacketSamples(pkt, sampleRate);
                byte flags = (i == packets.Count - 1) ? (byte)0x04 : (byte)0x00;
                WriteOggPacket(ms, pkt, (ulong)granule, serial, ref seq, flags);
            }
            if (packets.Count == 0) WriteOggPage(ms, new byte[0], (ulong)granule, serial, seq++, 0x04);
            return ms.ToArray();
        }

        private static byte[] BuildOpusHead(int channels, ushort preSkip, uint inputRate)
        {
            byte[] b = new byte[19];
            Encoding.ASCII.GetBytes("OpusHead").CopyTo(b, 0);
            b[8] = 1; b[9] = (byte)channels; BitConverter.GetBytes(preSkip).CopyTo(b, 10); BitConverter.GetBytes(inputRate).CopyTo(b, 12);
            BitConverter.GetBytes((short)0).CopyTo(b, 16); b[18] = 0; return b;
        }

        private static byte[] BuildOpusTags()
        {
            byte[] vendor = Encoding.UTF8.GetBytes("OpenRPF9");
            using (MemoryStream ms = new MemoryStream())
            {
                byte[] h = Encoding.ASCII.GetBytes("OpusTags"); ms.Write(h, 0, h.Length);
                ms.Write(BitConverter.GetBytes(vendor.Length), 0, 4); ms.Write(vendor, 0, vendor.Length);
                ms.Write(BitConverter.GetBytes(0), 0, 4);
                return ms.ToArray();
            }
        }

        private static int OpusPacketSamples(byte[] pkt, int sampleRate)
        {
            if (pkt == null || pkt.Length == 0) return 960;
            int toc = pkt[0]; int config = toc >> 3;
            int frameCount = 0; int c = toc & 3;
            if (c == 0) frameCount = 1; else if (c == 1 || c == 2) frameCount = 2; else frameCount = pkt.Length > 1 ? (pkt[1] & 0x3F) : 1;
            if (frameCount <= 0) frameCount = 1;
            int samplesPerFrame = 960;
            if (config < 12) { int[] s = new int[] { 480, 960, 1920, 2880 }; samplesPerFrame = s[config & 3]; }
            else if (config < 16) { samplesPerFrame = (config & 1) == 0 ? 480 : 960; }
            else { int[] s = new int[] { 120, 240, 480, 960 }; samplesPerFrame = s[config & 3]; }
            if (sampleRate != 48000 && sampleRate > 0) return (int)((long)frameCount * samplesPerFrame * sampleRate / 48000);
            return frameCount * samplesPerFrame;
        }

        private static void WriteOggPacket(MemoryStream ms, byte[] pkt, ulong granule, uint serial, ref uint seq, byte flags)
        {
            if (pkt == null) pkt = new byte[0];
            int off = 0; bool first = true;
            while (off < pkt.Length || first)
            {
                int chunk = Math.Min(255 * 255, pkt.Length - off);
                if (chunk < 0) chunk = 0;
                byte[] segData = new byte[chunk]; if (chunk > 0) Array.Copy(pkt, off, segData, 0, chunk); off += chunk;
                List<byte> segs = new System.Collections.Generic.List<byte>();
                int rem = chunk;
                while (rem >= 255) { segs.Add(255); rem -= 255; }
                segs.Add((byte)rem);
                bool packetEnd = off >= pkt.Length;
                if (chunk > 0 && chunk % 255 == 0 && packetEnd) segs.Add(0);
                if (chunk == 0) { segs.Clear(); segs.Add(0); }
                byte hf = 0; if (!first) hf |= 0x01; if (packetEnd) hf |= flags;
                WriteOggPage(ms, segData, (packetEnd ? granule : 0xFFFFFFFFFFFFFFFFul), serial, seq++, hf, segs.ToArray());
                first = false; if (pkt.Length == 0) break;
            }
        }
        private static void WriteOggPage(MemoryStream ms, byte[] data, ulong granule, uint serial, uint seq, byte headerType, byte[] segments = null)
        {
            if (segments == null)
            {
                List<byte> segs = new System.Collections.Generic.List<byte>(); int rem = data.Length;
                while (rem >= 255) { segs.Add(255); rem -= 255; } if (data.Length == 0 || rem > 0 || data.Length % 255 == 0) segs.Add((byte)rem);
                segments = segs.ToArray();
            }
            byte[] page = new byte[27 + segments.Length + data.Length];
            Encoding.ASCII.GetBytes("OggS").CopyTo(page, 0); page[4] = 0; page[5] = headerType;
            BitConverter.GetBytes(granule).CopyTo(page, 6); BitConverter.GetBytes(serial).CopyTo(page, 14); BitConverter.GetBytes(seq).CopyTo(page, 18);
            BitConverter.GetBytes(0u).CopyTo(page, 22); page[26] = (byte)segments.Length;
            Array.Copy(segments, 0, page, 27, segments.Length); Array.Copy(data, 0, page, 27 + segments.Length, data.Length);
            uint crc = OggCrc(page); BitConverter.GetBytes(crc).CopyTo(page, 22);
            ms.Write(page, 0, page.Length);
        }

        private static uint OggCrc(byte[] page)
        {
            uint crc = 0;
            for (int i = 0; i < page.Length; i++)
            {
                crc ^= (uint)page[i] << 24;
                for (int b = 0; b < 8; b++) crc = (crc & 0x80000000u) != 0 ? (crc << 1) ^ 0x04C11DB7u : (crc << 1);
            }
            return crc;
        }

        private static void ExtractRpf9OpusMulti(byte[] data, AwcFile awc, AwcChunkRef dat, List<AwcStreamInfo> chans, int blockCount, int blockSize)
        {
            if (dat == null || dat.Offset >= (uint)data.Length) { for (int i = 0; i < chans.Count; i++) awc.Streams.Add(chans[i]); return; }
            int bs = blockSize > 0 ? blockSize : 0x80000;
            int totalBlocks = blockCount > 0 ? blockCount : (int)(((long)data.Length - dat.Offset + bs - 1) / bs);
            int datLen = (int)Math.Min((long)data.Length - dat.Offset, (long)totalBlocks * bs);
            byte[] blob = new byte[Math.Max(0, datLen)];
            if (datLen > 0) Array.Copy(data, (int)dat.Offset, blob, 0, datLen);
            int? nSub = ProbeRpf9SuperblockStreams(blob);
            for (int c = 0; c < chans.Count; c++)
            {
                List<byte[]> packets = new System.Collections.Generic.List<byte[]>();
                bool packetsOk;
                if (nSub.HasValue)
                    packetsOk = CollectRpf9SuperblockPackets(blob, nSub.Value, c, packets);
                else
                {
                    CollectRpf9OpusPacketsLegacy(blob, 0, blob.Length, DetectRpf9OpusHeaderSizeLegacy(blob, chans.Count, bs), c, chans.Count, packets);
                    packetsOk = packets.Count > 0;
                }
                AwcStreamInfo ch = chans[c];
                if (!packetsOk || packets.Count == 0)
                {
                    ch.Data = new byte[0];
                    ch.CodecName = "OPUS (unparsed)";
                    ch.DecodeError = "No valid Opus packets found; raw AWC export available";
                    ch.Channels = Math.Max(1, ch.Channels);
                    ch.Size = 0; ch.Offset = dat.Offset; ch.BlockSize = bs;
                    awc.Streams.Add(ch);
                    continue;
                }
                int outChannels = Math.Max(1, ch.Channels);
                outChannels = ChannelsFromOpusPackets(packets, outChannels);
                ch.Data = BuildOggOpus(packets, ch.SampleRate > 0 ? ch.SampleRate : 48000, outChannels, 312);
                ch.BlockSize = bs; ch.CodecName = "OPUS"; ch.Channels = outChannels;
                ch.Size = (uint)(ch.Data == null ? 0 : ch.Data.Length); ch.Offset = dat.Offset;
                awc.Streams.Add(ch);
            }
        }

        private static int? ProbeRpf9SuperblockStreams(byte[] blob)
        {
            if (blob == null || blob.Length < 4096) return null;
            int n = 0; int totalBlks = 0; int limit = Math.Min(blob.Length, 2048);
            while ((n + 1) * 24 <= limit)
            {
                if (BitConverter.ToUInt32(blob, n * 24) != 0xFFFFFFFFu) break;
                uint cnt = BitConverter.ToUInt32(blob, n * 24 + 4);
                if (cnt == 0 || cnt > 255) break;
                totalBlks += (int)cnt; n++;
            }
            if (n >= 1 && totalBlks <= 255) return n;
            return null;
        }

        private static bool CollectRpf9SuperblockPackets(byte[] blob, int nSub, int subIdx, List<byte[]> packets)
        {
            if (blob == null || packets == null || nSub <= 0 || subIdx < 0 || subIdx >= nSub) return false;
            bool sawSuperblock = false;
            for (int sbStart = 0; sbStart < blob.Length; sbStart += 0x80000)
            {
                int sbLen = Math.Min(0x80000, blob.Length - sbStart);
                if (sbLen < nSub * 24) break;
                int[] counts = new int[nSub];
                bool valid = true; int totalBlks = 0;
                for (int s = 0; s < nSub; s++)
                {
                    if (BitConverter.ToUInt32(blob, sbStart + s * 24) != 0xFFFFFFFFu) { valid = false; break; }
                    uint c = BitConverter.ToUInt32(blob, sbStart + s * 24 + 4);
                    if (c > 256) { valid = false; break; }
                    counts[s] = (int)c; totalBlks += (int)c;
                }
                if (!valid || totalBlks == 0 || totalBlks > 256) continue;
                sawSuperblock = true;
                int dirBytes = nSub * 24 + totalBlks * 4;
                int dirBlks = Math.Max(1, (dirBytes + 2047) / 2048);
                int startBlk = dirBlks;
                for (int s = 0; s < subIdx; s++) startBlk += counts[s];
                int endBlk = startBlk + counts[subIdx];
                int byteStart = sbStart + startBlk * 2048;
                int byteEnd = Math.Min(sbStart + endBlk * 2048, sbStart + sbLen);
                if (byteStart < byteEnd && !CollectRpf92KPacketsRange(blob, byteStart, byteEnd - byteStart, packets)) return false;
            }
            return sawSuperblock && packets.Count > 0;
        }

        private static bool CollectRpf92KPacketsRange(byte[] data, int offset, int length, List<byte[]> packets)
        {
            if (data == null || packets == null) return false;
            int end = Math.Min(data.Length, offset + Math.Max(0, length));
            for (int bpos = Math.Max(0, offset); bpos < end; bpos += 0x800)
            {
                int bend = Math.Min(end, bpos + 0x800);
                int p = bpos;
                while (p + 2 <= bend)
                {
                    ushort len = BitConverter.ToUInt16(data, p);
                    if (len == 0) break;
                    if (len > 1276 || p + 2 + len > bend) return false;
                    byte[] pkt = new byte[len]; Array.Copy(data, p + 2, pkt, 0, len); packets.Add(pkt);
                    p += 2 + len;
                }
                for (; p < bend; p++) if (data[p] != 0) return false;
            }
            return true;
        }

        private static bool CollectRpf9Simple2KPackets(byte[] data, List<byte[]> packets)
        {
            if (data == null) return false;
            return CollectRpf92KPacketsRange(data, 0, data.Length, packets);
        }

        private static int DetectRpf9OpusHeaderSizeLegacy(byte[] data, int channels, int blockSize)
        {
            int[] candidates = new int[] { 0x800, 0x1000, 0x2000, 0x4000 };
            int best = 0x800; int bestScore = -1;
            int blocks = Math.Min(2, (data.Length + blockSize - 1) / blockSize);
            for (int ci = 0; ci < candidates.Length; ci++)
            {
                int hs = candidates[ci]; if (hs >= blockSize) continue; int score = 0;
                for (int b = 0; b < blocks; b++)
                {
                    int bo = b * blockSize; int blen = Math.Min(blockSize, data.Length - bo);
                    List<byte[]> pk = new System.Collections.Generic.List<byte[]>();
                    CollectRpf9OpusPacketsLegacy(data, bo, blen, hs, 0, Math.Max(1, channels), pk);
                    score += pk.Count;
                }
                if (score > bestScore || (score == bestScore && hs > best)) { bestScore = score; best = hs; }
            }
            return best;
        }

        private static void CollectRpf9OpusPacketsLegacy(byte[] data, int blockOff, int blockLen, int headerSize, int channelIndex, int channelCount, List<byte[]> packets)
        {
            int pos = blockOff + Math.Max(0, headerSize);
            int end = blockOff + blockLen;
            int sub = 0x800;
            while (pos + 2 <= end)
            {
                int subStart = pos;
                int subEnd = Math.Min(end, subStart + sub);
                int subIndex = ((subStart - (blockOff + Math.Max(0, headerSize))) / sub);
                int p = subStart;
                while (p + 2 <= subEnd)
                {
                    ushort len = BitConverter.ToUInt16(data, p);
                    if (len == 0 || p + 2 + len > subEnd) { p = subEnd; break; }
                    if (channelCount <= 1 || (subIndex % Math.Max(1, channelCount)) == channelIndex)
                    {
                        byte[] pkt = new byte[len]; Array.Copy(data, p + 2, pkt, 0, len); packets.Add(pkt);
                    }
                    p += 2 + len;
                }
                pos = subEnd;
            }
        }

        private static int ChannelsFromOpusPackets(List<byte[]> packets, int fallback)
        {
            if (packets == null || packets.Count == 0) return fallback > 0 ? fallback : 1;
            byte[] biggest = null;
            for (int i = 0; i < packets.Count; i++) { byte[] p = packets[i]; if (p != null && p.Length > 0 && (biggest == null || p.Length > biggest.Length)) biggest = p; }
            if (biggest == null) return fallback > 0 ? fallback : 1;
            return (biggest[0] & 0x04) != 0 ? 2 : 1;
        }

        private static void BuildRpf9Multi(byte[] data, AwcFile awc, bool be, uint flags, uint baseId, AwcChunkRef fmt, AwcChunkRef dat, List<AwcChunkRef> tags)
        {
            if (fmt == null || fmt.Offset + 12 > (uint)data.Length) return;
            int fo = (int)fmt.Offset;
            int blockCount = unchecked((int)ReadU32(data, fo + 0, be));
            int blockSize = unchecked((int)ReadU32(data, fo + 4, be));
            int channelCount = unchecked((int)ReadU32(data, fo + 8, be));
            if (channelCount <= 0 || channelCount > 64) return;
            List<AwcStreamInfo> chans = new System.Collections.Generic.List<AwcStreamInfo>();
            for (int c = 0; c < channelCount; c++)
            {
                int co = fo + 12 + c * 16;
                if (co + 16 > data.Length) break;
                AwcStreamInfo ch = new AwcStreamInfo();
                ch.Index = awc.Streams.Count + c;
                ch.StreamId = ReadU32(data, co + 0, be);
                ch.Samples = ReadU32(data, co + 4, be);
                ch.SampleRate = ReadU16(data, co + 10, be);
                ch.Codec = data[co + 12];
                ch.Channels = 1; ch.CodecName = AwcCodecName(ch.Codec, IsGta6Awc(flags)); ch.Data = new byte[0]; ch.BlockSize = blockSize; if (IsGta6Awc(flags) && ch.Codec == 11) ch.CodecName = "OPUS";
                chans.Add(ch);
            }
            if (dat == null || dat.Offset >= (uint)data.Length) { for (int i = 0; i < chans.Count; i++) awc.Streams.Add(chans[i]); return; }
            byte codec = chans.Count > 0 ? (byte)chans[0].Codec : (byte)0;
            if (codec == 0x0D || (IsGta6Awc(flags) && codec == 11))
            {
                ExtractRpf9OpusMulti(data, awc, dat, chans, blockCount, blockSize);
                return;
            }
            BuildGtaMulti(data, awc, be, baseId, fmt, dat);
        }

        private static void ParseAwcGta(byte[] data, AwcFile awc, bool be, uint flags, int streamCount, int dataOffset)
        {
            int pos = 16;
            bool chunkIndices = (flags & 1u) != 0;
            if (chunkIndices) pos += streamCount * 2;
            if (pos + streamCount * 4 > data.Length) return;

            uint[] infos = new uint[streamCount];
            for (int i = 0; i < streamCount; i++) { infos[i] = ReadU32(data, pos, be); pos += 4; }

            List<AwcChunkRef>[] per = new List<AwcChunkRef>[streamCount];
            for (int i = 0; i < streamCount; i++)
            {
                int tagCount = (int)((infos[i] >> 29) & 0x7u);
                List<AwcChunkRef> tags = new System.Collections.Generic.List<AwcChunkRef>();
                for (int t = 0; t < tagCount; t++)
                {
                    if (pos + 8 > data.Length) break;
                    ulong raw = ReadU64(data, pos, be); pos += 8;
                    AwcChunkRef r = new AwcChunkRef();
                    r.Type = (byte)((raw >> 56) & 0xFFu);
                    r.Size = (uint)((raw >> 28) & 0x0FFFFFFFu);
                    r.Offset = (uint)(raw & 0x0FFFFFFFu);
                    tags.Add(r);
                }
                per[i] = tags;
            }

            int parsed = 0;
            for (int i = 0; i < streamCount; i++)
            {
                uint streamId = infos[i] & 0x1FFFFFFFu;
                List<AwcChunkRef> tags = per[i];
                AwcChunkRef fmt = FindChunk(tags, 0xFA);
                AwcChunkRef fmt48 = FindChunk(tags, 0x48);
                AwcChunkRef fmt76 = FindChunk(tags, 0x76);
                AwcChunkRef dataTag = FindChunk(tags, 0x55);
                AwcChunkRef sfxData = FindChunk(tags, 0x44);
                if (fmt48 != null)
                {
                    int before = awc.Streams.Count;
                    BuildGtaMulti(data, awc, be, streamId, fmt48, dataTag);
                    if (awc.Streams.Count > before) { parsed++; continue; }
                }
                AwcStreamInfo si = new AwcStreamInfo();
                si.Index = awc.Streams.Count;
                si.StreamId = streamId;
                si.Channels = 1;
                AwcChunkRef fmtTag = fmt != null ? fmt : fmt76;
                if (fmtTag != null && fmtTag.Offset + 20 <= (uint)data.Length)
                {
                    int fo = (int)fmtTag.Offset;
                    si.Samples = ReadU32(data, fo + 0, be);
                    si.SampleRate = (int)ReadU16(data, fo + 8, be);
                    byte codec = 0;
                    if (fmtTag.Type == 0xFA && fo + 0x14 <= data.Length) codec = data[fo + 0x13];
                    else if (fmtTag.Type == 0x76 && fo + 0x1D <= data.Length) codec = data[fo + 0x1C];
                    si.Codec = codec;
                }
                AwcChunkRef dt = dataTag != null ? dataTag : sfxData;
                if (dt != null && dt.Offset < (uint)data.Length)
                {
                    uint sz = dt.Size;
                    if (sz == 0 || dt.Offset + sz > (uint)data.Length) sz = (uint)(data.Length - dt.Offset);
                    si.Offset = dt.Offset; si.Size = sz;
                    si.Data = new byte[sz];
                    Array.Copy(data, dt.Offset, si.Data, 0, sz);
                }
                else si.Data = new byte[0];
                si.CodecName = AwcCodecName(si.Codec);
                bool plausible = si.Size > 0 && si.Offset < (uint)data.Length;
                if (plausible) { awc.Streams.Add(si); parsed++; }
            }

            if (parsed == 0)
            {
                AppLog.Line("AWC GTA parse yielded 0 plausible streams, fallback. flags=0x" + flags.ToString("X8") + " sc=" + streamCount + " dataOff=0x" + dataOffset.ToString("X"));
                TryAwcFallback(data, awc, be, flags, streamCount, dataOffset, infos, per);
            }
        }

        private static bool LooksLikeKnownAwcLayout(int dataLen, List<AwcChunkRef>[] per)
        {
            if (per == null) return false;
            int plausible = 0; int implausible = 0;
            for (int i = 0; i < per.Length; i++)
            {
                List<AwcChunkRef> tags = per[i];
                if (tags == null) continue;
                for (int t = 0; t < tags.Count; t++)
                {
                    AwcChunkRef r = tags[t];
                    if (r.Size == 0 || r.Offset >= (uint)dataLen || (ulong)r.Offset + r.Size > (ulong)dataLen) { implausible++; continue; }
                    bool typeOk = r.Type == 0x55 || r.Type == 0x44 || r.Type == 0xFA || r.Type == 0x76 || r.Type == 0x48 || r.Type == 0xA3 || r.Type == 0x7F || r.Type == 0x68 || r.Type == 0x36 || r.Type == 0x5C;
                    if (typeOk) plausible++; else implausible++;
                }
            }
            if (implausible > 0 && plausible == 0) return false;
            if (implausible > plausible * 2) return false;
            return plausible > 0;
        }

        private static void TryAwcFallback(byte[] data, AwcFile awc, bool be, uint flags, int streamCount, int dataOffset, uint[] infos, List<AwcChunkRef>[] per)
        {
            for (int i = 0; i < Math.Min(streamCount, 8); i++)
            {
                int tc = (int)((infos[i] >> 29) & 0x7u);
                uint sid = infos[i] & 0x1FFFFFFFu;
                AppLog.Line("  info[" + i + "] raw=0x" + infos[i].ToString("X8") + " tc=" + tc + " id=0x" + sid.ToString("X8"));
                List<AwcChunkRef> tags = per[i];
                if (tags == null) continue;
                for (int t = 0; t < tags.Count; t++) AppLog.Line("    tag 0x" + tags[t].Type.ToString("X2") + " size=0x" + tags[t].Size.ToString("X") + " off=0x" + tags[t].Offset.ToString("X"));
            }

            bool layoutKnown = LooksLikeKnownAwcLayout(data.Length, per);
            if (layoutKnown)
            {
                ScanAwcFormatDataPairs(data, awc, be);
                if (awc.Streams.Count > 0) return;
            }
            else
            {
                AppLog.Line("AWC fallback: tag layout implausible, skipping pair scan. flags=0x" + flags.ToString("X8"));
            }

            ScanEmbeddedMedia(data, awc);
            if (awc.Streams.Count > 0) return;

            if (dataOffset > 0 && dataOffset < data.Length)
            {
                int sz = data.Length - dataOffset;
                AwcStreamInfo si = new AwcStreamInfo();
                si.Index = 0; si.StreamId = 0; si.Codec = -1; si.CodecName = "AWC-DATA";
                si.Channels = 0; si.SampleRate = 0; si.Samples = 0;
                si.Offset = (uint)dataOffset; si.Size = (uint)sz;
                si.Data = new byte[sz];
                Array.Copy(data, dataOffset, si.Data, 0, sz);
                si.DecodeError = "AWC layout not recognized; raw export available";
                awc.Streams.Add(si);
                return;
            }

            AwcStreamInfo raw = new AwcStreamInfo();
            raw.Index = 0; raw.StreamId = 0; raw.Codec = -1; raw.CodecName = "AWC-RAW";
            raw.Offset = 0; raw.Size = (uint)data.Length; raw.Data = data;
            raw.DecodeError = "AWC layout not recognized; raw export available";
            awc.Streams.Add(raw);
        }

        private static void ScanAwcFormatDataPairs(byte[] data, AwcFile awc, bool be)
        {
            try
            {
                ScanEmbeddedMedia(data, awc);
                if (awc.Streams.Count > 0) return;
                List<AwcChunkRef> dataRefs = new System.Collections.Generic.List<AwcChunkRef>();
                List<AwcChunkRef> fmtRefs = new System.Collections.Generic.List<AwcChunkRef>();
                int maxScan = Math.Min(data.Length - 8, 64 * 1024);
                for (int j = 16; j < maxScan; j += 8)
                {
                    ulong raw = ReadU64(data, j, be);
                    byte tt = (byte)((raw >> 56) & 0xFF);
                    uint sz = (uint)((raw >> 28) & 0x0FFFFFFF);
                    uint of = (uint)(raw & 0x0FFFFFFF);
                    if (of >= (uint)data.Length || sz == 0) continue;
                    if ((ulong)of + sz > (ulong)data.Length) continue;
                    if (tt == 0x55 || tt == 0x44) dataRefs.Add(new AwcChunkRef { Type = tt, Size = sz, Offset = of });
                    if (tt == 0xFA || tt == 0x76 || tt == 0x48) fmtRefs.Add(new AwcChunkRef { Type = tt, Size = sz, Offset = of });
                }
                if (fmtRefs.Count == 0 || dataRefs.Count == 0) return;
                for (int fi = 0; fi < fmtRefs.Count && awc.Streams.Count < 64; fi++)
                {
                    AwcChunkRef fr = fmtRefs[fi];
                    if (fr.Offset + 16 > (uint)data.Length) continue;
                    int fo = (int)fr.Offset;
                    AwcStreamInfo si = new AwcStreamInfo();
                    si.Index = awc.Streams.Count;
                    si.StreamId = 0;
                    si.Channels = 1;
                    if (fr.Type == 0xFA || fr.Type == 0x76)
                    {
                        si.Samples = ReadU32(data, fo + 0, be);
                        si.SampleRate = (int)ReadU16(data, fo + 8, be);
                        si.Codec = fr.Type == 0xFA ? data[fo + 0x13] : (fo + 0x1C < data.Length ? data[fo + 0x1C] : (byte)0);
                    }
                    else continue;
                    AwcChunkRef dr = dataRefs[Math.Min(fi, dataRefs.Count - 1)];
                    si.Offset = dr.Offset; si.Size = dr.Size;
                    si.Data = new byte[dr.Size];
                    Array.Copy(data, dr.Offset, si.Data, 0, dr.Size);
                    si.CodecName = AwcCodecName(si.Codec);
                    bool rateOk = si.SampleRate == 8000 || si.SampleRate == 11025 || si.SampleRate == 12000 || si.SampleRate == 16000 || si.SampleRate == 22050 || si.SampleRate == 24000 || si.SampleRate == 32000 || si.SampleRate == 44100 || si.SampleRate == 48000;
                    if (!rateOk) continue;
                    awc.Streams.Add(si);
                    AppLog.Line("AWC fallback pair hz=" + si.SampleRate + " codec=" + si.CodecName + " off=0x" + si.Offset.ToString("X") + " sz=" + si.Size);
                }
            }
            catch (Exception ex) { AppLog.Line("AWC fallback scan err: " + ex.Message); }
        }

        private static void ScanEmbeddedMedia(byte[] data, AwcFile awc)
        {
            try
            {
                for (int i = 0; i < data.Length - 12 && awc.Streams.Count < 64; i++)
                {
                    if (data[i] == (byte)'R' && data[i + 1] == (byte)'I' && data[i + 2] == (byte)'F' && data[i + 3] == (byte)'F' && data[i + 8] == (byte)'W' && data[i + 9] == (byte)'A' && data[i + 10] == (byte)'V' && data[i + 11] == (byte)'E')
                    {
                        uint sz = BitConverter.ToUInt32(data, i + 4);
                        long total = 8L + sz;
                        if (total < 44) total = Math.Min(data.Length - i, 8 * 1024 * 1024);
                        if (i + total > data.Length) total = data.Length - i;
                        AwcStreamInfo si = new AwcStreamInfo();
                        si.Index = awc.Streams.Count; si.StreamId = 0; si.Codec = -1; si.CodecName = "RIFF-WAV";
                        si.Offset = (uint)i; si.Size = (uint)total;
                        si.Data = new byte[total]; Array.Copy(data, i, si.Data, 0, (int)total);
                        TryFillWavInfo(si); awc.Streams.Add(si);
                        i += (int)Math.Max(16, total - 1);
                    }
                    else if (data[i] == (byte)'O' && data[i + 1] == (byte)'g' && data[i + 2] == (byte)'g' && data[i + 3] == (byte)'S')
                    {
                        int len = Math.Min(data.Length - i, 8 * 1024 * 1024);
                        AwcStreamInfo si = new AwcStreamInfo();
                        si.Index = awc.Streams.Count; si.StreamId = 0; si.Codec = -1; si.CodecName = "OGG";
                        si.Offset = (uint)i; si.Size = (uint)len;
                        si.Data = new byte[len]; Array.Copy(data, i, si.Data, 0, len);
                        awc.Streams.Add(si); i += len - 1;
                    }
                }
            }
            catch (Exception ex) { AppLog.Line("AWC embedded scan err: " + ex.Message); }
        }

        private static void TryFillWavInfo(AwcStreamInfo si)
        {
            try
            {
                byte[] d = si.Data; if (d == null || d.Length < 44) return;
                int pos = 12;
                while (pos + 8 <= d.Length)
                {
                    string id = Encoding.ASCII.GetString(d, pos, 4);
                    int sz = BitConverter.ToInt32(d, pos + 4);
                    if (id == "fmt " && sz >= 16 && pos + 8 + sz <= d.Length)
                    {
                        si.Channels = BitConverter.ToUInt16(d, pos + 10);
                        si.SampleRate = BitConverter.ToInt32(d, pos + 12);
                    }
                    if (id == "data" && pos + 8 <= d.Length)
                    {
                        if (si.Channels > 0 && si.SampleRate > 0) si.Samples = (uint)Math.Max(0, (sz / Math.Max(1, si.Channels * 2)));
                        break;
                    }
                    if (sz <= 0) break;
                    pos += 8 + sz + (sz & 1);
                }
            }
            catch { }
        }
        private static void BuildGtaMulti(byte[] data, AwcFile awc, bool be, uint baseId, AwcChunkRef fmt, AwcChunkRef dat)
        {
            if (fmt == null || fmt.Offset + 12 > (uint)data.Length) return;
            int fo = (int)fmt.Offset;
            int blockCount = unchecked((int)ReadU32(data, fo + 0, be));
            int blockSize = unchecked((int)ReadU32(data, fo + 4, be));
            int channelCount = unchecked((int)ReadU32(data, fo + 8, be));
            if (channelCount <= 0 || channelCount > 64 || blockCount < 0 || blockSize < 0) return;
            List<AwcStreamInfo> chans = new System.Collections.Generic.List<AwcStreamInfo>();
            for (int c = 0; c < channelCount; c++)
            {
                int co = fo + 12 + c * 16;
                if (co + 16 > data.Length) break;
                AwcStreamInfo ch = new AwcStreamInfo();
                ch.Index = awc.Streams.Count + c;
                ch.StreamId = ReadU32(data, co + 0, be);
                ch.Samples = ReadU32(data, co + 4, be);
                ch.SampleRate = ReadU16(data, co + 10, be);
                ch.Codec = data[co + 12];
                ch.Channels = 1;
                ch.CodecName = AwcCodecName(ch.Codec);
                ch.Data = new byte[0];
                chans.Add(ch);
            }
            if (dat == null || dat.Offset >= (uint)data.Length || blockCount <= 0 || blockSize <= 0) { for (int i = 0; i < chans.Count; i++) awc.Streams.Add(chans[i]); return; }

            byte codec = chans.Count > 0 ? (byte)chans[0].Codec : (byte)0;
            int entrySize = 0x10;
            int seekEntrySize = 4;
            int extraEntrySize = 0;
            int headerPadding = 0x800;
            int frameSize = 0x800;
            if (codec == 0x07 || codec == 0x08) { entrySize = 0x18; seekEntrySize = 4; extraEntrySize = 0; headerPadding = 0x800; frameSize = 0x800; }
            else if (codec == 0x0D || codec == 0x0F) { entrySize = 0x10; seekEntrySize = 0; extraEntrySize = 0x70; headerPadding = 0; frameSize = 0x800; }
            else { entrySize = 0x10; seekEntrySize = 4; extraEntrySize = 0; headerPadding = 0x800; frameSize = 0x800; }

            for (int b = 0; b < blockCount; b++)
            {
                long bo = (long)dat.Offset + (long)b * blockSize;
                if (bo < 0 || bo + (long)chans.Count * entrySize > data.Length) break;
                int[] entries = new int[chans.Count];
                int[] skips = new int[chans.Count];
                for (int c = 0; c < chans.Count; c++)
                {
                    int ce = (int)bo + c * entrySize;
                    entries[c] = Math.Max(0, unchecked((int)ReadU32(data, ce + 4, be)));
                    if (entrySize >= 16) skips[c] = unchecked((int)ReadU32(data, ce + 8, be));
                }

                int headerSize = chans.Count * entrySize;
                for (int c = 0; c < chans.Count; c++) headerSize += entries[c] * seekEntrySize;
                headerSize += chans.Count * extraEntrySize;
                int dataStart = (int)bo + headerSize;
                if (headerPadding > 0) dataStart = (int)bo + ((headerSize + headerPadding - 1) / headerPadding) * headerPadding;

                int cursor = dataStart;
                for (int c = 0; c < chans.Count; c++)
                {
                    int chunkSize = entries[c] * frameSize;
                    int repeat = 0;
                    if (skips[c] > 0)
                    {
                        if (codec == 0x05 || codec == 0x08) repeat = frameSize;
                        else if (codec == 0x07)
                        {
                            int maxFrames = Math.Max(0, skips[c] / 1152);
                            int pos = 0;
                            int frames = 0;
                            int limit = Math.Min(frameSize, data.Length - cursor);
                            while (pos + 4 <= limit && frames < maxFrames)
                            {
                                uint hdr = ReadU32BE(data, cursor + pos);
                                int fsz = MpegFrameSize(hdr);
                                if (fsz <= 0 || pos + fsz > limit) break;
                                pos += fsz; frames++;
                            }
                            repeat = pos;
                        }
                        else if (codec == 0x04)
                        {
                            repeat = Math.Min(frameSize, Math.Max(0, skips[c] / 2) + 4 * Math.Max(1, chans.Count));
                        }
                    }
                    if (codec == 0x07 && entrySize >= 24)
                    {
                        int chanSize = unchecked((int)ReadU32(data, (int)bo + c * entrySize + 20, be));
                        if (chanSize > 0) chunkSize = (chanSize + 0x0F) & ~0x0F;
                    }
                    int dlen = Math.Max(0, chunkSize - repeat);
                    int src = cursor + repeat;
                    if (dlen > 0 && src >= 0 && src + dlen <= data.Length)
                    {
                        AwcStreamInfo ch = chans[c];
                        byte[] merged = new byte[ch.Data.Length + dlen];
                        if (ch.Data.Length > 0) Array.Copy(ch.Data, 0, merged, 0, ch.Data.Length);
                        Array.Copy(data, src, merged, ch.Data.Length, dlen);
                        ch.Data = merged; ch.Size = (uint)ch.Data.Length; ch.Offset = (uint)src;
                    }
                    cursor += chunkSize;
                }
            }
            for (int i = 0; i < chans.Count; i++) awc.Streams.Add(chans[i]);
        }

        private static bool IsRpf9AudioAwc(uint flags) { return flags == 0xFF020001u || flags == 0xFF210001u || flags == 0xFF250001u || flags == 0xFF610001u; }
        private static bool IsGta6Awc(uint flags) { if (IsRpf9AudioAwc(flags)) return true; int v = (int)((flags >> 16) & 0xFF); return v >= 0x21; }
        private static string AwcCodecName(int codec, bool gta6)
        {
            if (gta6)
            {
                if (codec == 0) return "PCM16LE";
                if (codec == 1) return "ADPCM";
                if (codec == 2) return "PCM32F";
                if (codec == 5) return "XMA2";
                if (codec == 11) return "OPUS";
                if (codec == -1) return "RAW";
                return "codec_" + codec.ToString("X2");
            }
            if (codec == 0) return "PCM16LE";
            if (codec == 1) return "PCM16BE";
            if (codec == 4) return "AWC-IMA";
            if (codec == 5 || codec == 0x0B) return "XMA2";
            if (codec == 7) return "MP3";
            if (codec == 8) return "Vorbis";
            if (codec == 0x0C) return "DSP";
            if (codec == 0x0D) return "OPUS";
            if (codec == 0x0F) return "ATRAC9";
            if (codec == 0x10) return "DSP";
            if (codec == 0x11) return "MSADPCM";
            if (codec == -1) return "RAW";
            return "codec_" + codec.ToString("X2");
        }
        private static string AwcCodecName(int codec) { return AwcCodecName(codec, false); }

        public static void EnsureDecoded(AwcStreamInfo s)
        {
            if (s == null) return;
            if (s.DecodedWav != null || !string.IsNullOrEmpty(s.DecodeError)) return;
            if (s.Data == null || s.Data.Length == 0) { s.DecodeError = "no data"; return; }
            try
            {
                s.DecodedWav = DecodeAwcToWav(s);
                if (s.DecodedWav == null)
                {
                    string ext = GuessAwcInputExt(s);
                    try
                    {
                        byte[] ffInput = PrepareAwcDataForFfmpeg(s); string ffExt = (s.CodecName == "XMA2" || s.Codec == 0x0B || s.Codec == 0x05 || s.Codec == 4 || s.CodecName == "AWC-IMA" || s.Codec == 0x11 || s.CodecName == "MSADPCM") ? ".wav" : ext; if (s.CodecName == "OPUS" || s.CodecName == "OGG") ffExt = ".ogg"; byte[] ff = Ffmpeg.DecodeToWav(ffInput ?? s.Data, ffExt);
                        if (ff != null && Ffmpeg.LooksLikeWavBytes(ff))
                        {
                            s.DecodedWav = ff;
                            s.DecodeError = null;
                            return;
                        }
                        if (ff != null && ff.Length > 0) { string msg = "FFmpeg produced non-WAV output for AWC codec=" + s.CodecName; AppLog.Line(msg + " stem=" + s.Index); if (string.IsNullOrEmpty(s.DecodeError)) s.DecodeError = msg; }
                    }
                    catch (Exception ex)
                    {
                        AppLog.Line("FFmpeg AWC decode failed codec=" + s.CodecName + ": " + ex.Message);
                        if (string.IsNullOrEmpty(s.DecodeError)) s.DecodeError = ex.Message;
                    }
                }
                if (s.DecodedWav == null && string.IsNullOrEmpty(s.DecodeError)) s.DecodeError = "Unsupported codec " + s.CodecName;
            }
            catch (Exception ex)
            {
                s.DecodeError = ex.Message;
                AppLog.Line("Decode fail id=0x" + s.StreamId.ToString("X8") + " codec=" + s.CodecName + ": " + ex.Message);
            }
        }

        private static string GuessAwcInputExt(AwcStreamInfo s)
        {
            if (s == null) return ".bin";
            if (s.Codec == 7 || s.CodecName == "MP3") return ".mp3";
            if (s.Codec == 8 || s.CodecName == "Vorbis" || s.CodecName == "OGG" || s.CodecName == "OPUS" || s.Codec == 11) return ".ogg";
            if (s.CodecName == "XMA2" || s.Codec == 0x0B || s.Codec == 0x05) return ".wav";
            if (s.Codec == 4 || s.CodecName == "AWC-IMA") return ".wav"; if (s.Codec == 0x11 || s.CodecName == "MSADPCM") return ".wav";
            if (s.CodecName == "RIFF-WAV" || s.CodecName == "WAV") return ".wav";
            return ".bin";
        }

        private static byte[] PrepareAwcDataForFfmpeg(AwcStreamInfo s)
        {
            if (s == null || s.Data == null || s.Data.Length == 0) return null;
            if (s.CodecName == "XMA2" || s.Codec == 0x0B || s.Codec == 0x05) { byte[] xd = DeblockRpf9Simple2K(s.Data); if (xd == null || xd.Length == 0) xd = s.Data; return WrapXma2ToWav(xd, s.SampleRate, s.Channels, s.Samples); }
            if (s.Codec == 4 || s.CodecName == "AWC-IMA") { short[] pcm = DecodeAwcIma(s.Data, Math.Max(1, s.Channels)); return BuildWav16(pcm, s.SampleRate <= 0 ? 48000 : s.SampleRate, Math.Max(1, s.Channels)); }
            return s.Data;
        }

        private static byte[] WrapXma2ToWav(byte[] data, int sampleRate, int channels, uint samples)
        {
            if (data == null || data.Length == 0) return null;
            if (sampleRate <= 0) sampleRate = 48000;
            if (channels <= 0) channels = 1;
            int bytesPerBlock = 2048;
            int blockCount = Math.Max(1, (data.Length + bytesPerBlock - 1) / bytesPerBlock);
            int blockAlign = bytesPerBlock;
            ushort numStreams = (ushort)Math.Max(1, (channels + 1) / 2);
            uint channelMask = GetDefaultChannelMask(channels);
            uint samplesEncoded = samples == 0 ? (uint)blockCount * 512u : samples;
            uint playBegin = 0;
            uint playLength = samples;
            uint loopBegin = 0;
            uint loopLength = 0;
            byte loopCount = 0;
            byte encoderVersion = 4;
            int fmtChunkSize = 52;
            int dataSize = data.Length;
            int riffSize = 4 + (8 + fmtChunkSize) + (8 + dataSize);
            int avgBytes = sampleRate * blockAlign / 512;
            if (avgBytes <= 0) avgBytes = sampleRate * channels * 2;
            using (MemoryStream ms = new MemoryStream(8 + riffSize))
            using (BinaryWriter bw = new BinaryWriter(ms))
            {
                bw.Write(Encoding.ASCII.GetBytes("RIFF"));
                bw.Write(riffSize);
                bw.Write(Encoding.ASCII.GetBytes("WAVE"));
                bw.Write(Encoding.ASCII.GetBytes("fmt "));
                bw.Write(fmtChunkSize);
                bw.Write((ushort)0x0166);
                bw.Write((ushort)channels);
                bw.Write(sampleRate);
                bw.Write(avgBytes);
                bw.Write((ushort)blockAlign);
                bw.Write((ushort)16);
                bw.Write((ushort)34);
                bw.Write(numStreams);
                bw.Write(channelMask);
                bw.Write(samplesEncoded);
                bw.Write((uint)bytesPerBlock);
                bw.Write(playBegin);
                bw.Write(playLength);
                bw.Write(loopBegin);
                bw.Write(loopLength);
                bw.Write(loopCount);
                bw.Write(encoderVersion);
                bw.Write((ushort)blockCount);
                bw.Write(Encoding.ASCII.GetBytes("data"));
                bw.Write(dataSize);
                bw.Write(data);
                return ms.ToArray();
            }
        }

        private static uint GetDefaultChannelMask(int channels)
        {
            if (channels == 1) return 0x4u;
            if (channels == 2) return 0x3u;
            if (channels == 3) return 0x7u;
            if (channels == 4) return 0x33u;
            if (channels == 5) return 0x37u;
            if (channels == 6) return 0x60Fu;
            return 0u;
        }
        private static byte[] WrapImaAdpcmToWav(byte[] data, int sampleRate, int channels)
        {
            if (data == null || data.Length == 0) return null;
            if (sampleRate <= 0) sampleRate = 48000;
            if (channels <= 0) channels = 1;
            int blockAlign = 256 * channels;
            int samplesPerBlock = (blockAlign - 4 * channels) * 2 / channels + 1;
            int avgBytes = sampleRate * blockAlign / Math.Max(1, samplesPerBlock);
            int fmtChunkSize = 20;
            int dataSize = data.Length;
            int riffSize = 4 + (8 + fmtChunkSize) + (8 + dataSize);
            using (MemoryStream ms = new MemoryStream(44 + fmtChunkSize + dataSize))
            using (BinaryWriter bw = new BinaryWriter(ms))
            {
                bw.Write(Encoding.ASCII.GetBytes("RIFF"));
                bw.Write(riffSize);
                bw.Write(Encoding.ASCII.GetBytes("WAVE"));
                bw.Write(Encoding.ASCII.GetBytes("fmt "));
                bw.Write(fmtChunkSize);
                bw.Write((ushort)0x0011);
                bw.Write((ushort)channels);
                bw.Write(sampleRate);
                bw.Write(avgBytes);
                bw.Write((ushort)blockAlign);
                bw.Write((ushort)4);
                bw.Write((ushort)2);
                bw.Write((ushort)samplesPerBlock);
                bw.Write(Encoding.ASCII.GetBytes("data"));
                bw.Write(dataSize);
                bw.Write(data);
                return ms.ToArray();
            }
        }
        public static byte[] DecodeAwcToWav(AwcStreamInfo si)
        {
            if (si == null || si.Data == null || si.Data.Length == 0) return null;
            if (si.Data.Length >= 4 && si.Data[0] == 0x4F && si.Data[1] == 0x67 && si.Data[2] == 0x67 && si.Data[3] == 0x53) return null;
            if (si.Codec == -1 && si.CodecName == "OGG") { si.DecodeError = "OGG/Vorbis decode not supported; raw export is available"; return null; }
            int ch = si.Channels <= 0 ? 1 : si.Channels;
            int hz = si.SampleRate <= 0 ? 48000 : si.SampleRate;
            short[] pcm = null;
            int c = si.Codec;
            if (c == 0)
            {
                int samples = si.Data.Length / 2;
                pcm = new short[samples];
                for (int i = 0; i < samples; i++) pcm[i] = unchecked((short)(si.Data[i * 2] | (si.Data[i * 2 + 1] << 8)));
            }
            else if (c == 1)
            {
                int samples = si.Data.Length / 2;
                pcm = new short[samples];
                for (int i = 0; i < samples; i++) pcm[i] = unchecked((short)((si.Data[i * 2] << 8) | si.Data[i * 2 + 1]));
            }
            else if (c == 4)
            {
                pcm = DecodeAwcIma(si.Data, ch);
            }
            else
            {
                si.DecodeError = "Unsupported codec " + si.CodecName + "; raw export is available";
                return null;
            }
            if (pcm == null || pcm.Length == 0) { si.DecodeError = "decode produced no samples"; return null; }
            return BuildWav16(pcm, hz, ch);
        }
        private static short[] DecodeAwcIma(byte[] data, int channels)
        {
            if (data == null || data.Length == 0) return new short[0];
            if (channels <= 0) channels = 1;
            int blockAlign = 0x800;
            if (blockAlign % (4 * channels) != 0) blockAlign = 256 * channels;
            int blocks = data.Length / blockAlign;
            if (blocks <= 0) { blockAlign = data.Length - (data.Length % Math.Max(1, 4 * channels)); blocks = blockAlign > 0 ? 1 : 0; }
            if (blocks <= 0) return new short[0];
            int[] stepTable = {7,8,9,10,11,12,13,14,16,17,19,21,23,25,28,31,34,37,41,45,50,55,60,66,73,80,88,97,107,118,130,143,157,173,190,209,230,253,279,307,337,371,408,449,494,544,598,658,724,796,876,963,1060,1166,1282,1411,1552,1707,1878,2066,2272,2499,2749,3024,3327,3660,4026,4428,4871,5358,5894,6484,7132,7845,8630,9493,10442,11487,12635,13899,15289,16818,18500,20350,22385,24623,27086,29794,32767};
            int[] indexAdjust = {-1,-1,-1,-1,2,4,6,8,-1,-1,-1,-1,2,4,6,8};
            List<short> outp = new System.Collections.Generic.List<short>();
            for (int b = 0; b < blocks; b++)
            {
                int bo = b * blockAlign;
                int[] pred = new int[channels];
                int[] stepIdx = new int[channels];
                for (int c = 0; c < channels; c++)
                {
                    int ho = bo + c * 4;
                    if (ho + 4 > data.Length) { pred[c] = 0; stepIdx[c] = 0; continue; }
                    pred[c] = BitConverter.ToInt16(data, ho);
                    stepIdx[c] = data[ho + 2];
                    if (stepIdx[c] < 0) stepIdx[c] = 0; if (stepIdx[c] > 88) stepIdx[c] = 88;
                }
                int dataBytes = blockAlign - 4 * channels;
                int nibblesPerCh = dataBytes * 2 / Math.Max(1, channels);
                for (int n = 0; n < nibblesPerCh; n++)
                {
                    for (int c = 0; c < channels; c++)
                    {
                        if (n == 0) { int pv = pred[c]; if (pv > 32767) pv = 32767; if (pv < -32768) pv = -32768; outp.Add((short)pv); continue; }
                        int nib = n - 1;
                        int byteIndex = bo + 4 * channels + (nib / 2) * channels + c;
                        if (byteIndex >= data.Length) { outp.Add((short)pred[c]); continue; }
                        int bval = data[byteIndex];
                        int code = (nib % 2 == 0) ? (bval & 0x0F) : ((bval >> 4) & 0x0F);
                        int step = stepTable[stepIdx[c]];
                        int diff = step >> 3;
                        if ((code & 1) != 0) diff += step >> 2;
                        if ((code & 2) != 0) diff += step >> 1;
                        if ((code & 4) != 0) diff += step;
                        if ((code & 8) != 0) pred[c] -= diff; else pred[c] += diff;
                        if (pred[c] > 32767) pred[c] = 32767; if (pred[c] < -32768) pred[c] = -32768;
                        stepIdx[c] += indexAdjust[code];
                        if (stepIdx[c] < 0) stepIdx[c] = 0; if (stepIdx[c] > 88) stepIdx[c] = 88;
                        outp.Add((short)pred[c]);
                    }
                }
            }
            return outp.ToArray();
        }

        private static short[] DecodeImaAdpcm(byte[] data, int channels)
        {
            if (channels <= 0) channels = 1;
            int blockAlign = 256 * channels;
            if (data.Length < 4 * channels) return new short[0];
            int blocks = data.Length / blockAlign;
            if (blocks <= 0) { blockAlign = data.Length; blocks = 1; }
            int[] stepTable = {7,8,9,10,11,12,13,14,16,17,19,21,23,25,28,31,34,37,41,45,50,55,60,66,73,80,88,97,107,118,130,143,157,173,190,209,230,253,279,307,337,371,408,449,494,544,598,658,724,796,876,963,1060,1166,1282,1411,1552,1707,1878,2066,2272,2499,2749,3024,3327,3660,4026,4428,4871,5358,5894,6484,7132,7845,8630,9493,10442,11487,12635,13899,15289,16818,18500,20350,22385,24623,27086,29794,32767};
            int[] indexAdjust = {-1,-1,-1,-1,2,4,6,8};
            List<short> outp = new List<short>(blocks * 505 * channels);
            for (int b = 0; b < blocks; b++)
            {
                int bo = b * blockAlign;
                int[] pred = new int[channels];
                int[] stepIdx = new int[channels];
                int perCh = blockAlign / channels;
                for (int c = 0; c < channels; c++)
                {
                    int ho = bo + c * 4;
                    if (ho + 4 > data.Length) { pred[c] = 0; stepIdx[c] = 0; continue; }
                    pred[c] = BitConverter.ToInt16(data, ho);
                    stepIdx[c] = data[ho + 2];
                    if (stepIdx[c] < 0) stepIdx[c] = 0;
                    if (stepIdx[c] > 88) stepIdx[c] = 88;
                }
                int samplesPerCh = Math.Max(1, (perCh - 4) * 2 + 1);
                for (int s = 0; s < samplesPerCh; s++)
                {
                    for (int c = 0; c < channels; c++)
                    {
                        if (s == 0)
                        {
                            int pv = pred[c]; if (pv > 32767) pv = 32767; if (pv < -32768) pv = -32768;
                            outp.Add((short)pv);
                            continue;
                        }
                        int nibbleIndex = s - 1;
                        int byteInCh = 4 + nibbleIndex / 2;
                        int dataOff = bo + c * perCh + byteInCh;
                        if (dataOff >= data.Length) { outp.Add((short)pred[c]); continue; }
                        int bval = data[dataOff];
                        int nibble = (nibbleIndex % 2 == 0) ? (bval & 0x0F) : ((bval >> 4) & 0x0F);
                        int step = stepTable[stepIdx[c]];
                        int diff = step >> 3;
                        if ((nibble & 1) != 0) diff += step >> 2;
                        if ((nibble & 2) != 0) diff += step >> 1;
                        if ((nibble & 4) != 0) diff += step;
                        if ((nibble & 8) != 0) pred[c] -= diff; else pred[c] += diff;
                        if (pred[c] > 32767) pred[c] = 32767;
                        if (pred[c] < -32768) pred[c] = -32768;
                        stepIdx[c] += indexAdjust[nibble & 7];
                        if (stepIdx[c] < 0) stepIdx[c] = 0;
                        if (stepIdx[c] > 88) stepIdx[c] = 88;
                        outp.Add((short)pred[c]);
                    }
                }
            }
            return outp.ToArray();
        }

        private static byte[] BuildWav16(short[] pcm, int sampleRate, int channels)
        {
            int dataSize = pcm.Length * 2;
            byte[] wav = new byte[44 + dataSize];
            Encoding.ASCII.GetBytes("RIFF").CopyTo(wav, 0);
            BitConverter.GetBytes(36 + dataSize).CopyTo(wav, 4);
            Encoding.ASCII.GetBytes("WAVE").CopyTo(wav, 8);
            Encoding.ASCII.GetBytes("fmt ").CopyTo(wav, 12);
            BitConverter.GetBytes(16).CopyTo(wav, 16);
            BitConverter.GetBytes((short)1).CopyTo(wav, 20);
            BitConverter.GetBytes((short)channels).CopyTo(wav, 22);
            BitConverter.GetBytes(sampleRate).CopyTo(wav, 24);
            BitConverter.GetBytes(sampleRate * channels * 2).CopyTo(wav, 28);
            BitConverter.GetBytes((short)(channels * 2)).CopyTo(wav, 32);
            BitConverter.GetBytes((short)16).CopyTo(wav, 34);
            Encoding.ASCII.GetBytes("data").CopyTo(wav, 36);
            BitConverter.GetBytes(dataSize).CopyTo(wav, 40);
            Buffer.BlockCopy(pcm, 0, wav, 44, dataSize);
            return wav;
        }
    }

    internal sealed class SimpleModel
    {
        public List<System.Windows.Media.Media3D.Point3D> Positions = new List<System.Windows.Media.Media3D.Point3D>();
        public List<System.Windows.Media.Media3D.Vector3D> Normals = new List<System.Windows.Media.Media3D.Vector3D>();
        public List<int> Triangles = new List<int>();
        public System.Windows.Media.Media3D.Rect3D Bounds;
        public string Info = "";
    }

    internal static class SimpleModelLoader
    {
        public static SimpleModel TryLoad(FileEntry entry)
        {
            if (entry == null || entry.Data == null || entry.Data.Length == 0) return null;
            string name = (entry.Name ?? "").ToLowerInvariant();
            if (name.EndsWith(".obj") || LooksLikeObj(entry.Data)) return LoadObj(entry.Data);
            if (RpfScanner.LooksLikeRsc(entry.Data))
            {
                SimpleModel m = new SimpleModel();
                m.Info = DescribeRsc(entry.Data);
                return m;
            }
            return null;
        }

        private static bool LooksLikeObj(byte[] data)
        {
            if (data == null || data.Length < 8) return false;
            string s = Encoding.ASCII.GetString(data, 0, Math.Min(data.Length, 256));
            return s.IndexOf("\nv ", StringComparison.Ordinal) >= 0 || s.StartsWith("v ", StringComparison.Ordinal) || s.IndexOf("\nf ", StringComparison.Ordinal) >= 0;
        }

        private static SimpleModel LoadObj(byte[] data)
        {
            SimpleModel m = new SimpleModel();
            var lines = Encoding.UTF8.GetString(data).Replace("\r\n", "\n").Split('\n');
            var verts = new List<System.Windows.Media.Media3D.Point3D>();
            foreach (var raw in lines)
            {
                string line = raw.Trim();
                if (line.Length == 0 || line.StartsWith("#")) continue;
                if (line.StartsWith("v "))
                {
                    var p = line.Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
                    if (p.Length >= 4)
                    {
                        double x, y, z;
                        if (double.TryParse(p[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out x)
                         && double.TryParse(p[2], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out y)
                         && double.TryParse(p[3], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out z))
                            verts.Add(new System.Windows.Media.Media3D.Point3D(x, y, z));
                    }
                }
                else if (line.StartsWith("f "))
                {
                    var p = line.Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
                    List<int> face = new List<int>();
                    for (int i = 1; i < p.Length; i++)
                    {
                        string tok = p[i].Split('/')[0];
                        int vi;
                        if (int.TryParse(tok, out vi))
                        {
                            if (vi < 0) vi = verts.Count + vi; else vi = vi - 1;
                            if (vi >= 0 && vi < verts.Count) face.Add(vi);
                        }
                    }
                    for (int i = 1; i + 1 < face.Count; i++)
                    {
                        m.Triangles.Add(face[0]); m.Triangles.Add(face[i]); m.Triangles.Add(face[i + 1]);
                    }
                }
            }
            m.Positions = verts;
            ComputeNormals(m);
            m.Bounds = ComputeBounds(m.Positions);
            m.Info = "OBJ";
            return m;
        }

        private static string DescribeRsc(byte[] data)
        {
            if (data == null || data.Length < 16) return "RSC resource";
            uint magic = BitConverter.ToUInt32(data, 0);
            string tag = Encoding.ASCII.GetString(data, 0, 4);
            if (magic == 0x37435352) return "RSC7 resource (" + data.Length + " bytes)";
            if (magic == 0x38435352) return "RSC8 resource (" + data.Length + " bytes)";
            if (magic == 0x39435352) return "RSC9 resource (" + data.Length + " bytes). Drawable/model decode not fully implemented; raw resource export available.";
            return "RSC-like resource " + tag;
        }

        private static void ComputeNormals(SimpleModel m)
        {
            var normals = new System.Windows.Media.Media3D.Vector3D[m.Positions.Count];
            for (int i = 0; i + 2 < m.Triangles.Count; i += 3)
            {
                int a = m.Triangles[i], b = m.Triangles[i + 1], c = m.Triangles[i + 2];
                if (a < 0 || b < 0 || c < 0 || a >= m.Positions.Count || b >= m.Positions.Count || c >= m.Positions.Count) continue;
                var pa = m.Positions[a]; var pb = m.Positions[b]; var pc = m.Positions[c];
                var n = System.Windows.Media.Media3D.Vector3D.CrossProduct(pb - pa, pc - pa);
                normals[a] += n; normals[b] += n; normals[c] += n;
            }
            m.Normals.Clear();
            for (int i = 0; i < normals.Length; i++)
            {
                var n = normals[i];
                if (n.LengthSquared > 0.0000001) n.Normalize(); else n = new System.Windows.Media.Media3D.Vector3D(0, 1, 0);
                m.Normals.Add(n);
            }
        }

        private static System.Windows.Media.Media3D.Rect3D ComputeBounds(List<System.Windows.Media.Media3D.Point3D> pts)
        {
            if (pts == null || pts.Count == 0) return new System.Windows.Media.Media3D.Rect3D(0, 0, 0, 0, 0, 0);
            double minX = pts[0].X, minY = pts[0].Y, minZ = pts[0].Z, maxX = minX, maxY = minY, maxZ = minZ;
            foreach (var p in pts) { if (p.X < minX) minX = p.X; if (p.Y < minY) minY = p.Y; if (p.Z < minZ) minZ = p.Z; if (p.X > maxX) maxX = p.X; if (p.Y > maxY) maxY = p.Y; if (p.Z > maxZ) maxZ = p.Z; }
            return new System.Windows.Media.Media3D.Rect3D(minX, minY, minZ, Math.Max(0.0001, maxX - minX), Math.Max(0.0001, maxY - minY), Math.Max(0.0001, maxZ - minZ));
        }
    }

        internal static class RawImageDecoder
    {
        public static Bitmap Decode(byte[] data, int offset, int w, int h, string fmt, out string info)
        {
            info = "";
            if (data == null || w <= 0 || h <= 0 || w > 8192 || h > 8192) return null;
            fmt = (fmt ?? "Auto").ToUpperInvariant();
            if (fmt == "AUTO") fmt = "RGBA8";
            byte[] rgba;
            if (fmt == "RGBA8") rgba = CopyRgba(data, offset, w, h, true);
            else if (fmt == "BGRA8") rgba = CopyRgba(data, offset, w, h, false);
            else if (fmt == "RGB8") rgba = CopyRgb24(data, offset, w, h);
            else if (fmt == "DXT1") rgba = DdsDecoder.DecodeBc1Public(data, offset, w, h, true);
            else if (fmt == "DXT5") rgba = DdsDecoder.DecodeBc3Public(data, offset, w, h);
            else if (fmt == "BC7") rgba = DdsDecoder.DecodeBc7Public(data, offset, w, h);
            else if (fmt == "BC6H") rgba = BcDecoders.DecodeBc6h(data, offset, w, h, false);
            else if (fmt == "BC6H-S") rgba = BcDecoders.DecodeBc6h(data, offset, w, h, true);
            else return null;
            if (rgba == null) return null;
            Bitmap bmp = new Bitmap(w, h, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            var rect = new Rectangle(0, 0, w, h);
            var bd = bmp.LockBits(rect, System.Drawing.Imaging.ImageLockMode.WriteOnly, bmp.PixelFormat);
            try { Marshal.Copy(rgba, 0, bd.Scan0, Math.Min(rgba.Length, bd.Stride * h)); } finally { bmp.UnlockBits(bd); }
            info = w + "x" + h + " " + fmt;
            return bmp;
        }
        public static Bitmap DecodeGray(byte[] data, int offset, int w, int h, out string info)
        {
            info = "";
            int n = w*h;
            if (data == null || offset + n > data.Length) return null;
            byte[] rgba = new byte[n*4];
            for (int i=0;i<n;i++){ byte v=data[offset+i]; int o=i*4; rgba[o]=v; rgba[o+1]=v; rgba[o+2]=v; rgba[o+3]=255; }
            Bitmap bmp = new Bitmap(w, h, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            var rect = new Rectangle(0, 0, w, h);
            var bd = bmp.LockBits(rect, System.Drawing.Imaging.ImageLockMode.WriteOnly, bmp.PixelFormat);
            try { Marshal.Copy(rgba, 0, bd.Scan0, rgba.Length); } finally { bmp.UnlockBits(bd); }
            info = w+"x"+h+" GRAY8";
            return bmp;
        }
        private static byte[] CopyRgba(byte[] data, int offset, int w, int h, bool rgbaOrder)
        {
            int n=w*h*4; if (offset+n>data.Length) return null;
            byte[] bgra=new byte[n];
            for (int i=0;i<w*h;i++){ int si=offset+i*4, di=i*4; byte r=data[si], g=data[si+1], b=data[si+2], a=data[si+3]; if(!rgbaOrder){ b=data[si]; g=data[si+1]; r=data[si+2]; a=data[si+3]; } bgra[di]=b; bgra[di+1]=g; bgra[di+2]=r; bgra[di+3]=a; }
            return bgra;
        }
        private static byte[] CopyRgb24(byte[] data, int offset, int w, int h)
        {
            int n=w*h*3; if (offset+n>data.Length) return null;
            byte[] bgra=new byte[w*h*4];
            for (int i=0;i<w*h;i++){ int si=offset+i*3, di=i*4; bgra[di]=data[si+2]; bgra[di+1]=data[si+1]; bgra[di+2]=data[si]; bgra[di+3]=255; }
            return bgra;
        }
    }
internal static class DdsDecoder
    {
        public static Bitmap DecodeToBitmap(byte[] data, out string info)
        {
            info = "";
            if (data == null || data.Length < 128) throw new InvalidDataException("DDS too small.");
            if (data[0] != 'D' || data[1] != 'D' || data[2] != 'S' || data[3] != ' ') throw new InvalidDataException("Not DDS.");
            int headerSize = BitConverter.ToInt32(data, 4);
            int flags = BitConverter.ToInt32(data, 8);
            int height = BitConverter.ToInt32(data, 12);
            int width = BitConverter.ToInt32(data, 16);
            int pitchOrLinear = BitConverter.ToInt32(data, 20);
            int mipMapCount = BitConverter.ToInt32(data, 28);
            int pfSize = BitConverter.ToInt32(data, 76);
            int pfFlags = BitConverter.ToInt32(data, 80);
            string fourCC = Encoding.ASCII.GetString(data, 84, 4);
            int rgbBitCount = BitConverter.ToInt32(data, 88);
            uint rMask = BitConverter.ToUInt32(data, 92);
            uint gMask = BitConverter.ToUInt32(data, 96);
            uint bMask = BitConverter.ToUInt32(data, 100);
            uint aMask = BitConverter.ToUInt32(data, 104);
            int offset = 128;
            string format = fourCC;
            bool dx10 = fourCC == "DX10";
            int dxgi = 0;
            if (dx10)
            {
                if (data.Length < 148) throw new InvalidDataException("DX10 DDS too small.");
                dxgi = BitConverter.ToInt32(data, 128);
                offset = 148;
                format = "DX10/" + dxgi;
            }
            if (width <= 0 || height <= 0 || width > 16384 || height > 16384) throw new InvalidDataException("DDS dimensions unsupported.");
            byte[] rgba = DecodePixels(data, offset, width, height, fourCC, dxgi, pfFlags, rgbBitCount, rMask, gMask, bMask, aMask);
            var bmp = new Bitmap(width, height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            var rect = new Rectangle(0, 0, width, height);
            var bd = bmp.LockBits(rect, System.Drawing.Imaging.ImageLockMode.WriteOnly, bmp.PixelFormat);
            try { Marshal.Copy(rgba, 0, bd.Scan0, Math.Min(rgba.Length, bd.Stride * height)); }
            finally { bmp.UnlockBits(bd); }
            info = width + "x" + height + " " + format + (mipMapCount > 1 ? " mips=" + mipMapCount : "");
            return bmp;
        }

        private static byte[] DecodePixels(byte[] data, int offset, int width, int height, string fourCC, int dxgi, int pfFlags, int rgbBitCount, uint rMask, uint gMask, uint bMask, uint aMask)
        {
            bool alpha = (pfFlags & 0x1) != 0;
            bool rgb = (pfFlags & 0x40) != 0;
            bool four = (pfFlags & 0x4) != 0;
            if (four)
            {
                string f = fourCC.TrimEnd('\0', ' ');
                if (f == "DXT1") return DecodeBc1Public(data, offset, width, height, true);
                if (f == "DXT3") return DecodeBc2(data, offset, width, height);
                if (f == "DXT5") return DecodeBc3Public(data, offset, width, height);
                if (f == "ATI1" || f == "BC4U") return DecodeBc4(data, offset, width, height, false);
                if (f == "BC4S") return DecodeBc4(data, offset, width, height, true);
                if (f == "ATI2" || f == "BC5U") return DecodeBc5(data, offset, width, height, false);
                if (f == "BC5S") return DecodeBc5(data, offset, width, height, true);
                if (fourCC == "DX10")
                {
                    if (dxgi == 71 || dxgi == 72) return DecodeBc1Public(data, offset, width, height, true);
                    if (dxgi == 74 || dxgi == 75) return DecodeBc2(data, offset, width, height);
                    if (dxgi == 77 || dxgi == 78) return DecodeBc3Public(data, offset, width, height);
                    if (dxgi == 80) return DecodeBc4(data, offset, width, height, false);
                    if (dxgi == 81) return DecodeBc4(data, offset, width, height, true);
                    if (dxgi == 83) return DecodeBc5(data, offset, width, height, false);
                    if (dxgi == 84) return DecodeBc5(data, offset, width, height, true);
                    if (dxgi == 95) return BcDecoders.DecodeBc6h(data, offset, width, height, false);
                    if (dxgi == 96) return BcDecoders.DecodeBc6h(data, offset, width, height, true);
                    if (dxgi == 98 || dxgi == 99) return DecodeBc7Public(data, offset, width, height);
                    if (dxgi == 28 || dxgi == 29) return DecodeRgba8(data, offset, width, height);
                    if (dxgi == 87) return DecodeBgra8(data, offset, width, height);
                    if (dxgi == 88 || dxgi == 93) return DecodeBgrx8(data, offset, width, height);
                    if (dxgi == 10) return DecodeRgba16Float(data, offset, width, height);
                    if (dxgi == 11) return DecodeRgba16Unorm(data, offset, width, height);
                    if (dxgi == 24) return DecodeR10G10B10A2(data, offset, width, height);
                    if (dxgi == 34) return DecodeRg16Float(data, offset, width, height);
                    if (dxgi == 41) return DecodeR32Float(data, offset, width, height);
                    if (dxgi == 49) return DecodeRg8(data, offset, width, height);
                    if (dxgi == 54) return DecodeR16Float(data, offset, width, height);
                    if (dxgi == 56) return DecodeR16Unorm(data, offset, width, height);
                    if (dxgi == 61) return DecodeR8(data, offset, width, height);
                    if (dxgi == 65) return DecodeA8(data, offset, width, height);
                }
            }
            if (rgb || alpha)
            {
                if (rgbBitCount == 32) return DecodeMasked(data, offset, width, height, rMask, gMask, bMask, aMask, rgbBitCount);
                if (rgbBitCount == 24) return DecodeMasked(data, offset, width, height, rMask, gMask, bMask, 0, rgbBitCount);
            }
            throw new InvalidDataException("Unsupported DDS format " + fourCC + (fourCC=="DX10" ? "/" + dxgi : ""));
        }

        private static byte[] DecodeMasked(byte[] data, int offset, int width, int height, uint rMask, uint gMask, uint bMask, uint aMask, int bpp)
        {
            byte[] rgba = new byte[width * height * 4];
            int stride = bpp / 8;
            for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                int i = offset + (y * width + x) * stride;
                if (i + stride > data.Length) break;
                uint px = stride == 4 ? BitConverter.ToUInt32(data, i) : (uint)(data[i] | (data[i + 1] << 8) | (data[i + 2] << 16));
                byte r = Unmask(px, rMask), g = Unmask(px, gMask), b = Unmask(px, bMask), a = aMask == 0 ? (byte)255 : Unmask(px, aMask);
                int o = (y * width + x) * 4;
                rgba[o] = b; rgba[o + 1] = g; rgba[o + 2] = r; rgba[o + 3] = a;
            }
            return rgba;
        }

        private static byte[] DecodeRgba8(byte[] data, int offset, int width, int height) { return DecodeMasked(data, offset, width, height, 0xFF, 0xFF00, 0xFF0000, 0xFF000000, 32); }
        private static byte[] DecodeBgra8(byte[] data, int offset, int width, int height) { return DecodeMasked(data, offset, width, height, 0xFF0000, 0xFF00, 0xFF, 0xFF000000, 32); }
        private static byte[] DecodeBgrx8(byte[] data, int offset, int width, int height) { return DecodeMasked(data, offset, width, height, 0xFF0000, 0xFF00, 0xFF, 0, 32); }

        private static byte[] DecodeRgba16Float(byte[] data, int offset, int width, int height)
        {
            byte[] rgba = new byte[width * height * 4];
            for (int i = 0; i < width * height; i++)
            {
                int p = offset + i * 8;
                if (p + 8 > data.Length) break;
                byte r = Quantize(HalfToFloat((ushort)(data[p] | (data[p + 1] << 8))));
                byte g = Quantize(HalfToFloat((ushort)(data[p + 2] | (data[p + 3] << 8))));
                byte b = Quantize(HalfToFloat((ushort)(data[p + 4] | (data[p + 5] << 8))));
                byte a = Quantize(HalfToFloat((ushort)(data[p + 6] | (data[p + 7] << 8))));
                int o = i * 4; rgba[o] = b; rgba[o + 1] = g; rgba[o + 2] = r; rgba[o + 3] = a;
            }
            return rgba;
        }

        private static byte[] DecodeRgba16Unorm(byte[] data, int offset, int width, int height)
        {
            byte[] rgba = new byte[width * height * 4];
            for (int i = 0; i < width * height; i++)
            {
                int p = offset + i * 8;
                if (p + 8 > data.Length) break;
                byte r = (byte)(data[p + 1]), g = (byte)(data[p + 3]), b = (byte)(data[p + 5]), a = (byte)(data[p + 7]);
                int o = i * 4; rgba[o] = b; rgba[o + 1] = g; rgba[o + 2] = r; rgba[o + 3] = a;
            }
            return rgba;
        }

        private static byte[] DecodeR10G10B10A2(byte[] data, int offset, int width, int height)
        {
            byte[] rgba = new byte[width * height * 4];
            for (int i = 0; i < width * height; i++)
            {
                int p = offset + i * 4;
                if (p + 4 > data.Length) break;
                uint v = BitConverter.ToUInt32(data, p);
                byte r = (byte)(((v & 0x3FF) * 255) / 1023);
                byte g = (byte)((((v >> 10) & 0x3FF) * 255) / 1023);
                byte b = (byte)((((v >> 20) & 0x3FF) * 255) / 1023);
                byte a = (byte)((((v >> 30) & 0x3) * 255) / 3);
                int o = i * 4; rgba[o] = b; rgba[o + 1] = g; rgba[o + 2] = r; rgba[o + 3] = a;
            }
            return rgba;
        }

        private static byte[] DecodeRg16Float(byte[] data, int offset, int width, int height)
        {
            byte[] rgba = new byte[width * height * 4];
            for (int i = 0; i < width * height; i++)
            {
                int p = offset + i * 4;
                if (p + 4 > data.Length) break;
                byte r = Quantize(HalfToFloat((ushort)(data[p] | (data[p + 1] << 8))));
                byte g = Quantize(HalfToFloat((ushort)(data[p + 2] | (data[p + 3] << 8))));
                int o = i * 4; rgba[o] = 0; rgba[o + 1] = g; rgba[o + 2] = r; rgba[o + 3] = 255;
            }
            return rgba;
        }

        private static byte[] DecodeR32Float(byte[] data, int offset, int width, int height)
        {
            byte[] rgba = new byte[width * height * 4];
            for (int i = 0; i < width * height; i++)
            {
                int p = offset + i * 4;
                if (p + 4 > data.Length) break;
                byte v = Quantize(BitConverter.ToSingle(data, p));
                int o = i * 4; rgba[o] = v; rgba[o + 1] = v; rgba[o + 2] = v; rgba[o + 3] = 255;
            }
            return rgba;
        }

        private static byte[] DecodeRg8(byte[] data, int offset, int width, int height)
        {
            byte[] rgba = new byte[width * height * 4];
            for (int i = 0; i < width * height; i++)
            {
                int p = offset + i * 2;
                if (p + 2 > data.Length) break;
                int o = i * 4; rgba[o] = 0; rgba[o + 1] = data[p + 1]; rgba[o + 2] = data[p]; rgba[o + 3] = 255;
            }
            return rgba;
        }

        private static byte[] DecodeR16Float(byte[] data, int offset, int width, int height)
        {
            byte[] rgba = new byte[width * height * 4];
            for (int i = 0; i < width * height; i++)
            {
                int p = offset + i * 2;
                if (p + 2 > data.Length) break;
                byte v = Quantize(HalfToFloat((ushort)(data[p] | (data[p + 1] << 8))));
                int o = i * 4; rgba[o] = v; rgba[o + 1] = v; rgba[o + 2] = v; rgba[o + 3] = 255;
            }
            return rgba;
        }

        private static byte[] DecodeR16Unorm(byte[] data, int offset, int width, int height)
        {
            byte[] rgba = new byte[width * height * 4];
            for (int i = 0; i < width * height; i++)
            {
                int p = offset + i * 2;
                if (p + 2 > data.Length) break;
                byte v = data[p + 1];
                int o = i * 4; rgba[o] = v; rgba[o + 1] = v; rgba[o + 2] = v; rgba[o + 3] = 255;
            }
            return rgba;
        }

        private static byte[] DecodeR8(byte[] data, int offset, int width, int height)
        {
            byte[] rgba = new byte[width * height * 4];
            for (int i = 0; i < width * height; i++)
            {
                int p = offset + i;
                if (p >= data.Length) break;
                byte v = data[p];
                int o = i * 4; rgba[o] = v; rgba[o + 1] = v; rgba[o + 2] = v; rgba[o + 3] = 255;
            }
            return rgba;
        }

        private static byte[] DecodeA8(byte[] data, int offset, int width, int height)
        {
            byte[] rgba = new byte[width * height * 4];
            for (int i = 0; i < width * height; i++)
            {
                int p = offset + i;
                if (p >= data.Length) break;
                byte v = data[p];
                int o = i * 4; rgba[o] = v; rgba[o + 1] = v; rgba[o + 2] = v; rgba[o + 3] = 255;
            }
            return rgba;
        }

        private static float HalfToFloat(ushort h)
        {
            int sign = (h >> 15) & 1;
            int exp = (h >> 10) & 0x1F;
            int mant = h & 0x3FF;
            double value;
            if (exp == 0) value = mant * Math.Pow(2.0, -24.0);
            else if (exp == 31) value = mant == 0 ? double.PositiveInfinity : double.NaN;
            else value = (1.0 + mant / 1024.0) * Math.Pow(2.0, exp - 15);
            return (float)(sign == 1 ? -value : value);
        }

        private static byte Quantize(double v)
        {
            if (double.IsNaN(v) || v <= 0) return 0;
            if (v >= 1) return 255;
            return (byte)Math.Round(v * 255.0);
        }

        private static byte Unmask(uint px, uint mask)
        {
            if (mask == 0) return 255;
            uint v = px & mask;
            int shift = 0;
            while ((mask & 1) == 0) { mask >>= 1; v >>= 1; shift++; }
            int bits = 0; uint m = mask;
            while (m != 0) { bits++; m >>= 1; }
            if (bits == 0) return 255;
            return (byte)(v * 255 / ((1u << bits) - 1));
        }

        private static void DecodeColorBlock(byte[] data, int offset, byte[] colors, bool dxt1Alpha)
        {
            ushort c0 = BitConverter.ToUInt16(data, offset);
            ushort c1 = BitConverter.ToUInt16(data, offset + 2);
            uint table = BitConverter.ToUInt32(data, offset + 4);
            Color565(c0, out colors[0], out colors[1], out colors[2]); colors[3] = 255;
            Color565(c1, out colors[4], out colors[5], out colors[6]); colors[7] = 255;
            if (c0 > c1 || !dxt1Alpha)
            {
                colors[8] = (byte)((2 * colors[0] + colors[4]) / 3); colors[9] = (byte)((2 * colors[1] + colors[5]) / 3); colors[10] = (byte)((2 * colors[2] + colors[6]) / 3); colors[11] = 255;
                colors[12] = (byte)((colors[0] + 2 * colors[4]) / 3); colors[13] = (byte)((colors[1] + 2 * colors[5]) / 3); colors[14] = (byte)((colors[2] + 2 * colors[6]) / 3); colors[15] = 255;
            }
            else
            {
                colors[8] = (byte)((colors[0] + colors[4]) / 2); colors[9] = (byte)((colors[1] + colors[5]) / 2); colors[10] = (byte)((colors[2] + colors[6]) / 2); colors[11] = 255;
                colors[12] = 0; colors[13] = 0; colors[14] = 0; colors[15] = 0;
            }
        }
        private static void Color565(ushort c, out byte r, out byte g, out byte b)
        {
            r = (byte)(((c >> 11) & 31) * 255 / 31);
            g = (byte)(((c >> 5) & 63) * 255 / 63);
            b = (byte)((c & 31) * 255 / 31);
        }
        public static byte[] DecodeBc1Public(byte[] data, int offset, int w, int h, bool dxt1Alpha)
        {
            byte[] rgba = new byte[w * h * 4]; byte[] c = new byte[16];
            int p = offset;
            for (int by = 0; by < h; by += 4)
            for (int bx = 0; bx < w; bx += 4)
            {
                if (p + 8 > data.Length) return rgba;
                DecodeColorBlock(data, p, c, dxt1Alpha); uint table = BitConverter.ToUInt32(data, p + 4); p += 8;
                EmitBlock(rgba, w, h, bx, by, c, table, null);
            }
            return rgba;
        }
        private static byte[] DecodeBc2(byte[] data, int offset, int w, int h)
        {
            byte[] rgba = new byte[w * h * 4]; byte[] c = new byte[16];
            int p = offset;
            for (int by = 0; by < h; by += 4)
            for (int bx = 0; bx < w; bx += 4)
            {
                if (p + 16 > data.Length) return rgba;
                byte[] alpha = new byte[16];
                for (int i = 0; i < 8; i++) { byte b = data[p + i]; alpha[i * 2] = (byte)((b & 0xF) * 17); alpha[i * 2 + 1] = (byte)(((b >> 4) & 0xF) * 17); }
                DecodeColorBlock(data, p + 8, c, false); uint table = BitConverter.ToUInt32(data, p + 12); p += 16;
                EmitBlock(rgba, w, h, bx, by, c, table, alpha);
            }
            return rgba;
        }
        public static byte[] DecodeBc3Public(byte[] data, int offset, int w, int h)
        {
            byte[] rgba = new byte[w * h * 4]; byte[] c = new byte[16];
            int p = offset;
            for (int by = 0; by < h; by += 4)
            for (int bx = 0; bx < w; bx += 4)
            {
                if (p + 16 > data.Length) return rgba;
                byte[] alpha = DecodeBcAlpha(data, p);
                DecodeColorBlock(data, p + 8, c, false); uint table = BitConverter.ToUInt32(data, p + 12); p += 16;
                EmitBlock(rgba, w, h, bx, by, c, table, alpha);
            }
            return rgba;
        }
        private static byte[] DecodeBc4(byte[] data, int offset, int w, int h, bool signed)
        {
            byte[] rgba = new byte[w * h * 4];
            int p = offset;
            for (int by = 0; by < h; by += 4)
            for (int bx = 0; bx < w; bx += 4)
            {
                if (p + 8 > data.Length) return rgba;
                byte[] r = DecodeBcAlpha(data, p); p += 8;
                for (int i = 0; i < 16; i++) { int px = bx + i % 4, py = by + i / 4; if (px >= w || py >= h) continue; int o = (py * w + px) * 4; rgba[o] = r[i]; rgba[o + 1] = r[i]; rgba[o + 2] = r[i]; rgba[o + 3] = 255; }
            }
            return rgba;
        }
        private static byte[] DecodeBc5(byte[] data, int offset, int w, int h, bool signed)
        {
            byte[] rgba = new byte[w * h * 4];
            int p = offset;
            for (int by = 0; by < h; by += 4)
            for (int bx = 0; bx < w; bx += 4)
            {
                if (p + 16 > data.Length) return rgba;
                byte[] r = DecodeBcAlpha(data, p); byte[] g = DecodeBcAlpha(data, p + 8); p += 16;
                for (int i = 0; i < 16; i++) { int px = bx + i % 4, py = by + i / 4; if (px >= w || py >= h) continue; int o = (py * w + px) * 4; rgba[o] = 255; rgba[o + 1] = g[i]; rgba[o + 2] = r[i]; rgba[o + 3] = 255; }
            }
            return rgba;
        }
        public static byte[] DecodeBc7Public(byte[] data, int offset, int w, int h)
        {
            return BcDecoders.DecodeBc7(data, offset, w, h);
        }
        private static byte[] DecodeBcAlpha(byte[] data, int offset)
        {
            byte a0 = data[offset], a1 = data[offset + 1];
            ulong bits = 0;
            for (int i = 0; i < 6; i++) bits |= ((ulong)data[offset + 2 + i]) << (8 * i);
            byte[] table = new byte[8];
            table[0] = a0; table[1] = a1;
            if (a0 > a1) { for (int i = 2; i < 8; i++) table[i] = (byte)(((8 - i) * a0 + (i - 1) * a1) / 7); }
            else { for (int i = 2; i < 6; i++) table[i] = (byte)(((6 - i) * a0 + (i - 1) * a1) / 5); table[6] = 0; table[7] = 255; }
            byte[] outA = new byte[16];
            for (int i = 0; i < 16; i++) outA[i] = table[(int)((bits >> (3 * i)) & 7)];
            return outA;
        }
        private static void EmitBlock(byte[] rgba, int w, int h, int bx, int by, byte[] c, uint table, byte[] alpha)
        {
            for (int i = 0; i < 16; i++)
            {
                int px = bx + i % 4, py = by + i / 4;
                if (px >= w || py >= h) continue;
                int ci = (int)((table >> (2 * i)) & 3) * 4;
                int o = (py * w + px) * 4;
                rgba[o] = c[ci + 2]; rgba[o + 1] = c[ci + 1]; rgba[o + 2] = c[ci]; rgba[o + 3] = alpha != null ? alpha[i] : c[ci + 3];
            }
        }
    }
}
