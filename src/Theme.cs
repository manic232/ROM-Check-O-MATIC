// ROM Check O-MATIC - light and dark themes.

using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace RomCheckOMatic
{
    class Palette
    {
        public bool Dark;
        public Color Back, Surface, Input, Text, Gray, Border, Button, Hover, Accent, MenuBack, MenuHover, Separator;
        public Color Good, Bad;   // soft green and red for the system list

        public static Palette DarkColours()
        {
            return new Palette
            {
                Dark = true,
                Back = Color.FromArgb(32, 32, 32), Surface = Color.FromArgb(25, 25, 25), Input = Color.FromArgb(45, 45, 45),
                Text = Color.FromArgb(235, 235, 235), Gray = Color.FromArgb(150, 150, 150), Border = Color.FromArgb(85, 85, 85),
                Button = Color.FromArgb(55, 55, 55), Hover = Color.FromArgb(72, 72, 72), Accent = Color.FromArgb(76, 160, 255),
                MenuBack = Color.FromArgb(43, 43, 43), MenuHover = Color.FromArgb(65, 65, 65), Separator = Color.FromArgb(75, 75, 75),
                Good = Color.FromArgb(125, 196, 140), Bad = Color.FromArgb(230, 130, 125)
            };
        }

        public static Palette LightColours()
        {
            return new Palette
            {
                Dark = false,
                Back = SystemColors.Control, Surface = SystemColors.Window, Input = SystemColors.Window,
                Text = SystemColors.ControlText, Gray = SystemColors.GrayText, Border = Color.FromArgb(200, 200, 200),
                Button = SystemColors.Control, Hover = Color.FromArgb(229, 241, 251), Accent = Color.FromArgb(0, 120, 215),
                MenuBack = Color.FromArgb(249, 249, 249), MenuHover = Color.FromArgb(232, 232, 232), Separator = Color.FromArgb(220, 220, 220),
                Good = Color.FromArgb(30, 120, 60), Bad = Color.FromArgb(180, 50, 45)
            };
        }
    }

    static class Theme
    {
        public static Palette Current = Palette.LightColours();

        [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
        [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)] static extern int SetWindowTheme(IntPtr hwnd, string app, string idList);
        [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr hwnd, int msg, IntPtr w, IntPtr l);
        const int LVM_GETHEADER = 0x101F;

        // Windows stores the "app mode" (light/dark) choice here. This only reads it; nothing is written.
        public static bool WindowsUsesDarkMode()
        {
            try
            {
                using (var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
                {
                    if (k == null) return false;
                    object v = k.GetValue("AppsUseLightTheme");
                    return v is int && (int)v == 0;
                }
            }
            catch { return false; }
        }

        // setting is "System", "Light" or "Dark".
        public static void Choose(string setting)
        {
            bool dark = setting == "Dark" || (setting != "Light" && WindowsUsesDarkMode());
            Current = dark ? Palette.DarkColours() : Palette.LightColours();
        }

        // Colours a window and everything in it.
        public static void Apply(Control root)
        {
            var form = root as Form;
            if (form != null) TitleBar(form);
            ApplyOne(root);
            foreach (Control c in root.Controls) Apply(c);
            if (root.ContextMenuStrip != null) Menu(root.ContextMenuStrip);
        }

        public static void Menu(ToolStrip menu)
        {
            menu.Renderer = new ThemedRenderer(Current);
        }

        static void WhenHandle(Control c, Action a)
        {
            if (c.IsHandleCreated) a();
            else
            {
                EventHandler h = null;
                h = (s, e) => { c.HandleCreated -= h; a(); };
                c.HandleCreated += h;
            }
        }

        static void TitleBar(Form f)
        {
            WhenHandle(f, () =>
            {
                int on = Current.Dark ? 1 : 0;
                if (DwmSetWindowAttribute(f.Handle, 20, ref on, 4) != 0) DwmSetWindowAttribute(f.Handle, 19, ref on, 4);
            });
        }

        // Windows draws list column headings in dim text in dark mode, so draw them ourselves then.
        static readonly System.Collections.Generic.HashSet<ListView> hooked = new System.Collections.Generic.HashSet<ListView>();
        static void Headers(ListView lv)
        {
            if (hooked.Add(lv))
            {
                // Rows are drawn here in both themes, so the column lines run all the way up through them.
                lv.DrawItem += (s, e) => { e.DrawDefault = false; };
                lv.DrawSubItem += (s, e) =>
                {
                    var p = Current;
                    bool selected = e.Item.Selected;
                    Color back = selected ? (p.Dark ? Color.FromArgb(0, 84, 153) : Color.FromArgb(204, 232, 255)) : lv.BackColor;
                    using (var b = new SolidBrush(back))
                        e.Graphics.FillRectangle(b, e.Bounds);
                    using (var line = new Pen(p.Dark ? Color.FromArgb(59, 59, 59) : Color.FromArgb(232, 232, 232)))
                        e.Graphics.DrawLine(line, e.Bounds.Right - 1, e.Bounds.Top, e.Bounds.Right - 1, e.Bounds.Bottom);
                    var col = lv.Columns[e.ColumnIndex];
                    var flags = TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix |
                                (col.TextAlign == HorizontalAlignment.Right ? TextFormatFlags.Right : TextFormatFlags.Left);
                    var r = new Rectangle(e.Bounds.X + 6, e.Bounds.Y, e.Bounds.Width - 12, e.Bounds.Height);
                    bool plain = e.Item.ForeColor == lv.ForeColor || e.Item.ForeColor == SystemColors.WindowText;
                    var colour = selected && p.Dark ? Color.White : (plain ? p.Text : e.Item.ForeColor);
                    TextRenderer.DrawText(e.Graphics, e.SubItem.Text, e.Item.Font ?? lv.Font, r, colour, flags);
                };
                // Owner-drawn rows don't always repaint fully when the selection moves.
                lv.MouseUp += (s, e) => lv.Invalidate();
                lv.KeyUp += (s, e) => lv.Invalidate();
                lv.DrawColumnHeader += (s, e) =>
                {
                    var p = Current;
                    if (!p.Dark) { e.DrawDefault = true; return; }
                    using (var back = new SolidBrush(p.Dark ? Color.FromArgb(40, 40, 40) : SystemColors.Control))
                        e.Graphics.FillRectangle(back, e.Bounds);
                    using (var line = new Pen(p.Dark ? Color.FromArgb(70, 70, 70) : SystemColors.ControlDark))
                    {
                        e.Graphics.DrawLine(line, e.Bounds.Right - 1, e.Bounds.Top + 4, e.Bounds.Right - 1, e.Bounds.Bottom - 5);
                        e.Graphics.DrawLine(line, e.Bounds.Left, e.Bounds.Bottom - 1, e.Bounds.Right, e.Bounds.Bottom - 1);
                    }
                    var flags = TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine |
                                (e.Header.TextAlign == HorizontalAlignment.Right ? TextFormatFlags.Right : TextFormatFlags.Left);
                    var r = new Rectangle(e.Bounds.X + 6, e.Bounds.Y, e.Bounds.Width - 12, e.Bounds.Height);
                    TextRenderer.DrawText(e.Graphics, e.Header.Text, lv.Font, r, p.Text, flags);
                };
            }
            lv.OwnerDraw = true;
        }

        // Windows draws a greyed-out flat button's text almost invisibly on a dark background,
        // so in dark mode draw it ourselves: still clearly greyed out, but readable.
        static readonly System.Collections.Generic.HashSet<Button> dimmed = new System.Collections.Generic.HashSet<Button>();
        static void DimButton(Button b)
        {
            if (!dimmed.Add(b)) return;
            b.EnabledChanged += (s, e) => b.Invalidate();
            b.Paint += (s, e) =>
            {
                if (b.Enabled || !Current.Dark) return;
                var g = e.Graphics;
                using (var back = new SolidBrush(Color.FromArgb(42, 42, 42))) g.FillRectangle(back, b.ClientRectangle);
                using (var pen = new Pen(Color.FromArgb(68, 68, 68))) g.DrawRectangle(pen, 0, 0, b.Width - 1, b.Height - 1);
                TextRenderer.DrawText(g, b.Text, b.Font, b.ClientRectangle, Color.FromArgb(135, 135, 135),
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
            };
        }

        // One tick box, in the current theme. Used by the dark-mode tick boxes and the region list.
        public static void DrawTick(Graphics g, Rectangle box, bool ticked, bool hover, bool enabled, float scale)
        {
            var p = Current;
            int size = box.Width;
            var old = g.SmoothingMode;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var path = RoundRect(box, Math.Max(2, (int)(3 * scale))))
            {
                if (ticked)
                {
                    Color fill = !enabled ? p.Border
                               : p.Dark ? (hover ? Color.FromArgb(110, 180, 255) : p.Accent)
                               : (hover ? Color.FromArgb(0, 100, 190) : p.Accent);
                    using (var b = new SolidBrush(fill)) g.FillPath(b, path);
                    using (var pen = new Pen(p.Dark ? Color.FromArgb(20, 20, 20) : Color.White, Math.Max(1.8f, 2f * scale)) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
                        g.DrawLines(pen, new[] {
                            new PointF(box.Left + size * 0.24f, box.Top + size * 0.52f),
                            new PointF(box.Left + size * 0.43f, box.Top + size * 0.70f),
                            new PointF(box.Left + size * 0.77f, box.Top + size * 0.32f) });
                }
                else
                {
                    using (var b = new SolidBrush(p.Dark ? p.Input : Color.White)) g.FillPath(b, path);
                    Color edge = p.Dark ? (hover ? Color.FromArgb(200, 200, 200) : Color.FromArgb(160, 160, 160))
                                        : (hover ? Color.FromArgb(60, 60, 60) : Color.FromArgb(110, 110, 110));
                    using (var pen = new Pen(edge, Math.Max(1f, scale))) g.DrawPath(pen, path);
                }
            }
            g.SmoothingMode = old;
        }

        // Windows draws tick boxes grey on grey in dark mode, so draw them ourselves then:
        // an outlined box when unticked, a blue box with a white tick when ticked.
        static readonly System.Collections.Generic.HashSet<CheckBox> ticks = new System.Collections.Generic.HashSet<CheckBox>();
        static void DarkTick(CheckBox b)
        {
            if (!ticks.Add(b)) return;
            b.MouseEnter += (s, e) => b.Invalidate();
            b.MouseLeave += (s, e) => b.Invalidate();
            b.Paint += (s, e) =>
            {
                var p = Current;
                if (!p.Dark) return;
                var g = e.Graphics;
                g.Clear(b.BackColor);
                float scale = g.DpiX / 96f;
                int size = (int)Math.Round(14 * scale);
                var box = new Rectangle(1, (b.ClientSize.Height - size) / 2, size, size);
                bool hover = b.Enabled && b.ClientRectangle.Contains(b.PointToClient(Cursor.Position));
                DrawTick(g, box, b.Checked, hover, b.Enabled, scale);
                var text = new Rectangle(box.Right + (int)(5 * scale), 0, b.ClientSize.Width - box.Right - (int)(5 * scale), b.ClientSize.Height);
                TextRenderer.DrawText(g, b.Text, b.Font, text, b.Enabled ? p.Text : p.Gray,
                    TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);
            };
        }

        static GraphicsPath RoundRect(Rectangle r, int radius)
        {
            int d = radius * 2;
            var path = new GraphicsPath();
            path.AddArc(r.Left, r.Top, d, d, 180, 90);
            path.AddArc(r.Right - d, r.Top, d, d, 270, 90);
            path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            path.AddArc(r.Left, r.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }

        static void ApplyOne(Control c)
        {
            var p = Current;
            if (c is TabStrip) { ((TabStrip)c).ApplyPalette(p); return; }
            if (c is ListView)
            {
                var lv = (ListView)c;
                lv.BackColor = p.Surface;
                lv.ForeColor = p.Text;
                Headers(lv);
                WhenHandle(lv, () =>
                {
                    SetWindowTheme(lv.Handle, p.Dark ? "DarkMode_Explorer" : "Explorer", null);
                    IntPtr header = SendMessage(lv.Handle, LVM_GETHEADER, IntPtr.Zero, IntPtr.Zero);
                    if (header != IntPtr.Zero) SetWindowTheme(header, p.Dark ? "DarkMode_ItemsView" : "ItemsView", null);
                    lv.Invalidate();
                });
            }
            else if (c is TextBox)
            {
                var t = (TextBox)c;
                t.BackColor = t.ReadOnly && !p.Dark ? SystemColors.Window : p.Input;
                t.ForeColor = p.Text;
                t.BorderStyle = p.Dark ? BorderStyle.FixedSingle : BorderStyle.Fixed3D;
                if (t.Multiline) WhenHandle(t, () => SetWindowTheme(t.Handle, p.Dark ? "DarkMode_Explorer" : "Explorer", null));
            }
            else if (c is ComboBox)
            {
                var cb = (ComboBox)c;
                cb.FlatStyle = p.Dark ? FlatStyle.Flat : FlatStyle.Standard;
                cb.BackColor = p.Dark ? p.Input : SystemColors.Window;
                cb.ForeColor = p.Text;
            }
            else if (c is ListBox)
            {
                var l = (ListBox)c;
                l.BackColor = p.Input;
                l.ForeColor = p.Text;
                l.BorderStyle = p.Dark ? BorderStyle.FixedSingle : BorderStyle.Fixed3D;
                WhenHandle(l, () => SetWindowTheme(l.Handle, p.Dark ? "DarkMode_Explorer" : "Explorer", null));
            }
            else if (c is Button)
            {
                var b = (Button)c;
                if (p.Dark)
                {
                    b.FlatStyle = FlatStyle.Flat;
                    b.BackColor = p.Button;
                    b.ForeColor = p.Text;
                    b.FlatAppearance.BorderColor = p.Border;
                    b.FlatAppearance.MouseOverBackColor = p.Hover;
                    b.FlatAppearance.MouseDownBackColor = p.Border;
                    DimButton(b);
                }
                else
                {
                    b.FlatStyle = FlatStyle.Standard;
                    b.BackColor = SystemColors.Control;
                    b.UseVisualStyleBackColor = true;
                    b.ForeColor = SystemColors.ControlText;
                }
            }
            else if (c is CheckBox)
            {
                var b = (CheckBox)c;
                b.FlatStyle = FlatStyle.Standard;
                b.ForeColor = p.Text;
                b.BackColor = p.Back;
                DarkTick(b);
                b.Invalidate();
            }
            else if (c is Label)
            {
                c.ForeColor = "gray".Equals(c.Tag) ? p.Gray : p.Text;
                c.BackColor = Color.Transparent;
            }
            else if (c is ProgressBar) { }
            else
            {
                c.BackColor = p.Back;
                c.ForeColor = p.Text;
            }
        }
    }

    // Tabs that can be coloured for the dark theme (the standard tab control can't).
    class TabStrip : FlowLayoutPanel
    {
        public event EventHandler SelectedChanged;
        readonly System.Collections.Generic.List<RadioButton> tabs = new System.Collections.Generic.List<RadioButton>();
        readonly System.Collections.Generic.List<Control> pages = new System.Collections.Generic.List<Control>();

        public TabStrip()
        {
            AutoSize = true;
            Dock = DockStyle.Top;
            WrapContents = false;
            Margin = new Padding(0);
            Padding = new Padding(0, 2, 0, 0);
        }

        public int SelectedIndex { get { return tabs.FindIndex(t => t.Checked); } }

        public RadioButton Add(Control page)
        {
            var tab = new RadioButton
            {
                Appearance = Appearance.Button, AutoSize = true, FlatStyle = FlatStyle.Flat, TextAlign = ContentAlignment.MiddleCenter,
                Margin = new Padding(0, 0, 2, 0), Padding = new Padding(10, 4, 10, 6), Checked = tabs.Count == 0
            };
            tab.FlatAppearance.BorderSize = 0;
            page.Visible = tab.Checked;
            tab.CheckedChanged += (s, e) =>
            {
                page.Visible = tab.Checked;
                if (tab.Checked && SelectedChanged != null) SelectedChanged(this, EventArgs.Empty);
                tab.Invalidate();
            };
            tab.Paint += (s, e) =>
            {
                if (!tab.Checked) return;
                using (var b = new SolidBrush(Theme.Current.Accent))
                    e.Graphics.FillRectangle(b, 0, tab.Height - 3, tab.Width, 3);
            };
            tabs.Add(tab);
            pages.Add(page);
            Controls.Add(tab);
            return tab;
        }

        public void ApplyPalette(Palette p)
        {
            BackColor = p.Back;
            foreach (var t in tabs)
            {
                t.BackColor = p.Back;
                t.ForeColor = p.Text;
                t.FlatAppearance.CheckedBackColor = p.Surface;
                t.FlatAppearance.MouseOverBackColor = p.Dark ? p.Hover : Color.FromArgb(225, 225, 225);
                t.FlatAppearance.MouseDownBackColor = p.Surface;
            }
        }
    }

    // Draws menus in a modern light or dark style (the same look as Pointer-O-MATIC).
    class ThemedRenderer : ToolStripProfessionalRenderer
    {
        readonly Palette p;

        public ThemedRenderer(Palette palette) : base(new Colours(palette)) { p = palette; RoundedEdges = false; }

        class Colours : ProfessionalColorTable
        {
            readonly Palette p;
            public Colours(Palette palette) { p = palette; UseSystemColors = false; }
            public override Color ToolStripDropDownBackground { get { return p.MenuBack; } }
            public override Color ImageMarginGradientBegin { get { return p.MenuBack; } }
            public override Color ImageMarginGradientMiddle { get { return p.MenuBack; } }
            public override Color ImageMarginGradientEnd { get { return p.MenuBack; } }
            public override Color MenuBorder { get { return p.Border; } }
            public override Color MenuItemBorder { get { return p.MenuHover; } }
            public override Color MenuItemSelected { get { return p.MenuHover; } }
            public override Color MenuItemSelectedGradientBegin { get { return p.MenuHover; } }
            public override Color MenuItemSelectedGradientEnd { get { return p.MenuHover; } }
            public override Color SeparatorDark { get { return p.Separator; } }
            public override Color SeparatorLight { get { return p.MenuBack; } }
            public override Color CheckBackground { get { return p.MenuBack; } }
            public override Color CheckSelectedBackground { get { return p.MenuHover; } }
            public override Color CheckPressedBackground { get { return p.MenuHover; } }
        }

        protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
        {
            if (!e.Item.Selected || !e.Item.Enabled) return;
            var r = new Rectangle(3, 1, e.Item.Width - 6, e.Item.Height - 2);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using (var path = RoundRect(r, 4))
            using (var b = new SolidBrush(p.MenuHover))
                e.Graphics.FillPath(b, path);
        }

        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            e.TextColor = e.Item.Enabled ? (p.Dark ? Color.White : Color.FromArgb(26, 26, 26)) : (p.Dark ? Color.FromArgb(120, 120, 120) : Color.FromArgb(160, 160, 160));
            base.OnRenderItemText(e);
        }

        protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e)
        {
            e.ArrowColor = p.Text;
            base.OnRenderArrow(e);
        }

        protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
        {
            int y = e.Item.Height / 2;
            using (var pen = new Pen(p.Separator))
                e.Graphics.DrawLine(pen, 8, y, e.Item.Width - 8, y);
        }

        // Draw our own tick so it shows up on a dark background.
        protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e)
        {
            var r = e.ImageRectangle;
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var pen = new Pen(p.Dark ? Color.White : Color.FromArgb(26, 26, 26), Math.Max(1.6f, r.Height / 9f)) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
            {
                g.DrawLines(pen, new[] {
                    new PointF(r.Left + r.Width * 0.20f, r.Top + r.Height * 0.52f),
                    new PointF(r.Left + r.Width * 0.42f, r.Top + r.Height * 0.74f),
                    new PointF(r.Left + r.Width * 0.80f, r.Top + r.Height * 0.28f) });
            }
        }

        static GraphicsPath RoundRect(Rectangle r, int radius)
        {
            int d = radius * 2;
            var path = new GraphicsPath();
            path.AddArc(r.Left, r.Top, d, d, 180, 90);
            path.AddArc(r.Right - d, r.Top, d, d, 270, 90);
            path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            path.AddArc(r.Left, r.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }
    }
}
