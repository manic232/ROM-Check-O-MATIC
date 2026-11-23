// ROM Check O-MATIC - message boxes that follow the app's light or dark theme.

using System;
using System.Drawing;
using System.Media;
using System.Windows.Forms;

namespace RomCheckOMatic
{
    // Asks for a line of text, in the app's theme. Returns null if cancelled.
    class InputBox : Form
    {
        public static string Ask(IWin32Window owner, string title, string prompt, string value)
        {
            using (var f = new InputBox(title, prompt, value))
                return f.ShowDialog(owner) == DialogResult.OK ? f.box.Text : null;
        }

        readonly TextBox box;

        InputBox(string title, string prompt, string value)
        {
            Text = title;
            Font = new Font("Segoe UI", 9f);
            AutoScaleMode = AutoScaleMode.Dpi;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MinimizeBox = MaximizeBox = false;
            ShowInTaskbar = false;
            ShowIcon = false;
            StartPosition = FormStartPosition.CenterParent;
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;

            var layout = new TableLayoutPanel { AutoSize = true, ColumnCount = 1, Padding = new Padding(14), Dock = DockStyle.Fill };
            layout.Controls.Add(new Label { Text = prompt, AutoSize = true, MaximumSize = new Size(420, 0), Margin = new Padding(3, 0, 3, 8) });
            box = new TextBox { Text = value, Width = 420 };
            layout.Controls.Add(box);
            var buttons = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.RightToLeft, Dock = DockStyle.Fill, Margin = new Padding(0, 12, 0, 0) };
            var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, AutoSize = true, MinimumSize = new Size(88, 28) };
            var ok = new Button { Text = "OK", DialogResult = DialogResult.OK, AutoSize = true, MinimumSize = new Size(88, 28) };
            buttons.Controls.Add(cancel);
            buttons.Controls.Add(ok);
            layout.Controls.Add(buttons);
            Controls.Add(layout);
            AcceptButton = ok;
            CancelButton = cancel;
            Theme.Apply(this);
        }
    }

    // Works like MessageBox.Show, but drawn in the app's theme.
    class ThemedBox : Form
    {
        public static DialogResult Show(IWin32Window owner, string text, string caption)
        {
            return Show(owner, text, caption, MessageBoxButtons.OK, MessageBoxIcon.None, MessageBoxDefaultButton.Button1);
        }

        public static DialogResult Show(IWin32Window owner, string text, string caption, MessageBoxButtons buttons)
        {
            return Show(owner, text, caption, buttons, MessageBoxIcon.None, MessageBoxDefaultButton.Button1);
        }

        public static DialogResult Show(IWin32Window owner, string text, string caption, MessageBoxButtons buttons, MessageBoxIcon icon)
        {
            return Show(owner, text, caption, buttons, icon, MessageBoxDefaultButton.Button1);
        }

        public static DialogResult Show(IWin32Window owner, string text, string caption, MessageBoxButtons buttons, MessageBoxIcon icon, MessageBoxDefaultButton def)
        {
            using (var box = new ThemedBox(text, caption, buttons, icon, def))
            {
                switch (icon)
                {
                    case MessageBoxIcon.Warning: SystemSounds.Exclamation.Play(); break;
                    case MessageBoxIcon.Error: SystemSounds.Hand.Play(); break;
                    case MessageBoxIcon.Information: SystemSounds.Asterisk.Play(); break;
                }
                return owner != null ? box.ShowDialog(owner) : box.ShowDialog();
            }
        }

        ThemedBox(string text, string caption, MessageBoxButtons buttons, MessageBoxIcon icon, MessageBoxDefaultButton def)
        {
            Text = caption;
            Font = new Font("Segoe UI", 9f);
            AutoScaleMode = AutoScaleMode.Dpi;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MinimizeBox = MaximizeBox = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;
            MinimumSize = new Size(340, 0);

            var p = Theme.Current;
            var body = new TableLayoutPanel { AutoSize = true, ColumnCount = 2, Dock = DockStyle.Fill, Padding = new Padding(18, 20, 24, 20) };
            Icon sysIcon = icon == MessageBoxIcon.Question ? SystemIcons.Question
                         : icon == MessageBoxIcon.Warning ? SystemIcons.Warning
                         : icon == MessageBoxIcon.Error ? SystemIcons.Error
                         : icon == MessageBoxIcon.Information ? SystemIcons.Information : null;
            if (sysIcon != null)
            {
                var pic = new PictureBox { Image = sysIcon.ToBitmap(), SizeMode = PictureBoxSizeMode.AutoSize, Margin = new Padding(0, 0, 14, 0) };
                body.Controls.Add(pic, 0, 0);
            }
            var label = new Label { Text = text, AutoSize = true, MaximumSize = new Size(560, 0), Margin = new Padding(0, sysIcon != null ? 6 : 0, 0, 0), UseMnemonic = false };
            body.Controls.Add(label, 1, 0);

            // Buttons sit on a slightly different band along the bottom, like Windows' own message boxes.
            var band = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Bottom, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(10, 10, 10, 10), Tag = "band" };
            Button first = null, second = null;
            Func<string, DialogResult, Button> add = (t, r) =>
            {
                var b = new Button { Text = t, DialogResult = r, AutoSize = true, MinimumSize = new Size(88, 28), Margin = new Padding(6, 0, 0, 0) };
                return b;
            };
            switch (buttons)
            {
                case MessageBoxButtons.OKCancel: first = add("OK", DialogResult.OK); second = add("Cancel", DialogResult.Cancel); break;
                case MessageBoxButtons.YesNo: first = add("Yes", DialogResult.Yes); second = add("No", DialogResult.No); break;
                default: first = add("OK", DialogResult.OK); break;
            }
            if (second != null) band.Controls.Add(second);
            band.Controls.Add(first);

            Controls.Add(body);
            Controls.Add(band);
            Theme.Apply(this);
            band.BackColor = p.Dark ? Color.FromArgb(43, 43, 43) : Color.FromArgb(240, 240, 240);
            BackColor = body.BackColor = p.Dark ? p.Back : SystemColors.Window;
            foreach (Control c in body.Controls) if (c is PictureBox) c.BackColor = Color.Transparent;

            var defaultButton = def == MessageBoxDefaultButton.Button2 && second != null ? second : first;
            AcceptButton = defaultButton;
            CancelButton = second ?? first;
            Shown += (s, e) => defaultButton.Focus();
        }
    }
}
