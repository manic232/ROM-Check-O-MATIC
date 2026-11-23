// ROM Check O-MATIC - watches the downloads folder, and a small note that pops up when games are added.

using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;

namespace RomCheckOMatic
{
    // Notices new files in a folder, and reports them once they've finished downloading:
    // the file has stopped growing for a couple of seconds and nothing else has it open.
    class DownloadWatcher : IDisposable
    {
        public readonly string Folder;
        public event Action<List<string>> Ready;

        class Pending { public long Size = -1; public DateTime Write; public int Stable; }

        readonly Control ui;
        readonly Func<bool> canProcess;
        readonly FileSystemWatcher fsw;
        readonly Timer timer;
        readonly Dictionary<string, Pending> pending = new Dictionary<string, Pending>(StringComparer.OrdinalIgnoreCase);

        public DownloadWatcher(Control ui, string folder, Func<bool> canProcess)
        {
            this.ui = ui;
            this.canProcess = canProcess;
            Folder = folder;
            fsw = new FileSystemWatcher(folder)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.Size | NotifyFilters.LastWrite,
                InternalBufferSize = 64 * 1024
            };
            fsw.Created += (s, e) => Add(e.FullPath);
            fsw.Changed += (s, e) => Add(e.FullPath);
            fsw.Renamed += (s, e) => Add(e.FullPath);          // browsers rename "x.zip.crdownload" to "x.zip" when done
            fsw.Error += (s, e) => AddExisting();               // too many changes at once: just look at everything
            fsw.EnableRaisingEvents = true;
            timer = new Timer { Interval = 1000 };
            timer.Tick += (s, e) => Check();
            timer.Start();
        }

        void Add(string path)
        {
            if (ui.IsDisposed || !ui.IsHandleCreated) return;
            ui.BeginInvoke((Action)(() =>
            {
                if (Intake.IsPartial(path) || Directory.Exists(path)) return;
                if (!pending.ContainsKey(path)) pending[path] = new Pending();
            }));
        }

        // Also pick up files that were already there.
        public void AddExisting()
        {
            try { foreach (var f in Files.Enumerate(Folder, "*")) Add(f); } catch { }
        }

        void Check()
        {
            if (pending.Count == 0) return;
            var ready = new List<string>();
            foreach (var kv in pending.ToList())
            {
                string path = kv.Key;
                var p = kv.Value;
                try
                {
                    if (!File.Exists(path)) { pending.Remove(path); continue; }
                    // Firefox keeps a ".part" file beside the real one until it's finished.
                    if (File.Exists(path + ".part") || File.Exists(path + ".crdownload")) { p.Stable = 0; continue; }
                    var fi = new FileInfo(path);
                    if (fi.Length > 0 && fi.Length == p.Size && fi.LastWriteTimeUtc == p.Write) p.Stable++;
                    else { p.Stable = 0; p.Size = fi.Length; p.Write = fi.LastWriteTimeUtc; }
                    if (p.Stable >= 2 && CanOpen(path)) ready.Add(path);
                }
                catch { }
            }
            if (ready.Count == 0 || !canProcess()) return;   // try again on the next tick
            foreach (var r in ready) pending.Remove(r);
            if (Ready != null) Ready(ready);
        }

        static bool CanOpen(string path)
        {
            try { using (File.Open(path, FileMode.Open, FileAccess.Read, FileShare.None)) return true; }
            catch { return false; }
        }

        public void Dispose()
        {
            timer.Stop();
            timer.Dispose();
            fsw.EnableRaisingEvents = false;
            fsw.Dispose();
        }
    }

    // A small note in the corner of the screen that disappears by itself. It never takes the focus.
    class Toast : Form
    {
        static readonly List<Toast> open = new List<Toast>();

        public static void Show(Form near, string title, string text)
        {
            var t = new Toast(title, text);
            var area = Screen.FromControl(near).WorkingArea;
            int y = area.Bottom - 12;
            foreach (var o in open) y = Math.Min(y, o.Top - 8);
            t.Location = new Point(area.Right - t.Width - 12, y - t.Height);
            open.Add(t);
            t.FormClosed += (s, e) => open.Remove(t);
            t.Show();
        }

        protected override bool ShowWithoutActivation { get { return true; } }

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ExStyle |= 0x08000000 | 0x80 | 0x8;   // no activate, tool window (no taskbar button), topmost
                return cp;
            }
        }

        Toast(string title, string text)
        {
            var p = Theme.Current;
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            ShowInTaskbar = false;
            TopMost = true;
            Font = new Font("Segoe UI", 9f);
            AutoScaleMode = AutoScaleMode.Dpi;
            BackColor = p.Dark ? Color.FromArgb(43, 43, 43) : Color.FromArgb(249, 249, 249);
            ForeColor = p.Text;
            Padding = new Padding(1);

            var body = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, Padding = new Padding(14, 10, 14, 12), BackColor = BackColor, AutoSize = true };
            var head = new Label { Text = title, AutoSize = true, Font = new Font(Font, FontStyle.Bold), ForeColor = p.Text, UseMnemonic = false, Margin = new Padding(0, 0, 0, 4) };
            var line = new Label { Text = text, AutoSize = true, MaximumSize = new Size(360, 0), ForeColor = p.Dark ? Color.FromArgb(200, 200, 200) : Color.FromArgb(70, 70, 70), UseMnemonic = false };
            body.Controls.Add(head);
            body.Controls.Add(line);
            Controls.Add(body);
            Size = new Size(390, body.PreferredSize.Height + 2);
            Paint += (s, e) => { using (var pen = new Pen(p.Border)) e.Graphics.DrawRectangle(pen, 0, 0, Width - 1, Height - 1); };

            EventHandler close = (s, e) => Close();
            Click += close; body.Click += close; head.Click += close; line.Click += close;
            var timer = new Timer { Interval = 7000 };
            timer.Tick += (s, e) => { timer.Stop(); Close(); };
            timer.Start();
            FormClosed += (s, e) => timer.Dispose();
        }
    }
}
