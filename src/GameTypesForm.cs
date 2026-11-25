// ROM Check O-MATIC - choose which kinds of game to keep.

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace RomCheckOMatic
{
    class GameTypesForm : Form
    {
        public GameType Exclude;
        public bool UseCollection;   // the system's own ROM types were removed: it uses the collection's again

        static string Example(GameType t)
        {
            switch (t)
            {
                case GameType.Beta: return "(Beta), (Beta 2), (Alpha), (RC1)";
                case GameType.Proto: return "(Proto), (Debug), unfinished builds";
                case GameType.Demo: return "(Demo), (Sample), (Kiosk), (Trial), (Promo)";
                case GameType.Unlicensed: return "(Unl)";
                case GameType.Pirate: return "(Pirate)";
                case GameType.Aftermarket: return "(Aftermarket)";
                case GameType.Bios: return "[BIOS] ...";
                case GameType.Program: return "(Program), (Test Program), applications";
                default: return "";
            }
        }

        // system: set the ROM types for this system only (own: it already has its own).
        public GameTypesForm(GameType exclude, string system = null, bool own = false)
        {
            Text = system == null ? "ROM Types" : "ROM Type Overrides for " + system;
            Font = new Font("Segoe UI", 9f);
            AutoScaleMode = AutoScaleMode.Dpi;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MinimizeBox = MaximizeBox = false;
            StartPosition = FormStartPosition.CenterParent;
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;

            var layout = new TableLayoutPanel { AutoSize = true, ColumnCount = 2, Padding = new Padding(12), Dock = DockStyle.Fill };
            var heading = new Label
            {
                AutoSize = true, MaximumSize = new Size(520, 0), Margin = new Padding(3, 0, 3, 10),
                Text = (system == null ? "" : "These ROM types are for " + system + " only. Other systems keep the collection's ROM types.\n\n") +
                       "Keep these kinds of ROMs. ROMs you untick are listed as unneeded, and don't count as missing."
            };
            layout.Controls.Add(heading, 0, 0);
            layout.SetColumnSpan(heading, 2);

            var boxes = new Dictionary<GameType, CheckBox>();
            int row = 1;
            foreach (var t in GameTypes.All)
            {
                var box = new CheckBox { Text = GameTypes.Label(t), AutoSize = true, Checked = (exclude & t) == 0, Margin = new Padding(3, 3, 12, 3) };
                var example = new Label { Text = Example(t), AutoSize = true, Tag = "gray", Margin = new Padding(3, 7, 3, 3) };
                boxes[t] = box;
                string what = GameTypes.Label(t);
                if (what.Length > 1 && char.IsLower(what[1])) what = char.ToLower(what[0]) + what.Substring(1);   // "BIOS" stays as it is
                Tips.Set(box, "Ticked: keep " + what + ".\nUnticked: they're listed as unneeded, and don't count as missing.");
                layout.Controls.Add(box, 0, row);
                layout.Controls.Add(example, 1, row);
                row++;
            }

            var note = new Label
            {
                AutoSize = true, MaximumSize = new Size(520, 0), Tag = "gray", Margin = new Padding(3, 10, 3, 6),
                Text = "With \"Use 1G1R\" on, kept betas, prototypes and demos are still only used when a title has no full release."
            };
            layout.Controls.Add(note, 0, row++);
            layout.SetColumnSpan(note, 2);

            var buttons = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.RightToLeft, Dock = DockStyle.Fill, Margin = new Padding(0, 6, 0, 0) };
            var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, AutoSize = true, MinimumSize = new Size(80, 0) };
            var ok = new Button { Text = "OK", AutoSize = true, MinimumSize = new Size(80, 0) };
            var all = new Button { Text = "Keep All", AutoSize = true, MinimumSize = new Size(80, 0) };
            var gamesOnly = new Button { Text = "Finished ROMs Only", AutoSize = true, MinimumSize = new Size(80, 0) };
            Tips.Set(all, "Ticks every kind, so nothing is left out.");
            Tips.Set(gamesOnly, "Unticks everything except finished ROMs: no betas, prototypes, demos,\nunlicensed, pirate or aftermarket ROMs, BIOS files or applications.");
            ok.Click += (s, e) =>
            {
                Exclude = GameType.None;
                foreach (var kv in boxes) if (!kv.Value.Checked) Exclude |= kv.Key;
                DialogResult = DialogResult.OK;
            };
            all.Click += (s, e) => { foreach (var b in boxes.Values) b.Checked = true; };
            gamesOnly.Click += (s, e) =>
            {
                // Finished, licensed games: leave out everything except unlicensed and aftermarket releases.
                foreach (var kv in boxes) kv.Value.Checked = kv.Key == GameType.Unlicensed || kv.Key == GameType.Aftermarket;
            };
            buttons.Controls.Add(cancel);
            buttons.Controls.Add(ok);
            buttons.Controls.Add(all);
            buttons.Controls.Add(gamesOnly);
            if (own)
            {
                var shared = new Button { Text = "Use the Collection's ROM Types", AutoSize = true, MinimumSize = new Size(80, 0) };
                shared.Click += (s, e) => { UseCollection = true; DialogResult = DialogResult.OK; };
                Tips.Set(shared, "Removes this system's own ROM types, so it uses the same ones as the rest of the collection.");
                buttons.Controls.Add(shared);
            }
            layout.Controls.Add(buttons, 0, row);
            layout.SetColumnSpan(buttons, 2);

            Controls.Add(layout);
            AcceptButton = ok;
            CancelButton = cancel;
            Theme.Apply(this);
        }
    }
}
