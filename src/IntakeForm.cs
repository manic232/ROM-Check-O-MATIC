// ROM Check O-MATIC - shows what was found in the downloads folder before anything is moved.

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace RomCheckOMatic
{
    class IntakeForm : Form
    {
        public bool RemoveDownloads { get { return removeBox.Checked; } }
        readonly CheckBox removeBox;

        public IntakeForm(string folder, List<IntakeFile> plan, bool removeDownloads)
        {
            Text = "Add Missing ROMs from Folder";
            Font = new Font("Segoe UI", 9f);
            AutoScaleMode = AutoScaleMode.Dpi;
            Size = new Size(1150, 600);
            MinimumSize = new Size(700, 400);
            StartPosition = FormStartPosition.CenterParent;
            ShowInTaskbar = false;
            ShowIcon = false;
            MinimizeBox = false;

            var games = Intake.Adds(plan);
            int systems = games.Select(m => m.System).Distinct().Count();

            var top = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 1, Padding = new Padding(10, 10, 10, 4) };
            top.Controls.Add(new Label { Text = "Folder: " + folder, AutoSize = true, Tag = "gray", UseMnemonic = false });
            var heading = new Label
            {
                AutoSize = true, Margin = new Padding(3, 8, 3, 3), Font = new Font(Font, FontStyle.Bold), UseMnemonic = false,
                Text = plan.Count == 0 ? "The folder is empty."
                     : games.Count == 0 ? "None of the " + plan.Count + " file" + (plan.Count == 1 ? " is a missing ROM." : "s are missing ROMs.")
                     : "Found " + games.Count + " missing ROM" + (games.Count == 1 ? "" : "s") + " for " + systems + " system" + (systems == 1 ? "" : "s") + "."
            };
            top.Controls.Add(heading);
            if (games.Count > 0)
                top.Controls.Add(new Label
                {
                    AutoSize = true, Tag = "gray", MaximumSize = new Size(1000, 0),
                    Text = "Each ROM is zipped with its DAT name, checked against the DAT, and put in its system folder."
                });

            var list = new BufferedListView { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, HideSelection = false };
            list.Columns.Add("Downloaded File", 420);
            list.Columns.Add("What Happens", 330);
            list.Columns.Add("ROM", 400);
            foreach (var f in plan.OrderBy(f => f.Matches.Any(m => m.Error == null) ? 0 : 1).ThenBy(f => f.Name, StringComparer.OrdinalIgnoreCase))
            {
                var good = f.Matches.Where(m => m.Error == null).ToList();
                if (good.Count == 0)
                {
                    var item = new ListViewItem(new[] { f.Name, f.Outcome, "" });
                    item.ForeColor = Theme.Current.Gray;
                    list.Items.Add(item);
                }
                else
                    for (int i = 0; i < good.Count; i++)
                        list.Items.Add(new ListViewItem(new[] { i == 0 ? f.Name : "", i == 0 ? f.Outcome : "Add to " + good[i].System.FolderName, good[i].Game.Name }));
            }

            var bottom = new TableLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, ColumnCount = 4, Padding = new Padding(8) };
            bottom.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            bottom.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            bottom.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            bottom.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            removeBox = new CheckBox { Text = "Remove Downloads Once Their ROMs Are Added", AutoSize = true, Checked = removeDownloads, Anchor = AnchorStyles.Left, Margin = new Padding(3, 7, 3, 3) };
            var add = new Button { Text = games.Count == 0 ? "Add" : "Add " + games.Count + " ROM" + (games.Count == 1 ? "" : "s"), AutoSize = true, MinimumSize = new Size(100, 30), Enabled = games.Count > 0, DialogResult = DialogResult.OK };
            var change = new Button { Text = "Change Folder...", AutoSize = true, MinimumSize = new Size(100, 30), DialogResult = DialogResult.Retry };
            var cancel = new Button { Text = games.Count == 0 ? "Close" : "Cancel", AutoSize = true, MinimumSize = new Size(100, 30), DialogResult = DialogResult.Cancel };
            Tips.Set(removeBox, "Delete each download once its ROMs are safely in your set.\nDownloads holding other files too are always kept.");
            Tips.Set(add, "Zips each ROM with its DAT name, checks it against the DAT,\nand puts it in its system folder.");
            Tips.Set(change, "Look in a different folder.");
            bottom.Controls.Add(removeBox, 0, 0);
            bottom.Controls.Add(change, 1, 0);
            bottom.Controls.Add(add, 2, 0);
            bottom.Controls.Add(cancel, 3, 0);

            Controls.Add(list);
            Controls.Add(top);
            Controls.Add(bottom);
            list.BringToFront();
            AcceptButton = games.Count > 0 ? (IButtonControl)add : cancel;
            CancelButton = cancel;
            Theme.Apply(this);
        }
    }
}
