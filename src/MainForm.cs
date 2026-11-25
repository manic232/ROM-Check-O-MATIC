// ROM Check O-MATIC - the main window.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace RomCheckOMatic
{
    class Row
    {
        public string[] Cells;
        public FileResult File;
        public DatGame Game;
        public SystemResult System;
        public long Size = -1;   // for sorting the size column
    }

    // A fast list for tens of thousands of rows: only the visible rows are ever built.
    class ResultList : ListView
    {
        public List<Row> Rows = new List<Row>();
        int sortColumn = -1;
        bool sortDescending;
        public int SizeColumn = -1;

        public ResultList(params object[] columns)
        {
            View = View.Details;
            FullRowSelect = true;
            HideSelection = false;
            VirtualMode = true;
            DoubleBuffered = true;
            Dock = DockStyle.Fill;
            for (int i = 0; i < columns.Length; i += 2)
                Columns.Add((string)columns[i], (int)columns[i + 1]);
            RetrieveVirtualItem += (s, e) =>
            {
                var r = e.ItemIndex < Rows.Count ? Rows[e.ItemIndex] : null;
                e.Item = new ListViewItem(r == null ? new string[Columns.Count] : r.Cells);
            };
            ColumnClick += (s, e) =>
            {
                if (sortColumn == e.Column) sortDescending = !sortDescending;
                else { sortColumn = e.Column; sortDescending = false; }
                SetRows(Rows);
            };
        }

        public void SetRows(List<Row> rows)
        {
            SelectedIndices.Clear();
            if (sortColumn >= 0)
            {
                int c = sortColumn;
                IOrderedEnumerable<Row> sorted = c == SizeColumn
                    ? rows.OrderBy(r => r.Size)
                    : rows.OrderBy(r => r.Cells[c], StringComparer.CurrentCultureIgnoreCase);
                rows = sorted.ToList();
                if (sortDescending) rows.Reverse();
            }
            Rows = rows;
            VirtualListSize = rows.Count;
            Invalidate();
        }

        public List<Row> SelectedRows()
        {
            return SelectedIndices.Cast<int>().Where(i => i < Rows.Count).Select(i => Rows[i]).ToList();
        }
    }

    class MainForm : Form
    {
        // Where each kind of set's DATs are downloaded: the menu wording and the page.
        static Tuple<string, string> DownloadPageFor(string source)
        {
            switch (source)
            {
                case "No-Intro": return Tuple.Create("No-Intro DAT Download Page", "https://datomatic.no-intro.org/index.php?page=download&op=daily");
                case "Redump": return Tuple.Create("Redump DAT Download Page", "http://redump.org/downloads/");
                case "TOSEC": return Tuple.Create("TOSEC DAT Download Page", "https://www.tosecdev.org/downloads");
                case "MAME": return Tuple.Create("MAME DAT Download Page", "https://pleasuredome.github.io/pleasuredome/mame/index.html");
                default: return null;
            }
        }

        // Which site this collection's DATs come from, by the DATs themselves, or by the collection's name
        // before any DATs are found.
        string DatSource()
        {
            string source = index != null ? index.Source : null;
            if (source != null) return source;
            foreach (var name in new[] { "No-Intro", "Redump", "MAME", "TOSEC" })
                if (settings.Collection.IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0) return name;
            return null;
        }

        readonly Settings settings = new Settings();
        DatIndex index;
        CueIndex cues;   // cue sheet packs found at the last scan
        readonly List<SystemResult> results = new List<SystemResult>();
        List<string> unmatchedFolders = new List<string>();
        CancellationTokenSource cts;
        bool busy;

        TextBox romBox, datBox, dlBox, cueBox, filterBox;
        Button scanButton, romBrowse, datBrowse, dlBrowse, cueBrowse, datButton, regionButton, typesButton, themeButton, fixButton, deleteButton, addButton, exportButton, changesButton;
        CheckBox oneGameBox, miaBox, autoBox, scanOnOpenBox, keepPacksBox;
        DownloadWatcher watcher;
        bool loading = true;
        Label datLabel, statusLabel, cueLabel;
        ProgressBar progress;
        ListView systemList;
        SplitContainer split;
        TabStrip tabs;
        RadioButton missingTab, misnamedTab, unneededTab, problemTab, okTab;
        ResultList missingList, misnamedList, unneededList, problemList, okList;
        ContextMenuStrip listMenu, systemMenu, datMenu, themeMenu;

        public MainForm()
        {
            Text = AppTitle;
            Font = new Font("Segoe UI", 9f);
            AutoScaleMode = AutoScaleMode.Dpi;
            Size = new Size(1280, 800);
            MinimumSize = new Size(900, 500);
            StartPosition = FormStartPosition.CenterScreen;
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }

            BuildTop();
            BuildBottom();
            BuildMiddle();
            BuildMenus();
            SetTips();
            Fixer.UseRecycleBin = settings.UseRecycleBin;

            FillCollectionBox();
            ShowCollectionSettings();
            miaBox.Checked = settings.ShowMia;
            scanOnOpenBox.Checked = settings.ScanOnOpen;
            loading = false;
            RestoreWindow();
            RestoreColumns();
            ApplyTheme();

            // Follow Windows when it switches between light and dark.
            Microsoft.Win32.UserPreferenceChangedEventHandler follow = (s, e) =>
            {
                if (settings.Theme == "System" && IsHandleCreated) BeginInvoke((Action)ApplyTheme);
            };
            Microsoft.Win32.SystemEvents.UserPreferenceChanged += follow;
            FormClosed += (s, e) => Microsoft.Win32.SystemEvents.UserPreferenceChanged -= follow;

            Shown += async (s, e) =>
            {
                await LoadDats();
                await CheckDownloads();
                // Adding downloads automatically also needs to know what's missing, so it scans too.
                if (autoBox.Checked) UpdateWatcher(false);
                if ((scanOnOpenBox.Checked || autoBox.Checked) && Directory.Exists(romBox.Text.Trim()) && !busy) Scan();
                if (settings.CheckUpdates) CheckForUpdate(false);
            };
            FormClosing += (s, e) =>
            {
                if (cts != null) cts.Cancel();
                if (watcher != null) { watcher.Dispose(); watcher = null; }
                SaveWindow();
                SaveColumns();
            };
            RefreshAll();
        }

        // ------------------------------------------------------------ layout

        static Button MakeButton(string text, EventHandler click)
        {
            var b = new Button { Text = text, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new Padding(6, 1, 6, 1), Margin = new Padding(3) };
            b.Click += click;
            return b;
        }

        // "ROM Check O-MATIC v1.0", the version taken from the program itself (set in Program.cs).
        static readonly string AppTitle = "ROM Check O-MATIC v" + typeof(MainForm).Assembly.GetName().Version.ToString(2);

        void BuildTop()
        {
            var top = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 4, Padding = new Padding(6, 6, 6, 0) };
            top.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            top.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            top.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            top.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

            romBox = new TextBox { Dock = DockStyle.Fill, Margin = new Padding(3, 5, 3, 3) };
            romBox.KeyDown += (s, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; Scan(); } };
            romBrowse = MakeButton("Browse...", (s, e) => Browse(romBox, "Choose the folder that holds this ROM set (one subfolder per system)"));
            scanButton = MakeButton("Scan", (s, e) => Scan());
            scanButton.Font = new Font(Font, FontStyle.Bold);
            scanButton.MinimumSize = new Size(90, 0);

            datBox = new TextBox { Dock = DockStyle.Fill, Margin = new Padding(3, 5, 3, 3) };
            datBox.Leave += async (s, e) => { if (!busy && !SamePath(datBox.Text, settings.DatFolder)) await LoadDats(); };
            datBrowse = MakeButton("Browse...", async (s, e) => { if (Browse(datBox, "Choose the folder that holds your DAT files (subfolders are searched too)")) await LoadDats(); });
            // These buttons sit at the right of the window, so their menus open leftwards: opening rightwards
            // would push them off the edge, and on a second screen Windows would move them onto it.
            datButton = MakeButton("DAT Files ▾", (s, e) => datMenu.Show(datButton, new Point(datButton.Width, datButton.Height), ToolStripDropDownDirection.BelowLeft));
            themeButton = MakeButton("Options ▾", (s, e) => themeMenu.Show(themeButton, new Point(themeButton.Width, themeButton.Height), ToolStripDropDownDirection.BelowLeft));
            themeButton.Dock = DockStyle.Fill;
            datButton.Dock = DockStyle.Fill;
            scanButton.Dock = DockStyle.Fill;

            // Which collection (No-Intro, Redump...): each has its own folders and settings.
            collectionBox = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 260, Anchor = AnchorStyles.Left, Margin = new Padding(3, 5, 3, 3) };
            collectionBox.SelectedIndexChanged += (s, e) => CollectionPicked();
            top.Controls.Add(MakeLabel("Collection:"), 0, 0);
            top.Controls.Add(collectionBox, 1, 0);

            top.Controls.Add(MakeLabel("ROM folder:"), 0, 1);
            top.Controls.Add(romBox, 1, 1);
            top.Controls.Add(romBrowse, 2, 1);
            top.Controls.Add(scanButton, 3, 1);
            top.Controls.Add(MakeLabel("DAT folder:"), 0, 2);
            top.Controls.Add(datBox, 1, 2);
            top.Controls.Add(datBrowse, 2, 2);
            top.Controls.Add(datButton, 3, 2);

            // Where downloaded ROMs go, for adding missing ROMs to the set.
            dlBox = new TextBox { Dock = DockStyle.Fill, Margin = new Padding(3, 5, 3, 3) };
            dlBox.Leave += (s, e) => ApplyDownloadsBox();
            dlBox.KeyDown += (s, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; ApplyDownloadsBox(); AddFromFolder(); } };
            dlBrowse = MakeButton("Browse...", (s, e) => ChooseIncomingFolder());
            addButton = MakeButton("Add Missing", (s, e) => { ApplyDownloadsBox(); AddFromFolder(); });
            addButton.Dock = DockStyle.Fill;
            top.Controls.Add(MakeLabel("Missing ROMs folder:"), 0, 3);
            top.Controls.Add(dlBox, 1, 3);
            top.Controls.Add(dlBrowse, 2, 3);
            top.Controls.Add(addButton, 3, 3);

            // Where Redump's cue sheet packs are kept. It's optional ("!" folders in the ROM folder, and the DAT
            // folder, are looked in anyway), and only shown for disc sets or once it's been set.
            cueLabel = MakeLabel("Cue sheets folder:");
            cueBox = new TextBox { Dock = DockStyle.Fill, Margin = new Padding(3, 5, 3, 3) };
            cueBox.Leave += (s, e) => ApplyCueBox();
            cueBox.KeyDown += (s, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; ApplyCueBox(); } };
            cueBrowse = MakeButton("Browse...", (s, e) => ChooseCueFolder());
            top.Controls.Add(cueLabel, 0, 4);
            top.Controls.Add(cueBox, 1, 4);
            top.Controls.Add(cueBrowse, 2, 4);
            cueLabel.Visible = cueBox.Visible = cueBrowse.Visible = false;   // until the DATs are read

            // Wraps onto a second line when the window is too narrow, rather than running under the buttons at the right.
            var options = new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Dock = DockStyle.Fill, WrapContents = true, Margin = new Padding(0) };
            oneGameBox = new CheckBox { Text = "Use 1G1R", AutoSize = true, Margin = new Padding(3, 7, 3, 3) };
            oneGameBox.CheckedChanged += (s, e) =>
            {
                if (loading) return;
                settings.OneGamePerTitle = oneGameBox.Checked;
                regionButton.Enabled = !busy;
                if (results.Count > 0 && !busy) Scan();
            };
            regionButton = MakeButton("Region order...", (s, e) => EditRegions());
            typesButton = MakeButton("ROM Types...", (s, e) => EditGameTypes());
            typesButton.Margin = new Padding(16, 3, 3, 3);
            datLabel = new Label { AutoSize = true, Margin = new Padding(16, 8, 3, 3), Tag = "gray" };
            options.Controls.Add(oneGameBox);
            options.Controls.Add(regionButton);
            options.Controls.Add(typesButton);
            scanOnOpenBox = new CheckBox { Text = "Scan when the app opens", AutoSize = true, Margin = new Padding(16, 7, 3, 3) };
            scanOnOpenBox.CheckedChanged += (s, e) => { if (!loading) settings.ScanOnOpen = scanOnOpenBox.Checked; };
            options.Controls.Add(scanOnOpenBox);
            autoBox = new CheckBox { Text = "Add new downloads automatically", AutoSize = true, Margin = new Padding(16, 7, 3, 3) };
            autoBox.CheckedChanged += (s, e) => { if (!loading) { settings.AutoAdd = autoBox.Checked; UpdateWatcher(true); } };
            options.Controls.Add(autoBox);
            // TOSEC packs (one zip per DAT): fix inside them, or unpack them into one zip per game. Only shown for TOSEC.
            keepPacksBox = new CheckBox { Text = "Keep TOSEC packs", AutoSize = true, Margin = new Padding(16, 7, 3, 3), Visible = false };
            keepPacksBox.CheckedChanged += (s, e) => { if (!loading) { settings.KeepPacks = keepPacksBox.Checked; if (results.Any(r => r.IsPack) && !busy) Scan(); } };
            options.Controls.Add(keepPacksBox);
            options.Controls.Add(datLabel);
            top.Controls.Add(options, 1, 5);

            // Options, at the right.
            themeButton.Dock = DockStyle.None;
            var rightButtons = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Anchor = AnchorStyles.Right, Margin = new Padding(0) };
            rightButtons.Controls.Add(themeButton);
            top.Controls.Add(rightButtons, 2, 5);
            top.SetColumnSpan(rightButtons, 2);

            Controls.Add(top);
        }

        static Label MakeLabel(string text)
        {
            return new Label { Text = text, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 6, 3, 3) };
        }

        // The pop-up tips (Options > Show Pop-up Tips turns them off).
        void SetTips()
        {
            Tips.Enabled = settings.ShowTips;
            Tips.Set(collectionBox, "Which ROM set to work on: No-Intro, Redump, MAME, TOSEC or your own.\nEach collection has its own folders and settings. \"New collection...\" adds one.");
            Tips.Set(romBox, "The folder that holds this ROM set, with one subfolder per system.\nPress Enter to scan.");
            Tips.Set(romBrowse, "Choose the folder that holds this ROM set.");
            Tips.Set(scanButton, "Checks every system against its DAT.\nNothing is changed by scanning. Click again to stop.");
            Tips.Set(datBox, "The folder that holds this collection's DAT files.\nSubfolders are searched too, and the newest copy of each DAT is used.");
            Tips.Set(datBrowse, "Choose the folder that holds your DAT files.");
            Tips.Set(datButton, "Download, import, reload or show your DAT files,\nand rename system folders with their DAT dates.");
            Tips.Set(dlBox, "The folder you download missing ROMs into, such as Downloads.\nAdd Missing takes ROMs from here and puts them in your set.");
            Tips.Set(dlBrowse, "Choose the folder you download missing ROMs into.");
            Tips.Set(addButton, "Looks in the Missing ROMs folder for ROMs you're missing, and shows what it found.\nWhen you confirm, each one is zipped with its DAT name, checked, and put in its system folder.");
            Tips.Set(cueBox, "Where Redump's cue sheet packs (the Cuesheets zips) are kept. Optional:\n\"!\" folders in the ROM folder, and the DAT folder, are looked in anyway.");
            Tips.Set(cueBrowse, "Choose the folder that holds your cue sheet packs.");
            Tips.Set(oneGameBox, "1G1R (one game, one ROM): keep only the best version of each title,\nby your region order. Other versions are listed as unneeded.");
            Tips.Set(regionButton, "Set the region order for 1G1R, and untick regions to leave out\nROMs released only there.");
            Tips.Set(typesButton, "Choose which kinds of ROM to keep: betas, prototypes, demos,\nunlicensed, pirate, aftermarket, BIOS files and applications.");
            Tips.Set(scanOnOpenBox, "Scan your set every time the app opens, so the lists are always up to date.");
            Tips.Set(autoBox, "While the app is open, ROMs that land in your Missing ROMs folder are added\nto your set as soon as they've finished downloading.");
            Tips.Set(keepPacksBox, "Ticked: games are fixed inside your TOSEC packs (one zip per DAT).\nUnticked: Fix Wrong Names unpacks each pack into a folder with one zip per game.");
            Tips.Set(themeButton, "Theme (match Windows, light or dark), pop-up tips, the Recycle Bin\nand update checks. These settings are shared by every collection.");
            Tips.Set(fixButton, "Renames every file under Wrong Name to its DAT name (inside zips too).\nWith a system selected, only that system. You're asked first.");
            Tips.Set(deleteButton, "Permanently deletes every file under Unneeded.\nWith a system selected, only that system. You're asked first.");
            Tips.Set(exportButton, "Saves a text report of what's missing, misnamed and unneeded.\nWith All systems selected, each system gets its own report in its own folder.");
            Tips.Set(filterBox, "Shows only the rows that contain this text.");
            Tips.Set(miaBox, "MIA ROMs are ones no known dump exists for.\nUntick to leave them out of the Missing list.");
        }

        void BuildBottom()
        {
            var bottom = new TableLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, ColumnCount = 5, Padding = new Padding(6, 0, 6, 6) };
            bottom.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            bottom.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            bottom.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            bottom.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 160));
            bottom.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            fixButton = MakeButton("Fix Wrong Names", (s, e) => FixFiles(ScopeFiles(f => f.IsFixable && f.CanChange)));
            deleteButton = MakeButton("Delete Unneeded", (s, e) => DeleteFiles(ScopeFiles(f => f.IsUnneeded && f.CanChange)));
            exportButton = MakeButton("Save Report...", (s, e) => ExportReport());
            exportButton.Margin = new Padding(16, 3, 3, 3);
            progress = new ProgressBar { Dock = DockStyle.Fill, Margin = new Padding(8, 7, 3, 7), Visible = false };
            statusLabel = new Label { AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(8, 8, 3, 3) };
            changesButton = MakeButton("What Changed...", (s, e) => ShowChanges());
            changesButton.Visible = false;
            Tips.Set(changesButton, "Lists the ROMs added and newly missing in each system since the last scan.");
            var status = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Dock = DockStyle.Fill, Margin = new Padding(0) };
            status.Controls.Add(statusLabel);
            status.Controls.Add(changesButton);
            bottom.Controls.Add(fixButton, 0, 0);
            bottom.Controls.Add(deleteButton, 1, 0);
            bottom.Controls.Add(exportButton, 2, 0);
            bottom.Controls.Add(progress, 3, 0);
            bottom.Controls.Add(status, 4, 0);
            Controls.Add(bottom);
        }

        void BuildMiddle()
        {
            split = new SplitContainer { Dock = DockStyle.Fill, FixedPanel = FixedPanel.Panel1, Padding = new Padding(6, 4, 6, 4) };

            systemList = new ListView { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, HideSelection = false, MultiSelect = false };
            systemList.Columns.Add("System", 260);
            systemList.Columns.Add("Have", 90, HorizontalAlignment.Right);
            systemList.Columns.Add("Missing", 60, HorizontalAlignment.Right);
            systemList.Columns.Add("Wrong Name", 90, HorizontalAlignment.Right);
            systemList.Columns.Add("Unneeded", 70, HorizontalAlignment.Right);
            systemList.Columns.Add("Problems", 65, HorizontalAlignment.Right);
            systemList.SelectedIndexChanged += (s, e) => RefreshRows();
            systemList.MouseUp += (s, e) =>
            {
                if (e.Button != MouseButtons.Right) return;
                var hit = systemList.HitTest(e.Location);
                if (hit.Item == null) return;
                hit.Item.Selected = true;
                systemMenu.Show(systemList, e.Location);
            };
            split.Panel1.Controls.Add(systemList);

            var right = new Panel { Dock = DockStyle.Fill };
            var filterRow = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, WrapContents = false };
            filterBox = new TextBox { Width = 300, Margin = new Padding(3, 3, 3, 3) };
            filterBox.TextChanged += (s, e) => RefreshRows();
            miaBox = new CheckBox { Text = "Show MIA ROMs in Missing", AutoSize = true, Margin = new Padding(16, 5, 3, 3) };
            miaBox.CheckedChanged += (s, e) => { settings.ShowMia = miaBox.Checked; RefreshRows(); };
            var filterLabel = MakeLabel("Filter:"); filterLabel.Margin = new Padding(0, 6, 3, 3);
            filterRow.Controls.Add(filterLabel);
            filterRow.Controls.Add(filterBox);
            filterRow.Controls.Add(miaBox);

            tabs = new TabStrip();
            var pages = new Panel { Dock = DockStyle.Fill };
            missingList = new ResultList("System", 200, "ROM", 460, "CRC32", 80, "Note", 400);
            misnamedList = new ResultList("System", 220, "Current Name", 420, "Correct Name", 420, "What Changes", 260);
            unneededList = new ResultList("System", 220, "File", 520, "Size", 80, "Why", 420);
            unneededList.SizeColumn = 2;
            problemList = new ResultList("System", 220, "File", 520, "Problem", 500);
            okList = new ResultList("System", 220, "File", 620, "Note", 400);
            missingTab = AddTab(missingList, pages);
            misnamedTab = AddTab(misnamedList, pages);
            unneededTab = AddTab(unneededList, pages);
            problemTab = AddTab(problemList, pages);
            okTab = AddTab(okList, pages);
            tabs.SelectedChanged += (s, e) => UpdateButtons();

            right.Controls.Add(pages);
            right.Controls.Add(tabs);
            right.Controls.Add(filterRow);
            pages.BringToFront();
            split.Panel2.Controls.Add(right);

            Controls.Add(split);
            split.BringToFront();
        }

        RadioButton AddTab(ResultList list, Panel pages)
        {
            pages.Controls.Add(list);
            var tab = tabs.Add(list);
            list.MouseUp += (s, e) => { if (e.Button == MouseButtons.Right) ShowListMenu(list, e.Location); };
            list.DoubleClick += (s, e) => { var r = list.SelectedRows().FirstOrDefault(); if (r != null) ShowInFolder(r); };
            list.KeyDown += (s, e) =>
            {
                if (e.Control && e.KeyCode == Keys.A) { for (int i = 0; i < list.VirtualListSize; i++) list.SelectedIndices.Add(i); e.Handled = true; }
                else if (e.Control && e.KeyCode == Keys.C) { CopyNames(list); e.Handled = true; }
                else if (e.KeyCode == Keys.Delete) { DeleteSelected(list); e.Handled = true; }
            };
            return tab;
        }

        void BuildMenus()
        {
            datMenu = new ContextMenuStrip();
            var downloadItem = new ToolStripMenuItem("DAT Download Page", null, (s, e) =>
            {
                var page = DownloadPageFor(DatSource());
                if (page != null) OpenUrl(page.Item2);
            });
            datMenu.Items.Add(downloadItem);
            var importItem = new ToolStripMenuItem("Import a Downloaded Love Pack Zip...", null, async (s, e) => await ImportPackDialog());
            datMenu.Items.Add(importItem);
            var downloadSeparator = new ToolStripSeparator();
            datMenu.Items.Add(downloadSeparator);
            Tips.Item(importItem, "Unpacks a No-Intro Love Pack zip you've downloaded into the DAT folder,\nthen offers to delete the older pack.");
            Tips.Item(datMenu.Items.Add("Reload DAT Files", null, async (s, e) => await LoadDats()),
                "Reads the DAT folder again, after you've added or replaced DATs.");
            Tips.Item(datMenu.Items.Add("Show DAT Folder", null, (s, e) => OpenFolder(datBox.Text)),
                "Opens the DAT folder in File Explorer.");

            datMenu.Items.Add(new ToolStripSeparator());
            var renameAllFromDatMenu = new ToolStripMenuItem("Rename System Folders with DAT Dates...", null, (s, e) => RenameFolders(FoldersToRename(results)));
            Tips.Item(renameAllFromDatMenu, "Renames each system folder to its DAT's name and date, such as \"Sony - PlayStation (2026-06-15 11-55-46)\",\n" +
                                            "so you can see which DAT each one was checked against. You see the list first.");
            datMenu.Items.Add(renameAllFromDatMenu);
            datMenu.Opening += (s, e) =>
            {
                // Each set's DATs come from its own site; the Love Pack is No-Intro's.
                string source = DatSource();
                var page = DownloadPageFor(source);
                downloadItem.Visible = page != null;
                if (page != null)
                {
                    downloadItem.Text = page.Item1;
                    downloadItem.ToolTipText = "Opens the page where " + source + "'s DATs are downloaded, in your web browser.";
                }
                importItem.Visible = source == "No-Intro";
                downloadSeparator.Visible = downloadItem.Visible || importItem.Visible;
                int n = FoldersToRename(results).Count;
                renameAllFromDatMenu.Enabled = !busy && n > 0;
                renameAllFromDatMenu.Text = "Rename System Folders with DAT Dates" + (n > 0 ? " (" + n + ")..." : "");
            };

            systemMenu = new ContextMenuStrip();
            var chooseItem = new ToolStripMenuItem("Choose DAT...", null, (s, e) => ChooseDat());
            var rescanItem = new ToolStripMenuItem("Rescan", null, async (s, e) =>
            {
                var sys = SelectedSystem();
                if (sys != null) await Rescan(new[] { sys.Folder });
            });
            var showItem = new ToolStripMenuItem("Show in Folder", null, (s, e) =>
            {
                var sys = SelectedSystem();
                if (sys != null) OpenFolder(sys.Folder);
            });
            var renameSep = new ToolStripSeparator();
            var renameItem = new ToolStripMenuItem("Rename Folder with DAT Date", null, (s, e) =>
            {
                var sys = SelectedSystem();
                RenameFolders(sys != null ? FoldersToRename(new[] { sys }) : FoldersToRename(results));
            });
            var ownSep = new ToolStripSeparator();
            var ownRegionsItem = new ToolStripMenuItem("Region Overrides for This System...", null, (s, e) => EditSystemRegions(SelectedSystem()));
            var ownTypesItem = new ToolStripMenuItem("ROM Type Overrides for This System...", null, (s, e) => EditSystemTypes(SelectedSystem()));
            var ownRemoveItem = new ToolStripMenuItem("Remove This System's Overrides...", null, (s, e) => RemoveSystemOverrides(SelectedSystem()));
            Tips.Item(chooseItem, "Picks which DAT this folder is checked against, for a folder the app\ncan't match by name. Choose \"Match automatically by folder name\" to undo it.");
            Tips.Item(rescanItem, "Checks just this system again, such as after copying files into its folder.");
            Tips.Item(showItem, "Opens the system's folder in File Explorer.");
            Tips.Item(ownRegionsItem, "Gives this system its own regions in place of the collection's, such as\nkeeping Japan for a system only ever sold there. Ticked when it has them.");
            Tips.Item(ownTypesItem, "Gives this system its own ROM types (betas, demos, BIOS files...)\nin place of the collection's. Ticked when it has them.");
            Tips.Item(renameItem, "Renames the folder to its DAT's name and date, such as \"Sony - PlayStation (2026-06-15 11-55-46)\",\n" +
                                  "so you can see which DAT it was checked against.");
            systemMenu.Items.AddRange(new ToolStripItem[] { chooseItem, rescanItem, showItem, ownSep, ownRegionsItem, ownTypesItem, ownRemoveItem, renameSep, renameItem });
            systemMenu.Opening += (s, e) =>
            {
                var sys = SelectedSystem();
                bool all = sys == null;
                if (all && systemList.SelectedItems.Count == 0) { e.Cancel = true; return; }
                chooseItem.Visible = rescanItem.Visible = showItem.Visible = renameSep.Visible = !all;
                // A tick shows the system has its own setting rather than the collection's.
                ownSep.Visible = ownRegionsItem.Visible = ownTypesItem.Visible = !all;
                ownRemoveItem.Visible = !all && (sys.OwnRegions || sys.OwnTypes);
                if (!all)
                {
                    ownRegionsItem.Checked = sys.OwnRegions;
                    ownTypesItem.Checked = sys.OwnTypes;
                    ownRegionsItem.Enabled = ownTypesItem.Enabled = ownRemoveItem.Enabled = !busy && sys.Dat != null;
                    // Only shown when the system has overrides; says which it will remove.
                    ownRemoveItem.Text = sys.OwnRegions && sys.OwnTypes ? "Remove This System's Overrides..."
                                       : sys.OwnRegions ? "Remove This System's Region Overrides..." : "Remove This System's ROM Type Overrides...";
                    ownRemoveItem.ToolTipText = "Goes back to the collection's " +
                        (sys.OwnRegions && sys.OwnTypes ? "regions and ROM types" : sys.OwnRegions ? "regions" : "ROM types") +
                        " for this system, and checks it again. You're asked first.";
                }
                if (all)
                {
                    int n = FoldersToRename(results).Count;
                    renameItem.Text = n == 0 ? "All Folders Already Show Their DAT Dates" : "Rename " + Plural(n, "Folder") + " with DAT Dates...";
                    renameItem.Enabled = !busy && n > 0;
                }
                else
                {
                    string target = DatFolderName(sys);
                    renameItem.Text = target == null ? "Rename Folder with DAT Date (no DAT found)"
                                    : target == sys.FolderName ? "Folder Already Shows the DAT Date"
                                    : "Rename Folder to \"" + target + "\"...";
                    renameItem.Enabled = !busy && target != null && target != sys.FolderName;
                }
                foreach (ToolStripItem i in new ToolStripItem[] { chooseItem, rescanItem, showItem }) i.Enabled = !busy;
            };

            listMenu = new ContextMenuStrip();

            // Options: settings shared by every collection.
            themeMenu = new ContextMenuStrip();
            themeMenu.Items.Add(new ToolStripLabel("Theme") { ForeColor = SystemColors.GrayText });
            var themeItems = new List<ToolStripMenuItem>();
            foreach (var choice in new[] { new[] { "System", "Match Windows" }, new[] { "Light", "Light" }, new[] { "Dark", "Dark" } })
            {
                string value = choice[0];
                var item = new ToolStripMenuItem(choice[1]) { Tag = value };
                Tips.Item(item, value == "System" ? "Uses light or dark to match Windows' own setting, and follows it when it changes."
                                                  : "Always uses the " + value.ToLower() + " theme.");
                item.Click += (s, e) => { settings.Theme = value; ApplyTheme(); };
                themeMenu.Items.Add(item);
                themeItems.Add(item);
            }
            themeMenu.Items.Add(new ToolStripSeparator());
            var fitItem = new ToolStripMenuItem("Fit Columns Automatically");
            fitItem.Click += (s, e) =>
            {
                settings.AutoFitColumns = !settings.AutoFitColumns;
                if (settings.AutoFitColumns) { settings.Ini.Write("Columns", null, null); FitSystemColumns(); FitTabColumns(); }
                else SaveColumns();   // keep the widths as they are now
            };
            themeMenu.Items.Add(fitItem);
            Tips.Item(fitItem, "Ticked: the list columns are sized to fit what's in them whenever the lists change.\n" +
                               "Unticked: columns stay the width you drag them to, and the app remembers them.");
            var tipsItem = new ToolStripMenuItem("Show Pop-up Tips");
            tipsItem.Click += (s, e) => { settings.ShowTips = !settings.ShowTips; SetTips(); };
            themeMenu.Items.Add(tipsItem);
            var recycleItem = new ToolStripMenuItem("Delete to the Recycle Bin");
            recycleItem.Click += (s, e) => { settings.UseRecycleBin = !settings.UseRecycleBin; Fixer.UseRecycleBin = settings.UseRecycleBin; };
            themeMenu.Items.Add(recycleItem);
            var removeItem = new ToolStripMenuItem("Remove Downloads Once Their ROMs Are Added");
            removeItem.Click += (s, e) => { settings.RemoveDownloads = !settings.RemoveDownloads; };
            themeMenu.Items.Add(removeItem);
            var awakeItem = new ToolStripMenuItem("Keep the PC Awake During Long Jobs");
            awakeItem.Click += (s, e) => { settings.KeepAwake = !settings.KeepAwake; if (busy) Awake.Set(settings.KeepAwake); };
            themeMenu.Items.Add(awakeItem);
            themeMenu.Items.Add(new ToolStripSeparator());
            var updatesItem = new ToolStripMenuItem("Check for Updates When the App Opens");
            updatesItem.Click += (s, e) => { settings.CheckUpdates = !settings.CheckUpdates; if (settings.CheckUpdates) CheckForUpdate(false); };
            themeMenu.Items.Add(updatesItem);
            Tips.Item(themeMenu.Items.Add("Check for Updates Now", null, (s, e) => CheckForUpdate(true)),
                "Asks GitHub now whether there's a newer version, and offers to open its download page.");
            Tips.Item(tipsItem, "Shows a short explanation like this one when the mouse rests on a button, box or menu item.");
            Tips.Item(recycleItem, "Ticked: deleted files go to the Recycle Bin, so you can get them back.\nUnticked: they're deleted outright.");
            Tips.Item(removeItem, "Ticked: a download is deleted from the missing ROMs folder once its ROMs are safely in your set.\n" +
                                  "Unticked: downloads are left where they are. Downloads holding other files too are always kept.");
            Tips.Item(awakeItem, "Ticked: the PC won't go to sleep while a scan, fix, delete or add is running.\n" +
                                 "The screen can still turn off. Normal sleep settings return when the job ends.");
            Tips.Item(updatesItem,"Checks GitHub for a newer version each time the app opens.\nEach new version is only mentioned once.");
            themeMenu.Opening += (s, e) =>
            {
                foreach (var item in themeItems) item.Checked = (string)item.Tag == settings.Theme;
                fitItem.Checked = settings.AutoFitColumns;
                tipsItem.Checked = settings.ShowTips;
                recycleItem.Checked = settings.UseRecycleBin;
                removeItem.Checked = settings.RemoveDownloads;
                awakeItem.Checked = settings.KeepAwake;
                updatesItem.Checked = settings.CheckUpdates;
            };

            foreach (var m in new ToolStrip[] { datMenu, systemMenu, listMenu, themeMenu }) Tips.Menu(m);
        }

        // ------------------------------------------------------------ updates

        // Asks GitHub whether there's a newer version. "manual" is Check for Updates Now: it always says what
        // it found. The check when the app opens stays quiet unless there's a new version it hasn't mentioned yet.
        async void CheckForUpdate(bool manual)
        {
            UpdateInfo latest;
            try { latest = await Task.Run(() => Updates.Latest()); }
            catch (Exception ex)
            {
                if (manual) ThemedBox.Show(this, "Couldn't check for updates:\n\n" + ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            if (latest.Version <= Updates.Current)
            {
                if (manual) ThemedBox.Show(this, "You have the latest version (v" + Updates.Current + ").", Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            if (!manual && settings.MentionedUpdate == latest.Tag) return;
            settings.MentionedUpdate = latest.Tag;
            if (ThemedBox.Show(this, "ROM Check O-MATIC " + latest.Tag + " is available (you have v" + Updates.Current + ").\n\nOpen its download page?",
                               Text, MessageBoxButtons.YesNo, MessageBoxIcon.Information) == DialogResult.Yes)
                OpenUrl(latest.Page);
        }

        // ------------------------------------------------------------ collections

        ComboBox collectionBox;
        bool fillingCollections;
        const string NewCollectionItem = "New Collection...";

        void FillCollectionBox()
        {
            fillingCollections = true;
            collectionBox.Items.Clear();
            foreach (var c in settings.Collections) collectionBox.Items.Add(c);
            collectionBox.Items.Add(NewCollectionItem);
            collectionBox.SelectedItem = settings.Collection;
            fillingCollections = false;
        }

        // Puts the current collection's folders and settings into the window.
        void ShowCollectionSettings()
        {
            bool was = loading;
            loading = true;
            romBox.Text = settings.RomFolder;
            datBox.Text = settings.DatFolder;
            dlBox.Text = settings.IncomingFolder;
            cueBox.Text = settings.CueFolder;
            oneGameBox.Checked = settings.OneGamePerTitle;
            autoBox.Checked = settings.AutoAdd;
            keepPacksBox.Checked = settings.KeepPacks;
            loading = was;
            Text = AppTitle + " - " + settings.Collection;
            UpdateRegionButton();
            UpdateTypesButton();
        }

        async void CollectionPicked()
        {
            if (fillingCollections) return;
            string picked = collectionBox.SelectedItem as string;
            if (picked == null || picked == settings.Collection) return;
            if (busy) { FillCollectionBox(); return; }
            if (picked == NewCollectionItem)
            {
                string name = InputBox.Ask(this, "New Collection", "Name for the new collection, for example TOSEC.\n" +
                                           "It gets its own ROM, DAT and missing ROMs folders and its own settings.", "");
                name = name == null ? null : name.Trim();
                if (string.IsNullOrEmpty(name)) { FillCollectionBox(); return; }
                if (name.IndexOfAny(new[] { ',', '[', ']', '=' }) >= 0 || settings.Collections.Contains(name, StringComparer.OrdinalIgnoreCase) || name == NewCollectionItem)
                {
                    ThemedBox.Show(this, "\"" + name + "\" can't be used: it's taken, or contains , [ ] or =.", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    FillCollectionBox();
                    return;
                }
                var list = settings.Collections;
                list.Add(name);
                settings.Collections = list;
                picked = name;
            }

            // Keep what was set for the collection being left.
            ApplyDownloadsBox();
            settings.RomFolder = romBox.Text.Trim();
            if (watcher != null) { watcher.Dispose(); watcher = null; }
            SaveColumns();

            settings.Collection = picked;
            FillCollectionBox();
            results.Clear();
            changesButton.Visible = false;
            index = null;
            RefreshAll();
            ShowCollectionSettings();
            await LoadDats();
            if (autoBox.Checked) UpdateWatcher(false);
            if (Directory.Exists(romBox.Text.Trim()) && (scanOnOpenBox.Checked || autoBox.Checked)) Scan();
            else statusLabel.Text = romBox.Text.Trim().Length == 0 ? "Choose the ROM and DAT folders for the " + picked + " collection" : "Ready";
        }

        // ------------------------------------------------------------ fitting columns

        // The lists whose column widths are remembered, by the name used in the settings file.
        Dictionary<string, ListView> ColumnLists()
        {
            return new Dictionary<string, ListView> {
                { "Systems", systemList }, { "Missing", missingList }, { "WrongName", misnamedList },
                { "Unneeded", unneededList }, { "Problems", problemList }, { "Correct", okList } };
        }

        // With Options > Fit Columns Automatically on, every list is sized to fit whenever it changes, and no
        // widths are saved. With it off, columns stay where they're dragged: one set of widths per list.
        void SaveWidths(string key, ListView lv)
        {
            settings.Ini.Write("Columns", key, string.Join(",", lv.Columns.Cast<ColumnHeader>().Select(c => c.Width)));
        }

        // Returns false when nothing is saved under that key (the list then keeps its current widths).
        bool RestoreWidths(string key, ListView lv)
        {
            var parts = settings.Ini.Read("Columns", key, "").Split(',');
            // Only if the list still has the same columns (a new version may add one).
            if (parts.Length != lv.Columns.Count) return false;
            for (int i = 0; i < parts.Length; i++)
            {
                int w;
                if (int.TryParse(parts[i], out w) && w >= 20 && w <= 3000) lv.Columns[i].Width = w;
            }
            return true;
        }

        void SaveColumns()
        {
            if (settings.AutoFitColumns) return;
            foreach (var kv in ColumnLists()) SaveWidths(kv.Key, kv.Value);
        }

        void RestoreColumns()
        {
            // Older versions kept a set of widths for every system ("Missing@Sega - Mega Drive - Genesis"): clear those out.
            foreach (var key in settings.Ini.Keys("Columns").Where(k => k.Contains("@"))) settings.Ini.Write("Columns", key, null);
            if (settings.AutoFitColumns) { settings.Ini.Write("Columns", null, null); return; }
            foreach (var kv in ColumnLists()) RestoreWidths(kv.Key, kv.Value);
        }

        // Sizes the system list's columns to fit, and widens the left panel if the list doesn't fit in it
        // (it's never made narrower, so it stays where you put it).
        void FitSystemColumns()
        {
            Fit(systemList, systemList.Items.Cast<ListViewItem>()
                .Select(i => Tuple.Create(i.SubItems.Cast<ListViewItem.ListViewSubItem>().Select(x => x.Text).ToArray(), i.Font ?? systemList.Font)));
            int wanted = systemList.Columns.Cast<ColumnHeader>().Sum(c => c.Width) + SystemInformation.VerticalScrollBarWidth + 8;
            try { if (wanted > split.SplitterDistance) split.SplitterDistance = Math.Max(200, Math.Min(wanted, (int)(split.Width * 0.6))); } catch { }
        }

        // Sizes the five tabs' columns to fit what's in them.
        void FitTabColumns()
        {
            foreach (var list in new[] { missingList, misnamedList, unneededList, problemList, okList })
                Fit(list, list.Rows.Select(r => Tuple.Create(r.Cells, list.Font)));
        }

        static void Fit(ListView lv, IEnumerable<Tuple<string[], Font>> rows)
        {
            int n = lv.Columns.Count;
            var all = rows.ToList();
            // An empty list would shrink to its headings and be cramped later, so leave it as it is.
            if (all.Count == 0) return;
            var widths = new int[n];
            for (int i = 0; i < n; i++) widths[i] = TextRenderer.MeasureText(lv.Columns[i].Text, lv.Font).Width;
            // Measuring every row of a big list is slow, so measure the longest few texts in each column:
            // with a proportional font the widest is almost always among them. A count of the text lengths finds
            // them in one pass (no sorting), so this stays quick while typing in the filter box.
            for (int i = 0; i < n; i++)
            {
                int col = i, longest = 0;
                foreach (var r in all) if (col < r.Item1.Length && r.Item1[col] != null) longest = Math.Max(longest, r.Item1[col].Length);
                var count = new int[longest + 1];
                foreach (var r in all) if (col < r.Item1.Length && r.Item1[col] != null) count[r.Item1[col].Length]++;
                int from = longest, seen = 0;
                while (from > 0 && seen + count[from] < 60) seen += count[from--];
                // Every text longer than "from" (fewer than 60 of them), then some of those exactly that long.
                int sameLength = 0;
                foreach (var r in all)
                {
                    if (col >= r.Item1.Length || r.Item1[col] == null) continue;
                    int len = r.Item1[col].Length;
                    if (len < from || (len == from && ++sameLength > 60)) continue;
                    widths[i] = Math.Max(widths[i], TextRenderer.MeasureText(r.Item1[col], r.Item2).Width);
                }
            }
            lv.BeginUpdate();
            for (int i = 0; i < n; i++) lv.Columns[i].Width = widths[i] + 20;   // room for the padding either side
            lv.EndUpdate();
        }

        // ------------------------------------------------------------ renaming system folders

        // The system's name (from the DAT's header) and the DAT's date, e.g.
        // "Sega - Mega Drive - Genesis (20261121-203036)" or "Sony - PlayStation (2026-06-15 11-55-46)".
        // (Redump's DAT file names, like "Sony - PlayStation - Datfile (10914) (...)", would be confusing as folder names.)
        // The date is taken from the end of the DAT's file name, as No-Intro sometimes updates a DAT without
        // changing the date in its header; the header's version is used if the file name has no date.
        // Tags of your own at the end of the folder name, such as "[NKit]", are kept.
        static readonly Regex FileDate = new Regex(@"\((\d{4}-?\d{2}-?\d{2}[^()]*)\)\s*$");
        static readonly Regex OwnTags = new Regex(@"(\s*\[[^\[\]]*\])+\s*$");

        internal static string DatFolderName(SystemResult s)
        {
            if (s == null || s.Dat == null || string.IsNullOrEmpty(s.Dat.Name)) return null;
            var m = FileDate.Match(Path.GetFileNameWithoutExtension(s.Dat.Path ?? ""));
            string date = m.Success ? m.Groups[1].Value.Trim() : (s.Dat.Version ?? "").Trim();
            if (!date.Any(char.IsDigit)) date = "";   // e.g. "-insert version-"
            string name = s.Dat.Name.Trim() + (date.Length > 0 ? " (" + date + ")" : "");
            // MAME sets are named by version: "MAME ROMs (non-merged)" version 0.289 gives "MAME 0.289 ROMs (non-merged)".
            if (!m.Success && s.Dat.Name.StartsWith("MAME ", StringComparison.OrdinalIgnoreCase))
            {
                var v = DatIndex.MameVersion.Match(date);
                name = DatIndex.MameVersion.IsMatch(s.Dat.Name) || !v.Success || v.Value != date ? s.Dat.Name.Trim()
                     : "MAME " + date + " " + s.Dat.Name.Trim().Substring(5);
            }
            var tags = OwnTags.Match(s.FolderName ?? "");
            if (tags.Success) name += " " + tags.Value.Trim();
            foreach (char c in Path.GetInvalidFileNameChars()) name = name.Replace(c, c == ':' ? '-' : '_');
            return name.TrimEnd('.', ' ');
        }

        static List<SystemResult> FoldersToRename(IEnumerable<SystemResult> list)
        {
            return list.Where(s => !s.IsPack && DatFolderName(s) != null && DatFolderName(s) != s.FolderName).ToList();
        }

        async void RenameFolders(List<SystemResult> list)
        {
            if (busy || list.Count == 0) return;
            var lines = list.Take(12).Select(s => s.FolderName + "\n      → " + DatFolderName(s)).ToList();
            if (list.Count > 12) lines.Add("...and " + (list.Count - 12) + " more");
            string msg = "Rename " + Count(list.Count, "folder") + " to the system name and DAT date?\n\n" +
                         string.Join("\n", lines) + "\n\nOnly the folder names change. The ROMs inside aren't touched.";
            if (ThemedBox.Show(this, msg, Text, MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;

            var failed = new List<string>();
            var renamed = new List<string>();
            var map = settings.DatMap();
            foreach (var s in list)
            {
                string newName = DatFolderName(s);
                string target = Path.Combine(Path.GetDirectoryName(s.Folder.TrimEnd('\\')), newName);
                try
                {
                    if (string.Equals(s.Folder.TrimEnd('\\'), target, StringComparison.OrdinalIgnoreCase))
                    {
                        // Only the letter case differs: Windows needs a two-step rename.
                        string step = s.Folder.TrimEnd('\\') + ".romcheck-rename";
                        Directory.Move(s.Folder, step);
                        Directory.Move(step, target);
                    }
                    else if (Directory.Exists(target) || File.Exists(target))
                    {
                        failed.Add(s.FolderName + ": a folder called \"" + newName + "\" already exists");
                        continue;
                    }
                    else Directory.Move(s.Folder, target);
                }
                catch (Exception ex)
                {
                    failed.Add(s.FolderName + ": " + ex.Message + (ex is IOException ? " (is a file in it open, or the folder open in another program?)" : ""));
                    continue;
                }
                Log.Write("Renamed folder \"" + s.Folder + "\" to \"" + newName + "\"");
                // Keep a DAT chosen by hand, and the ROM folder box if it pointed at this folder.
                string mapped;
                if (map.TryGetValue(s.FolderName, out mapped)) { settings.SetDatMap(newName, mapped); settings.SetDatMap(s.FolderName, null); }
                if (SamePath(romBox.Text, s.Folder)) { romBox.Text = target; settings.RomFolder = target; }
                s.Folder = target;
                s.FolderName = newName;
                renamed.Add(target);
            }

            if (renamed.Count > 0)
            {
                await Rescan(renamed);
                results.Sort((a, b) => string.Compare(a.FolderName, b.FolderName, StringComparison.OrdinalIgnoreCase));
                RefreshAll();
            }
            string summary = "Renamed " + Count(renamed.Count, "folder") + ".";
            statusLabel.Text = summary + (failed.Count > 0 ? " " + failed.Count + " didn't work." : "");
            if (failed.Count == 0) ThemedBox.Show(this, summary, Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
            else using (var f = new TextForm(Text, summary + " These didn't work:", string.Join(Environment.NewLine, failed))) f.ShowDialog(this);
        }

        // ------------------------------------------------------------ theme and game types

        void ApplyTheme()
        {
            Theme.Choose(settings.Theme);
            SuspendLayout();
            Theme.Apply(this);
            foreach (var m in new[] { listMenu, systemMenu, datMenu, themeMenu }) Theme.Menu(m);
            ResumeLayout();
            RefreshSystems();
            ColourTabs();   // the theme just reset the tabs' text colour
            Invalidate(true);
        }

        void UpdateTypesButton()
        {
            var leave = settings.Exclude;
            if (leave == GameType.None) typesButton.Text = "ROM Types: Keeping All";
            else
            {
                var names = GameTypes.All.Where(t => (leave & t) != 0).Select(GameTypes.Short).ToList();
                typesButton.Text = "ROM Types: Leaving Out " + (names.Count <= 3 ? string.Join(", ", names) : string.Join(", ", names.Take(2)) + " and " + (names.Count - 2) + " more");
            }
        }

        void EditGameTypes()
        {
            using (var d = new GameTypesForm(settings.Exclude))
            {
                if (d.ShowDialog(this) != DialogResult.OK) return;
                settings.Exclude = d.Exclude;
            }
            UpdateTypesButton();
            if (results.Count > 0) Scan();
        }

        // ------------------------------------------------------------ settings, window

        void RestoreWindow()
        {
            var p = settings.Window.Split(',');
            int x, y, w, h, sd, max;
            if (p.Length == 6 && int.TryParse(p[0], out x) && int.TryParse(p[1], out y) && int.TryParse(p[2], out w) &&
                int.TryParse(p[3], out h) && int.TryParse(p[4], out max) && int.TryParse(p[5], out sd))
            {
                var r = new Rectangle(x, y, w, h);
                if (Screen.AllScreens.Any(s => s.WorkingArea.IntersectsWith(r)))
                {
                    StartPosition = FormStartPosition.Manual;
                    Bounds = r;
                }
                if (max == 1) WindowState = FormWindowState.Maximized;
                Load += (s, e) => { try { split.SplitterDistance = sd; } catch { } };
            }
            else Load += (s, e) => { try { split.SplitterDistance = 640; } catch { } };
        }

        void SaveWindow()
        {
            var r = WindowState == FormWindowState.Normal ? Bounds : RestoreBounds;
            settings.Window = string.Join(",", new[] { r.X, r.Y, r.Width, r.Height, WindowState == FormWindowState.Maximized ? 1 : 0, split.SplitterDistance });
        }

        void UpdateRegionButton()
        {
            var first = Preference.ParseList(settings.Regions).Take(3).ToList();
            var ignored = Settings.RegionSet(settings.IgnoredRegions);
            regionButton.Text = "Regions: " + string.Join(", ", first) + "..." +
                (ignored.Count == 0 ? "" : ignored.Count <= 2 ? " (leaving out " + string.Join(", ", ignored.OrderBy(r => r)) + ")" : " (leaving out " + ignored.Count + ")");
            regionButton.Enabled = !busy;
        }

        static bool SamePath(string a, string b)
        {
            return string.Equals((a ?? "").Trim().TrimEnd('\\'), (b ?? "").Trim().TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
        }

        bool Browse(TextBox box, string title)
        {
            using (var d = new FolderBrowserDialog { Description = title, ShowNewFolderButton = false })
            {
                if (Directory.Exists(box.Text)) d.SelectedPath = box.Text;
                if (d.ShowDialog(this) != DialogResult.OK) return false;
                box.Text = d.SelectedPath;
                return true;
            }
        }

        // ------------------------------------------------------------ busy state

        void SetBusy(bool on)
        {
            busy = on;
            Awake.Set(on && settings.KeepAwake);
            scanButton.Text = on ? "Stop" : "Scan";
            foreach (Control c in new Control[] { romBox, datBox, dlBox, cueBox, romBrowse, datBrowse, dlBrowse, cueBrowse, datButton, oneGameBox, typesButton, fixButton, deleteButton, addButton, autoBox, keepPacksBox, exportButton })
                c.Enabled = !on;
            regionButton.Enabled = !on;
            progress.Visible = on;
            if (!on) UpdateButtons();
        }

        void Progress(string text, int value, int max)
        {
            statusLabel.Text = text;
            if (max > 0)
            {
                progress.Style = ProgressBarStyle.Continuous;
                progress.Maximum = max;
                progress.Value = Math.Min(value, max);
            }
            else progress.Style = ProgressBarStyle.Marquee;
        }

        // ------------------------------------------------------------ DATs

        async Task LoadDats()
        {
            string root = datBox.Text.Trim();
            settings.DatFolder = root;
            if (!Directory.Exists(root))
            {
                index = new DatIndex { Root = root };
                ShowCueRow();
                datLabel.Text = "The DAT folder doesn't exist";
                return;
            }
            SetBusy(true);
            Progress("Reading DAT files...", 0, 0);
            cts = new CancellationTokenSource();
            var ct = cts.Token;
            try
            {
                index = await Task.Run(() => DatIndex.Build(root, ct));
                string romRoot = romBox.Text.Trim(), cueRoot = settings.CueFolder;
                cues = ShowCueRow() ? await Task.Run(() => CueIndex.Build(romRoot, root, cueRoot)) : null;
                UpdateDatLabel();
                statusLabel.Text = index.ByName.Count == 0 ? "No DAT files found in the DAT folder" : "Ready";
            }
            catch (OperationCanceledException) { statusLabel.Text = "Stopped"; }
            finally { SetBusy(false); }
        }

        // If a Love Pack zip is in Downloads and hasn't been imported, offer to import it.
        async Task CheckDownloads()
        {
            try
            {
                // The Love Pack is a No-Intro download, so this only applies to the No-Intro collection.
                if (settings.Collection != Settings.DefaultCollection) return;
                string downloads = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
                if (!Directory.Exists(downloads) || !Directory.Exists(datBox.Text)) return;
                var zip = new DirectoryInfo(downloads).GetFiles("No-Intro Love Pack*.zip").OrderByDescending(f => f.LastWriteTime).FirstOrDefault();
                if (zip == null) return;
                string name = Path.GetFileNameWithoutExtension(zip.Name);
                if (Directory.Exists(Path.Combine(datBox.Text, name)) || settings.IgnoredPack == name) return;
                var last = DatIndex.PackFolders(datBox.Text).LastOrDefault();
                if (last != null && string.Compare(Path.GetFileName(last), name, StringComparison.OrdinalIgnoreCase) >= 0) return;
                var answer = ThemedBox.Show(this, "A new DAT pack is in your Downloads folder:\n\n" + zip.Name + "\n\nImport it now?",
                    Text, MessageBoxButtons.YesNo, MessageBoxIcon.Question);
                if (answer == DialogResult.Yes) await ImportPack(zip.FullName);
                else settings.IgnoredPack = name;
            }
            catch { }
        }

        async Task ImportPackDialog()
        {
            using (var d = new OpenFileDialog { Title = "Choose the Love Pack zip you downloaded", Filter = "Zip files (*.zip)|*.zip" })
            {
                string downloads = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
                if (Directory.Exists(downloads)) d.InitialDirectory = downloads;
                if (d.ShowDialog(this) == DialogResult.OK) await ImportPack(d.FileName);
            }
        }

        async Task ImportPack(string zipPath)
        {
            string root = datBox.Text.Trim();
            if (!Directory.Exists(root)) { ThemedBox.Show(this, "Choose a DAT folder first.", Text); return; }
            string name = Path.GetFileNameWithoutExtension(zipPath);
            string dest = Path.Combine(root, name);
            SetBusy(true);
            Progress("Unpacking " + Path.GetFileName(zipPath) + "...", 0, 0);
            int count = 0;
            try
            {
                count = await Task.Run(() => Unpack(zipPath, dest));
            }
            catch (Exception ex)
            {
                SetBusy(false);
                ThemedBox.Show(this, "Couldn't unpack the zip:\n\n" + ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }
            SetBusy(false);
            await LoadDats();

            var older = DatIndex.PackFolders(root).Where(d => !SamePath(d, dest)).ToList();
            string msg = "Unpacked " + count + " DAT files into:\n" + dest;
            if (older.Count > 0)
            {
                msg += "\n\nThe newest DATs are always used, but you can delete the older pack" + (older.Count > 1 ? "s" : "") + " now:\n\n" +
                       string.Join("\n", older.Select(Path.GetFileName)) + "\n\nPermanently delete " + (older.Count > 1 ? "them" : "it") + "? This can't be undone.";
                if (ThemedBox.Show(this, msg, Text, MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) == DialogResult.Yes)
                {
                    foreach (var d in older)
                    {
                        try { Directory.Delete(d, true); Log.Write("Deleted old DAT pack \"" + d + "\""); }
                        catch (Exception ex) { ThemedBox.Show(this, "Couldn't delete " + d + ":\n" + ex.Message, Text); }
                    }
                    await LoadDats();
                }
            }
            else ThemedBox.Show(this, msg, Text);
            if (results.Count > 0) Scan();
        }

        // Unpacks the zip. If everything in it is inside one top folder, that folder is skipped.
        static int Unpack(string zipPath, string dest)
        {
            int count = 0;
            using (var zip = ZipFile.OpenRead(zipPath))
            {
                var names = zip.Entries.Select(e => e.FullName.Replace('\\', '/')).ToList();
                string strip = "";
                var firsts = names.Select(n => n.Contains("/") ? n.Substring(0, n.IndexOf('/') + 1) : "").Distinct().ToList();
                if (firsts.Count == 1 && firsts[0].Length > 0) strip = firsts[0];
                string full = Path.GetFullPath(dest).TrimEnd('\\') + "\\";
                foreach (var e in zip.Entries)
                {
                    string rel = e.FullName.Replace('\\', '/');
                    if (strip.Length > 0) rel = rel.Substring(strip.Length);
                    if (rel.Length == 0 || rel.EndsWith("/")) continue;
                    string path = Path.GetFullPath(Path.Combine(dest, rel.Replace('/', '\\')));
                    if (!path.StartsWith(full, StringComparison.OrdinalIgnoreCase)) continue;   // ignore entries that point outside the folder
                    Directory.CreateDirectory(Path.GetDirectoryName(path));
                    e.ExtractToFile(path, true);
                    if (path.EndsWith(".dat", StringComparison.OrdinalIgnoreCase)) count++;
                }
            }
            return count;
        }

        void ChooseDat()
        {
            var sys = SelectedSystem();
            if (sys == null || index == null) return;
            using (var d = new ChooseDatForm(sys.FolderName, index.ByName.Keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase).ToList(),
                                             settings.DatMap().ContainsKey(sys.FolderName) ? settings.DatMap()[sys.FolderName] : (sys.Dat != null ? sys.Dat.Name : null)))
            {
                if (d.ShowDialog(this) != DialogResult.OK) return;
                settings.SetDatMap(sys.FolderName, d.Choice);   // null clears it: match automatically
            }
            var folder = sys.Folder;
            var unused = Rescan(new[] { folder });
        }

        // Regions for one system only, such as every region for a system only ever sold in Japan.
        void EditSystemRegions(SystemResult sys)
        {
            if (sys == null || sys.Dat == null) return;
            SystemOverride own;
            settings.SystemOverrides().TryGetValue(sys.Dat.Name, out own);
            bool has = own != null && own.HasRegions;
            string order = has ? own.RegionPriority : settings.Regions;
            string ignored = has ? string.Join(", ", own.IgnoredRegions) : settings.IgnoredRegions;
            using (var d = new RegionForm(order, ignored, sys.FolderName, has))
            {
                if (d.ShowDialog(this) != DialogResult.OK) return;
                if (d.UseCollection) settings.SetSystemRegions(sys.Dat.Name, null, null);
                else settings.SetSystemRegions(sys.Dat.Name, d.Order, d.Ignored);
            }
            var unused = Rescan(new[] { sys.Folder });
        }

        // Takes away the system's own regions and ROM types, so it uses the collection's again.
        void RemoveSystemOverrides(SystemResult sys)
        {
            if (sys == null || sys.Dat == null || busy || !(sys.OwnRegions || sys.OwnTypes)) return;
            string what = sys.OwnRegions && sys.OwnTypes ? "region and ROM type overrides" : sys.OwnRegions ? "region overrides" : "ROM type overrides";
            string back = sys.OwnRegions && sys.OwnTypes ? "regions and ROM types" : sys.OwnRegions ? "regions" : "ROM types";
            if (ThemedBox.Show(this, "Remove the " + what + " for " + sys.FolderName + "?\n\nIt will use the collection's " + back + " again.",
                               Text, MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
            if (sys.OwnRegions) settings.SetSystemRegions(sys.Dat.Name, null, null);
            if (sys.OwnTypes) settings.SetSystemTypes(sys.Dat.Name, null);
            Log.Write("Removed the " + what + " for " + sys.Dat.Name);
            var unused = Rescan(new[] { sys.Folder });
        }

        void EditSystemTypes(SystemResult sys)
        {
            if (sys == null || sys.Dat == null) return;
            SystemOverride own;
            settings.SystemOverrides().TryGetValue(sys.Dat.Name, out own);
            bool has = own != null && own.Exclude.HasValue;
            using (var d = new GameTypesForm(has ? own.Exclude.Value : settings.Exclude, sys.FolderName, has))
            {
                if (d.ShowDialog(this) != DialogResult.OK) return;
                settings.SetSystemTypes(sys.Dat.Name, d.UseCollection ? (GameType?)null : d.Exclude);
            }
            var unused = Rescan(new[] { sys.Folder });
        }

        void EditRegions()
        {
            using (var d = new RegionForm(settings.Regions, settings.IgnoredRegions))
            {
                if (d.ShowDialog(this) != DialogResult.OK) return;
                settings.Regions = d.Order;
                settings.IgnoredRegions = d.Ignored;
            }
            UpdateRegionButton();
            if (results.Count > 0) Scan();
        }

        // ------------------------------------------------------------ scanning

        public static List<string> SystemFolders(string root, DatIndex index, IDictionary<string, string> manual)
        {
            // A single system's folder can be chosen too.
            if (index.Find(Path.GetFileName(root.TrimEnd('\\')), manual) != null) return new List<string> { root.TrimEnd('\\') };
            // Hidden folders, and ones starting with "!" (such as "!Cuesheets"), aren't systems.
            var list = new List<string>();
            foreach (var d in Directory.GetDirectories(root).Where(d => (new DirectoryInfo(d).Attributes & FileAttributes.Hidden) == 0 && !Path.GetFileName(d).StartsWith("!"))
                                .OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
            {
                if (index.Find(Path.GetFileName(d), manual) != null) { list.Add(d); continue; }
                // TOSEC sets are often grouped in folders, such as "Commodore Amiga\Games\", holding packs (one archive
                // per DAT, a folder per game inside, such as "Games - [ADF] (TOSEC-v2023-06-14).zip") or a folder per
                // DAT (such as an unpacked pack, "Commodore Amiga - Games - [ADF] (2025-01-30)"). Each is a system.
                var found = new List<string>();
                try { FindGrouped(d, index, manual, found, 0); } catch { }
                if (found.Count > 0) list.AddRange(found.OrderBy(p => p, StringComparer.OrdinalIgnoreCase)); else list.Add(d);
            }
            return list;
        }

        static void FindGrouped(string dir, DatIndex index, IDictionary<string, string> manual, List<string> found, int depth)
        {
            foreach (var p in Directory.GetFiles(dir))
                if ((p.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) || p.EndsWith(".7z", StringComparison.OrdinalIgnoreCase)) &&
                    Scanner.PackDat(p, index, manual) != null)
                    found.Add(p);
            if (depth >= 4) return;
            foreach (var sub in Directory.GetDirectories(dir))
            {
                if (Path.GetFileName(sub).StartsWith("!")) continue;
                if (index.Find(Path.GetFileName(sub), manual) != null) { found.Add(sub); continue; }   // a system: don't look inside
                FindGrouped(sub, index, manual, found, depth + 1);
            }
        }

        async void Scan()
        {
            if (busy) { if (cts != null) cts.Cancel(); return; }
            string root = romBox.Text.Trim();
            if (!Directory.Exists(root))
            {
                ThemedBox.Show(this, "Choose the folder that holds your No-Intro set first.", Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            settings.RomFolder = root;
            if (index == null || !SamePath(index.Root, datBox.Text)) await LoadDats();
            if (index.ByName.Count == 0)
            {
                ThemedBox.Show(this, "No DAT files were found in the DAT folder. Choose the folder where your DAT pack is, or use DAT Files > Import.", Text);
                return;
            }

            SetBusy(true);
            results.Clear();
            changesButton.Visible = false;
            RefreshAll();
            cts = new CancellationTokenSource();
            var ct = cts.Token;
            var opts = settings.ScanOptions();
            var manual = settings.DatMap();
            var idx = index;
            var report = new Progress<string[]>(p => Progress(p[0], int.Parse(p[1]), int.Parse(p[2])));
            var added = new Progress<SystemResult>(r => { results.Add(r); RefreshSystems(); });
            var sw = Stopwatch.StartNew();
            try
            {
                string datRoot = datBox.Text.Trim(), cueRoot = settings.CueFolder;
                await Task.Run(() =>
                {
                    // Cue sheet packs (for discs whose cue sheet is out of date), kept for rescans after fixes.
                    cues = CueIndex.Build(root, datRoot, cueRoot);
                    opts.Cues = cues;
                    var folders = SystemFolders(root, idx, manual);
                    for (int i = 0; i < folders.Count; i++)
                    {
                        int n = i;
                        var r = Scanner.ScanSystem(folders[i], idx, manual, opts, (name, done, total) =>
                            ((IProgress<string[]>)report).Report(new[] {
                                "Scanning " + name + " (" + (n + 1) + " of " + folders.Count + "): " + done + " of " + total + " files",
                                (n * 1000 + (total == 0 ? 1000 : done * 1000 / total)).ToString(CultureInfo.InvariantCulture),
                                (folders.Count * 1000).ToString(CultureInfo.InvariantCulture) }), ct);
                        ((IProgress<SystemResult>)added).Report(r);
                    }
                }, ct);
                await Task.Yield();
                ShowCueRow();
                UpdateDatLabel();
                string scanned = "Scanned " + results.Count + " folder" + (results.Count == 1 ? "" : "s") + " in " + sw.Elapsed.TotalSeconds.ToString("0.0") + "s";
                statusLabel.Text = scanned;
                await CompareWithLastScan(scanned);
            }
            catch (OperationCanceledException) { statusLabel.Text = "Scan stopped"; }
            catch (Exception ex)
            {
                statusLabel.Text = "Scan failed";
                ThemedBox.Show(this, "The scan failed:\n\n" + ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally { SetBusy(false); RefreshAll(); }
        }

        async Task Rescan(IEnumerable<string> folders)
        {
            var list = folders.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            SetBusy(true);
            cts = new CancellationTokenSource();
            var ct = cts.Token;
            var opts = settings.ScanOptions();
            opts.Cues = cues;
            var manual = settings.DatMap();
            var idx = index;
            try
            {
                var fresh = await Task.Run(() => list.Select(f => Scanner.ScanSystem(f, idx, manual, opts, null, ct)).ToList(), ct);
                foreach (var r in fresh)
                {
                    int i = results.FindIndex(x => SamePath(x.Folder, r.Folder));
                    if (i >= 0) results[i] = r; else results.Add(r);
                }
                // Changes made in the app (fixes, deletes, added ROMs) become part of the record too, so the next
                // scan only reports what changed outside it.
                string collection = settings.Collection;
                await Task.Run(() =>
                {
                    string path = ScanHistory.PathFor(collection);
                    var history = ScanHistory.Load(path);
                    ScanHistory.Update(history, fresh);
                    ScanHistory.Save(path, history);
                });
            }
            catch (OperationCanceledException) { }
            finally { SetBusy(false); RefreshAll(); }
        }

        // ------------------------------------------------------------ changes since the last scan

        List<SystemChanges> lastChanges;

        // After a full scan: compares it with the record of the last one, says what changed, and saves the new record.
        async Task CompareWithLastScan(string scanned)
        {
            string path = ScanHistory.PathFor(settings.Collection);
            var now = results.ToList();
            List<SystemChanges> changes = null;
            await Task.Run(() =>
            {
                var history = ScanHistory.Load(path);
                if (history.Count > 0) changes = ScanHistory.Compare(history, now);
                ScanHistory.Update(history, now);
                ScanHistory.Save(path, history);
            });
            lastChanges = changes;
            statusLabel.Text = scanned + (changes == null ? "" : ". " + ScanHistory.Summary(changes));
            changesButton.Visible = changes != null && changes.Count > 0;
        }

        void ShowChanges()
        {
            if (lastChanges == null || lastChanges.Count == 0) return;
            using (var f = new TextForm(Text, ScanHistory.Summary(lastChanges) + ":", ScanHistory.Details(lastChanges))) f.ShowDialog(this);
        }

        // ------------------------------------------------------------ showing results

        SystemResult SelectedSystem()
        {
            if (systemList.SelectedItems.Count == 0) return null;
            return systemList.SelectedItems[0].Tag as SystemResult;
        }

        List<SystemResult> Scope()
        {
            var sys = SelectedSystem();
            return sys != null ? new List<SystemResult> { sys } : results.ToList();
        }

        List<FileResult> ScopeFiles(Func<FileResult, bool> pick)
        {
            return Scope().SelectMany(s => s.Files).Where(pick).ToList();
        }

        void RefreshAll()
        {
            RefreshSystems();
            RefreshRows();
        }

        void RefreshSystems()
        {
            var selected = SelectedSystem();
            string selectedFolder = selected != null ? selected.Folder : null;
            systemList.BeginUpdate();
            systemList.Items.Clear();
            if (results.Count > 0)
            {
                var all = new ListViewItem(new[] {
                    "All systems (" + results.Count + ")",
                    results.Sum(s => s.Have) + " / " + results.Sum(s => s.Total),
                    results.Sum(s => s.MissingCount).ToString(),
                    results.Sum(s => s.Count(f => f.IsFixable)).ToString(),
                    results.Sum(s => s.Count(f => f.IsUnneeded)).ToString(),
                    (results.Sum(s => s.Count(f => f.IsProblem)) + results.Count(s => s.Error != null)).ToString() });
                all.Font = new Font(systemList.Font, FontStyle.Bold);
                StatusColours(all);
                systemList.Items.Add(all);
            }
            foreach (var s in results)
            {
                ListViewItem item;
                if (s.Error != null)
                {
                    item = new ListViewItem(new[] { s.FolderName, "", "", "", "", s.Error });
                    item.ForeColor = Theme.Current.Gray;
                }
                else
                {
                    item = new ListViewItem(new[] {
                        s.FolderName, s.Have + " / " + s.Total, s.MissingCount.ToString(), s.Count(f => f.IsFixable).ToString(),
                        s.Count(f => f.IsUnneeded).ToString(), s.Count(f => f.IsProblem).ToString() });
                    StatusColours(item);
                }
                item.Tag = s;
                systemList.Items.Add(item);
                if (selectedFolder != null && SamePath(s.Folder, selectedFolder)) item.Selected = true;
            }
            systemList.EndUpdate();
            if (settings.AutoFitColumns) FitSystemColumns();
        }

        // Each count is green when it's 0 and red when it isn't. Have goes with Missing, and the system's
        // name is red if anything at all needs doing, so problem systems still stand out in a long list.
        static void StatusColours(ListViewItem item)
        {
            item.UseItemStyleForSubItems = false;
            bool clean = true;
            for (int i = 2; i <= 5 && i < item.SubItems.Count; i++)
            {
                int n;
                bool bad = int.TryParse(item.SubItems[i].Text, out n) && n > 0;
                item.SubItems[i].ForeColor = bad ? Theme.Current.Bad : Theme.Current.Good;
                if (bad) clean = false;
            }
            if (item.SubItems.Count > 2) item.SubItems[1].ForeColor = item.SubItems[2].ForeColor;
            item.ForeColor = item.SubItems[0].ForeColor = clean ? Theme.Current.Good : Theme.Current.Bad;
        }

        // The tab titles follow the same rule: Missing (0) in green, Missing (3) in red. Correct stays plain,
        // and so does everything before the first scan.
        int[] tabTotals;

        void ColourTabs()
        {
            var tabsToColour = new[] { missingTab, misnamedTab, unneededTab, problemTab };
            for (int i = 0; i < tabsToColour.Length; i++)
                tabsToColour[i].ForeColor = tabTotals == null || results.Count == 0 ? Theme.Current.Text
                                          : tabTotals[i] > 0 ? Theme.Current.Bad : Theme.Current.Good;
        }

        static string Sys(SystemResult s)
        {
            return s.Dat != null ? s.Dat.Name : s.FolderName;
        }

        void RefreshRows()
        {
            var scope = Scope();
            string filter = filterBox.Text.Trim();
            Func<Row, bool> keep = r => filter.Length == 0 || r.Cells.Any(c => c != null && c.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0);

            var missing = new List<Row>();
            var misnamed = new List<Row>();
            var unneeded = new List<Row>();
            var problems = new List<Row>();
            var ok = new List<Row>();
            foreach (var s in scope)
            {
                if (s.Error != null) problems.Add(new Row { System = s, Cells = new[] { s.FolderName, "(the whole folder)", s.Error } });
                foreach (var g in s.Missing)
                {
                    if (g.Mia && !miaBox.Checked) continue;
                    var notes = new List<string>();
                    if (g.Mia) notes.Add("MIA (no known dump)");
                    if (g.BadDump) notes.Add("Only a bad dump is known");
                    if ((g.Type & GameType.Aftermarket) != 0) notes.Add("Aftermarket ROM: right-click to look for it on itch.io or its developer's site");
                    string note;
                    if (s.MissingNote.TryGetValue(g, out note)) notes.Add(note);
                    missing.Add(new Row { System = s, Game = g, Cells = new[] { Sys(s), g.Name, Crcs(g), string.Join("; ", notes) } });
                }
                foreach (var f in s.Files)
                {
                    if (f.IsFixable) misnamed.Add(new Row { System = s, File = f, Cells = new[] { Sys(s), CurrentName(f), CorrectName(f), f.Detail } });
                    else if (f.IsUnneeded) unneeded.Add(new Row { System = s, File = f, Size = f.UnneededSize, Cells = new[] { Sys(s), f.UnneededName, Naming.Size(f.UnneededSize), f.Detail } });
                    else if (f.IsProblem || f.Kind == FileKind.Mixed) problems.Add(new Row { System = s, File = f, Cells = new[] { Sys(s), f.Name, f.Detail } });
                    else if (f.Kind == FileKind.Ok || f.Kind == FileKind.Kept) ok.Add(new Row { System = s, File = f, Cells = new[] { Sys(s), f.Name, f.Detail } });
                }
            }
            missingList.SetRows(missing.Where(keep).ToList());
            misnamedList.SetRows(misnamed.Where(keep).ToList());
            unneededList.SetRows(unneeded.Where(keep).ToList());
            problemList.SetRows(problems.Where(keep).ToList());
            okList.SetRows(ok.Where(keep).ToList());

            Func<int, int, string> count = (shown, total) => shown == total ? total.ToString("N0") : shown.ToString("N0") + " of " + total.ToString("N0");
            missingTab.Text = "Missing (" + count(missingList.Rows.Count, missing.Count) + ")";
            misnamedTab.Text = "Wrong Name (" + count(misnamedList.Rows.Count, misnamed.Count) + ")";
            unneededTab.Text = "Unneeded (" + count(unneededList.Rows.Count, unneeded.Count) + ")";
            problemTab.Text = "Problems (" + count(problemList.Rows.Count, problems.Count) + ")";
            okTab.Text = "Correct (" + count(okList.Rows.Count, ok.Count) + ")";
            tabTotals = new[] { missing.Count, misnamed.Count, unneeded.Count, problems.Count };
            ColourTabs();
            if (settings.AutoFitColumns) FitTabColumns();
            UpdateButtons();
        }

        void UpdateButtons()
        {
            if (busy) return;
            int fix = ScopeFiles(f => f.IsFixable && f.CanChange).Count;
            var del = ScopeFiles(f => f.IsUnneeded && f.CanChange);
            string where = SelectedSystem() != null ? " in This System" : "";
            fixButton.Text = "Fix Wrong Names" + where + " (" + fix.ToString("N0") + ")";
            deleteButton.Text = "Delete Unneeded" + where + " (" + del.Count.ToString("N0") + (del.Count > 0 ? ", " + Naming.Size(del.Sum(f => f.UnneededSize)) : "") + ")";
            fixButton.Enabled = fix > 0;
            deleteButton.Enabled = del.Count > 0;
            exportButton.Enabled = results.Count > 0;
            addButton.Enabled = results.Any(r => r.Error == null);
        }

        // ------------------------------------------------------------ actions

        // Adds files to (or takes them off) the ignore list, then checks their systems again. Files on the list
        // are kept: they're never listed as unneeded or deleted.
        async void SetIgnored(List<FileResult> files, bool ignore)
        {
            if (busy) return;
            foreach (var g in files.GroupBy(f => f.System))
            {
                settings.SetIgnored(g.Key.Dat.Name, g.Select(f => f.Name), ignore);
                Log.Write((ignore ? "Added to ignore list" : "Removed from ignore list") + " (" + g.Key.Dat.Name + "): " + string.Join(", ", g.Select(f => f.Name)));
            }
            await Rescan(files.Select(f => f.System.Folder).ToList());
            statusLabel.Text = (ignore ? "Added " : "Removed ") + Count(files.Count, "file") + (ignore ? " to" : " from") + " the ignore list";
        }

        void ShowListMenu(ResultList list, Point at)
        {
            var rows = list.SelectedRows();
            if (rows.Count == 0) return;
            var files = rows.Where(r => r.File != null).Select(r => r.File).ToList();
            listMenu.Items.Clear();
            var fixable = files.Where(f => f.IsFixable && f.CanChange).ToList();
            var deletable = files.Where(f => f.Kind != FileKind.Ok && !f.IsFixable && f.CanChange).ToList();
            if (fixable.Count > 0)
                Tips.Item(listMenu.Items.Add("Rename " + Plural(fixable.Count, "Selected File"), null, (s, e) => FixFiles(fixable)),
                    "Gives the selected files their names from the DAT, and zips loose ROMs.");
            if (deletable.Count > 0)
                Tips.Item(listMenu.Items.Add("Delete " + Plural(deletable.Count, "Selected File") + "...", null, (s, e) => DeleteFiles(deletable)),
                    "Deletes the selected files" + (settings.UseRecycleBin ? " (to the Recycle Bin)" : "") + ". You're asked first.");
            var ignorable = files.Where(f => (f.IsUnneeded || f.IsProblem) && f.System != null && f.System.Dat != null).ToList();
            var ignored = files.Where(f => f.OnIgnoreList && f.System != null && f.System.Dat != null).ToList();
            if (ignorable.Count > 0)
                Tips.Item(listMenu.Items.Add(ignorable.Count == 1 ? "Add to Ignore List (Always Keep)" : "Add " + Plural(ignorable.Count, "Selected File") + " to Ignore List (Always Keep)",
                                             null, (s, e) => SetIgnored(ignorable, true)),
                    "Keeps these files for good, such as a translation guide: they're no longer\nlisted as unneeded, and are never deleted.");
            if (ignored.Count > 0)
                Tips.Item(listMenu.Items.Add(ignored.Count == 1 ? "Remove from Ignore List" : "Remove " + Plural(ignored.Count, "Selected File") + " from Ignore List",
                                             null, (s, e) => SetIgnored(ignored, false)),
                    "Takes these files off the ignore list, so they're checked like any other file again.");
            if (listMenu.Items.Count > 0) listMenu.Items.Add(new ToolStripSeparator());
            Tips.Item(listMenu.Items.Add("Show in Folder", null, (s, e) => ShowInFolder(rows[0])),
                "Opens the folder in File Explorer, with the file selected.");
            Tips.Item(listMenu.Items.Add("Copy Names", null, (s, e) => CopyNames(list)),
                "Copies the names of the selected rows, one per line, to paste elsewhere.");
            if (rows[0].Game != null)
                Tips.Item(listMenu.Items.Add("Copy CRC32", null, (s, e) =>
                {
                    var text = string.Join(Environment.NewLine, list.SelectedRows().Where(r => r.Game != null).Select(r => Crcs(r.Game)));
                    if (text.Length > 0) Clipboard.SetText(text);
                }), "Copies the CRC32 checksums the DAT gives for the selected ROMs,\nhandy for checking a download is the right one.");
            if (rows[0].Game != null)
                Tips.Item(listMenu.Items.Add("Search the Web for This ROM", null, (s, e) => OpenUrl("https://www.google.com/search?q=" + Uri.EscapeDataString(rows[0].Game.Name))),
                    "Searches Google for the ROM's name, in your web browser.");
            // Aftermarket (homebrew) games are usually sold or given away by their developers, mostly on itch.io.
            if (rows[0].Game != null && (rows[0].Game.Type & GameType.Aftermarket) != 0)
            {
                string title = Title(rows[0].Game.Name), system = SystemWords(rows[0].System);
                listMenu.Items.Add(new ToolStripSeparator());
                Tips.Item(listMenu.Items.Add("Find on itch.io", null, (s, e) => OpenUrl("https://itch.io/search?q=" + Uri.EscapeDataString(title))),
                    "Searches itch.io, where most homebrew games are sold or given away by their developers.");
                Tips.Item(listMenu.Items.Add("Search for the Developer's Page", null, (s, e) =>
                    OpenUrl("https://www.google.com/search?q=" + Uri.EscapeDataString("\"" + title + "\" " + system + " homebrew developer"))),
                    "Searches Google for the developer of this homebrew game, to buy or download it from them.");
            }
            foreach (ToolStripItem i in listMenu.Items) i.Enabled = !busy || i is ToolStripSeparator;
            listMenu.Show(list, at);
        }

        // When the name of the file inside the zip is wrong, show it too: "Game.zip › Game.a78".
        static bool InsideWrong(FileResult f)
        {
            return f.IsSet && f.Game != null && f.Items != null &&
                   f.Game.Roms.Any(r => !f.Items.Any(i => Scanner.SameData(i, r) && i.Name == Scanner.ExpectedName(r, i)));
        }

        // Only the files inside whose names are wrong (a disc can have many tracks).
        static List<Tuple<string, string>> WrongInside(FileResult f)
        {
            var list = new List<Tuple<string, string>>();
            foreach (var r in f.Game.Roms)
            {
                var i = f.Items.FirstOrDefault(x => Scanner.SameData(x, r));
                if (i != null && i.Name != Scanner.ExpectedName(r, i)) list.Add(Tuple.Create(i.Name, Scanner.ExpectedName(r, i)));
            }
            return list;
        }

        static string CurrentName(FileResult f)
        {
            if (!InsideWrong(f)) return f.Name;
            var w = WrongInside(f);
            return f.Name + "  ›  " + string.Join(", ", w.Take(2).Select(x => x.Item1)) + (w.Count > 2 ? " and " + (w.Count - 2) + " more" : "");
        }

        static string CorrectName(FileResult f)
        {
            if (!InsideWrong(f)) return f.TargetName;
            var w = WrongInside(f);
            return f.TargetName + "  ›  " + string.Join(", ", w.Take(2).Select(x => x.Item2)) + (w.Count > 2 ? " and " + (w.Count - 2) + " more" : "");
        }

        // "Xeno Crisis (World) (Aftermarket) (Unl)" gives "Xeno Crisis".
        static string Title(string name)
        {
            return System.Text.RegularExpressions.Regex.Replace(name, @"\s*[\(\[][^\)\]]*[\)\]]", "").Trim();
        }

        // "Sega - Mega Drive - Genesis" gives "Mega Drive Genesis", for web searches.
        static string SystemWords(SystemResult s)
        {
            if (s == null || s.Dat == null) return "";
            string n = s.Dat.Name;
            int i = n.IndexOf(" - ", StringComparison.Ordinal);
            if (i >= 0) n = n.Substring(i + 3);
            return Title(n).Replace(" - ", " ");
        }

        // The CRC32 of each file in the game, as written in the DAT (most games have just one).
        static string Crcs(DatGame g)
        {
            return string.Join(", ", g.Roms.Select(r => r.Sha1 != null ? "SHA1 " + r.Sha1 : r.Crc.ToString("X8")));
        }

        // "1 file", "3 files".
        static string Count(int n, string what)
        {
            return n.ToString("N0") + " " + what + (n == 1 ? "" : "s");
        }

        static string Plural(int n, string what)
        {
            return n == 1 ? what : n.ToString("N0") + " " + what + "s";
        }

        void DeleteSelected(ResultList list)
        {
            if (busy) return;
            var files = list.SelectedRows().Where(r => r.File != null && r.File.Kind != FileKind.Ok && !r.File.IsFixable && r.File.CanChange).Select(r => r.File).ToList();
            if (files.Count > 0) DeleteFiles(files);
        }

        void CopyNames(ResultList list)
        {
            var rows = list.SelectedRows();
            if (rows.Count == 0) rows = list.Rows;
            var text = string.Join(Environment.NewLine, rows.Select(r => r.Game != null ? r.Game.Name : r.File != null && r.File.Kind == FileKind.ExtraInside ? r.File.Name : r.Cells[1]));
            if (text.Length > 0) Clipboard.SetText(text);
        }

        void ShowInFolder(Row r)
        {
            if (r.File != null && File.Exists(r.File.Path)) Process.Start("explorer.exe", "/select,\"" + r.File.Path + "\"");
            else if (r.System != null && r.System.IsPack && File.Exists(r.System.Folder)) Process.Start("explorer.exe", "/select,\"" + r.System.Folder + "\"");
            else if (r.System != null) OpenFolder(r.System.Folder);
        }

        static void OpenFolder(string path)
        {
            if (Directory.Exists(path)) Process.Start("explorer.exe", "\"" + path + "\"");
        }

        static void OpenUrl(string url)
        {
            try { Process.Start(url); } catch { }
        }

        async void FixFiles(List<FileResult> files)
        {
            if (busy || files.Count == 0) return;
            int rebuild = files.Count(f => f.IsArchive && f.NeedsRebuild);
            int zipping = files.Count(f => !f.IsSet && !f.LooseChd);
            string msg = "Rename " + Count(files.Count, "file") + " to " + (files.Count == 1 ? "its name" : "their names") + " in the DAT?";
            int cueFixes = files.Count(f => f.FixCue);
            if (rebuild > 0) msg += "\n\n" + rebuild.ToString("N0") + " of them also have files renamed inside" +
                                    (cueFixes > 0 ? " (" + cueFixes.ToString("N0") + " with a cue sheet replaced from your cue sheet packs)" : "") +
                                    ". Each one is checked against the DAT before it's kept.";
            if (zipping > 0) msg += "\n\n" + zipping.ToString("N0") + " unzipped file" + (zipping == 1 ? " is" : "s are") + " zipped, and the unzipped copy removed.";
            var keptPacks = files.Where(f => f.InPack && f.System.KeepPack).Select(f => f.System).Distinct().ToList();
            var unpacking = files.Where(f => f.InPack && !f.System.KeepPack).Select(f => f.System).Distinct().ToList();
            if (keptPacks.Count > 0)
                msg += "\n\n" + Count(keptPacks.Count, "TOSEC pack") + " (" + Naming.Size(keptPacks.Sum(s => new FileInfo(s.Folder).Length)) + ") " +
                       (keptPacks.Count == 1 ? "is" : "are") + " rewritten with the games renamed inside. Each new copy is checked by unpacking every file before it replaces the old one, and needs that much free space while it's made.";
            if (unpacking.Count > 0)
            {
                msg += "\n\n" + Count(unpacking.Count, "TOSEC pack") + " (" + Naming.Size(unpacking.Sum(s => new FileInfo(s.Folder).Length)) + ") " +
                       (unpacking.Count == 1 ? "is" : "are") + " unpacked into one zip per game, in a folder named after the DAT:\n";
                msg += string.Join("\n", unpacking.Take(6).Select(s => "      " + Path.GetFileName(Packs.UnpackFolder(s)))) + (unpacking.Count > 6 ? "\n      ...and " + (unpacking.Count - 6) + " more" : "");
                msg += "\nEvery game in the pack is unpacked, including ones not in the DAT (they keep their names, and show as unneeded afterwards). " +
                       "Each zip is checked by unpacking it, and then the pack is permanently deleted. If anything doesn't check out, the pack is kept.";
            }
            int extraChds = files.Where(f => f.IsFolder && f.Game != null).Sum(f => f.Items.Count(i => !f.Game.Roms.Any(r => Scanner.SameData(i, r))));
            if (extraChds > 0) msg += "\n\n" + extraChds.ToString("N0") + " CHD" + (extraChds == 1 ? "" : "s") + " in these folders " + (extraChds == 1 ? "isn't" : "aren't") + " in the DAT and will be permanently deleted.";
            if (ThemedBox.Show(this, msg, Text, MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;

            SetBusy(true);
            cts = new CancellationTokenSource();
            var ct = cts.Token;
            var report = new Progress<int[]>(p => Progress("Renaming: " + p[0] + " of " + p[1], p[0], p[1]));
            List<string> lines = null;
            try
            {
                lines = await Task.Run(() => Actions.FixAll(files, (d, t) => ((IProgress<int[]>)report).Report(new[] { d, t }), ct), ct);
            }
            catch (OperationCanceledException) { lines = new List<string> { "Stopped part way. Scan again to see where things are." }; }
            finally { SetBusy(false); }
            if (unpacking.Count > 0) { ShowOutcome("Rename", lines); Scan(); return; }   // packs became folders: look again
            await Rescan(files.Select(f => f.System.Folder));
            ShowOutcome("Rename", lines);
        }

        async void DeleteFiles(List<FileResult> files)
        {
            if (busy || files.Count == 0) return;
            // Whole files, and zips that keep their ROM and only lose the extra files inside.
            var whole = files.Where(f => f.Kind != FileKind.ExtraInside).ToList();
            var inside = files.Where(f => f.Kind == FileKind.ExtraInside).ToList();
            long size = whole.Sum(f => f.Length);
            var kinds = whole.GroupBy(f => f.Kind).Select(g => g.Count().ToString("N0") + " " + KindText(g.Key, g.Count())).ToList();
            bool recycle = settings.UseRecycleBin;
            string msg = "";
            if (whole.Count > 0)
                msg = (recycle ? "Move " + Count(whole.Count, "file") + " (" + Naming.Size(size) + ") to the Recycle Bin?\n\n"
                               : "Permanently delete " + Count(whole.Count, "file") + " (" + Naming.Size(size) + ")?\n\n") +
                      string.Join("\n", kinds) +
                      (recycle ? "\n\nIf Windows can't recycle something (too big, or on a network drive), it asks before deleting it permanently."
                               : "\n\nThis can't be undone. (To use the Recycle Bin instead, tick Options > Delete to the Recycle Bin.)");
            if (inside.Count > 0)
            {
                int extras = inside.Sum(f => f.ExtraItems == null ? 0 : f.ExtraItems.Count);
                msg += (whole.Count > 0 ? "\n\nAlso take " : "Take ") + Count(extras, "extra file") + " (" + Naming.Size(inside.Sum(f => f.UnneededSize)) + ") out of " +
                       Count(inside.Count, "zip") + (whole.Count > 0 ? "." : "?") + " The ROMs in them are kept, and each zip is checked against the DAT afterwards. " +
                       "Files taken out of a zip are deleted outright: they don't go to the Recycle Bin.";
            }
            var packs = files.Where(f => f.InPack).Select(f => f.System).Distinct().ToList();
            if (packs.Count > 0)
                msg += "\n\nGames inside " + Count(packs.Count, "TOSEC pack") + " are taken out by rewriting the pack (" +
                       Naming.Size(packs.Sum(s => new FileInfo(s.Folder).Length)) + "), which needs that much free space while it's made. " +
                       "The new copy is checked by unpacking every file before it replaces the old one. These can't go to the Recycle Bin.";
            if (ThemedBox.Show(this, msg, Text, MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;

            SetBusy(true);
            cts = new CancellationTokenSource();
            var ct = cts.Token;
            var report = new Progress<int[]>(p => Progress("Deleting: " + p[0] + " of " + p[1], p[0], p[1]));
            var lines = new List<string>();
            try
            {
                lines = await Task.Run(() => Actions.DeleteAll(files, (d, t) => ((IProgress<int[]>)report).Report(new[] { d, t }), ct), ct);
            }
            catch (OperationCanceledException) { lines.Add("Stopped part way."); }
            finally { SetBusy(false); }
            await Rescan(files.Select(f => f.System.Folder));
            ShowOutcome("Delete", lines, files);
        }

        static string KindText(FileKind k, int n)
        {
            switch (k)
            {
                case FileKind.NotInDat: return "not in the DAT";
                case FileKind.Duplicate: return n == 1 ? "duplicate" : "duplicates";
                case FileKind.NotPreferred: return "other version" + (n == 1 ? "" : "s") + " of a title (1G1R)";
                case FileKind.Excluded: return "left out (ROM types you don't keep)";
                case FileKind.Unreadable: return "unreadable";
                case FileKind.Unsupported: return "not a .zip";
                case FileKind.Incomplete: return "incomplete";
                case FileKind.Mixed: return "holding more than one ROM";
                case FileKind.ExtraInside: return "with extra files inside";
                default: return k.ToString();
            }
        }

        // files: what was deleted, so zips that only lost their extra files are counted by those files.
        void ShowOutcome(string what, List<string> lines, List<FileResult> files = null)
        {
            var failed = lines.Where(l => l.StartsWith("COULDN'T") || l.StartsWith("Stopped")).ToList();
            int ok = lines.Count - failed.Count;
            string summary;
            if (what == "Rename") summary = "Renamed " + Count(ok, "file") + ".";
            else
            {
                var took = (files ?? new List<FileResult>()).Where(f => f.Kind == FileKind.ExtraInside && f.ExtraItems != null &&
                                                                       lines.Contains("Took the extra files out of " + f.Name)).ToList();
                int deleted = ok - took.Count;
                var parts = new List<string>();
                if (deleted > 0 || took.Count == 0) parts.Add("Deleted " + Count(deleted, "file"));
                if (took.Count > 0)
                    parts.Add((parts.Count > 0 ? "took " : "Took ") + Count(took.Sum(f => f.ExtraItems.Count), "extra file") + " out of " + Count(took.Count, "zip"));
                summary = string.Join(", and ", parts) + ".";
            }
            statusLabel.Text = summary + (failed.Count > 0 ? " " + failed.Count + " didn't work." : "");
            if (failed.Count == 0) { ThemedBox.Show(this, summary, Text, MessageBoxButtons.OK, MessageBoxIcon.Information); return; }
            using (var f = new TextForm(Text, summary + " These didn't work:", string.Join(Environment.NewLine, failed)))
                f.ShowDialog(this);
        }

        // ------------------------------------------------------------ adding missing games from a folder

        // Starts or stops watching the downloads folder, to match the tick box.
        void UpdateWatcher(bool userClicked)
        {
            if (watcher != null) { watcher.Dispose(); watcher = null; }
            if (!autoBox.Checked) { statusLabel.Text = userClicked ? "Stopped watching for downloads" : statusLabel.Text; return; }
            if (!Directory.Exists(settings.IncomingFolder))
            {
                if (!userClicked || !ChooseIncomingFolder()) { loading = true; autoBox.Checked = false; loading = false; settings.AutoAdd = false; return; }
            }
            try
            {
                watcher = new DownloadWatcher(this, settings.IncomingFolder, () => !busy && OwnedForms.Length == 0 && results.Any(r => r.Error == null));
            }
            catch (Exception ex)
            {
                ThemedBox.Show(this, "Couldn't watch " + settings.IncomingFolder + ":\n\n" + ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
                loading = true; autoBox.Checked = false; loading = false; settings.AutoAdd = false;
                return;
            }
            watcher.Ready += files => AutoAdd(files);
            watcher.AddExisting();
            statusLabel.Text = "Watching " + settings.IncomingFolder + " for downloads";
            if (userClicked && !results.Any(r => r.Error == null) && Directory.Exists(romBox.Text.Trim()) && !busy) Scan();
        }

        // Adds the missing games in newly downloaded files, without asking.
        async void AutoAdd(List<string> files)
        {
            if (busy) return;
            string folder = settings.IncomingFolder;
            bool remove = settings.RemoveDownloads;
            var idx = index;
            var snapshot = results.ToList();
            SetBusy(true);
            cts = new CancellationTokenSource();
            var ct = cts.Token;
            statusLabel.Text = "Checking " + Count(files.Count, "new download") + "...";
            List<IntakeFile> plan = null;
            List<string> lines = null;
            try
            {
                // The whole folder is checked, not just the new files, so a ROM made of several files
                // is added once its last file arrives. Files already checked aren't read again.
                plan = await Task.Run(() => Intake.Plan(folder, snapshot, idx, null, ct), ct);
                if (Intake.Adds(plan).Count > 0)
                    lines = await Task.Run(() => Intake.Apply(plan, remove, null, ct), ct);
            }
            catch (Exception ex) { lines = new List<string> { "COULDN'T ADD: " + ex.Message }; }
            finally { SetBusy(false); }

            if (lines != null)
            {
                await Rescan(Intake.Adds(plan).Select(m => m.System.Folder));
                foreach (var m in Intake.Adds(plan).Where(m => File.Exists(m.Target)))
                    Toast.Show(this, "Added to your set", m.Game.Name + "\nto " + m.System.FolderName);
                foreach (var line in lines.Where(l => l.StartsWith("COULDN'T")))
                    Toast.Show(this, "Couldn't add a download", line.Replace("COULDN'T ADD ", ""));
            }
            // New downloads that aren't (yet) missing ROMs are left alone; say so quietly in the status bar.
            var newFiles = new HashSet<string>(files, StringComparer.OrdinalIgnoreCase);
            var others = plan == null ? new List<IntakeFile>() : plan.Where(f => newFiles.Contains(f.Path) && !f.Matches.Any(m => m.Error == null)).ToList();
            int added = lines == null ? 0 : lines.Count(l => l.StartsWith("Added "));
            statusLabel.Text = (added > 0 ? "Added " + Count(added, "ROM") + ". " : "") +
                               (others.Count == 1 ? others[0].Name + ": " + others[0].Outcome : others.Count > 1 ? others.Count + " downloads weren't missing ROMs." : "") +
                               (added == 0 && others.Count == 0 ? "Watching " + folder + " for downloads" : "");
        }

        // The DAT line: how many DATs and, for disc sets, how many cue sheets were found, so a wrong
        // cue sheets folder shows straight away.
        void UpdateDatLabel()
        {
            var packs = DatIndex.PackFolders(index.Root ?? "");
            datLabel.Text = index.ByName.Count + " DATs" +
                (packs.Count > 0 ? " (newest pack: " + Path.GetFileName(packs.Last()) + ")" : "") +
                (index.Errors.Count > 0 ? ", " + index.Errors.Count + " couldn't be read" : "") +
                (cues != null && cueRow ? ", " + cues.Summary : "");
        }

        bool cueRow;

        // The cue sheets folder is only needed for disc sets (Redump's DATs say so in their header), so it's
        // hidden otherwise, unless one has been chosen.
        bool ShowCueRow()
        {
            keepPacksBox.Visible = index != null && index.IsTosec;   // only TOSEC sets come as packs
            cueRow = (index != null && index.UsesCueSheets) || settings.CueFolder.Length > 0;
            cueLabel.Visible = cueBox.Visible = cueBrowse.Visible = cueRow;
            return cueRow;
        }

        // Reads the cue sheets again after the folder changes, then checks the lists again, since discs are
        // judged against them.
        async Task RefreshCues()
        {
            string romRoot = romBox.Text.Trim(), datRoot = datBox.Text.Trim(), cueRoot = settings.CueFolder;
            ShowCueRow();
            SetBusy(true);
            Progress("Reading cue sheets...", 0, 0);
            try
            {
                cues = await Task.Run(() => CueIndex.Build(romRoot, datRoot, cueRoot));
                UpdateDatLabel();
                statusLabel.Text = "Ready";
            }
            finally { SetBusy(false); }
            if (results.Count > 0) await Rescan(results.Select(r => r.Folder).ToList());
        }

        // Takes a cue sheets folder typed into the box, checking it first.
        async void ApplyCueBox()
        {
            if (busy) return;
            string typed = cueBox.Text.Trim().TrimEnd('\\');
            if (SamePath(typed, settings.CueFolder)) return;
            if (typed.Length > 0 && !Directory.Exists(typed))
            {
                ThemedBox.Show(this, "The cue sheets folder \"" + typed + "\" doesn't exist.", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                cueBox.Text = settings.CueFolder;
                return;
            }
            settings.CueFolder = typed;
            await RefreshCues();
        }

        async void ChooseCueFolder()
        {
            using (var d = new FolderBrowserDialog { Description = "Choose the folder that holds your cue sheet packs (the Cuesheets zips from redump.org) or cue sheets. Subfolders are searched too.", ShowNewFolderButton = false })
            {
                string current = settings.CueFolder, rom = romBox.Text.Trim();
                d.SelectedPath = Directory.Exists(current) ? current : (Directory.Exists(rom) ? rom : "");
                if (d.ShowDialog(this) != DialogResult.OK) return;
                string chosen = d.SelectedPath.TrimEnd('\\');
                cueBox.Text = chosen;
                if (SamePath(chosen, settings.CueFolder)) return;
                settings.CueFolder = chosen;
                await RefreshCues();
            }
        }

        // Takes a downloads folder typed into the box, checking it first.
        void ApplyDownloadsBox()
        {
            string typed = dlBox.Text.Trim().TrimEnd('\\');
            if (SamePath(typed, settings.IncomingFolder)) return;
            string root = romBox.Text.Trim().TrimEnd('\\');
            string problem = typed.Length == 0 ? null
                           : !Directory.Exists(typed) ? "The missing ROMs folder \"" + typed + "\" doesn't exist."
                           : root.Length > 0 && (SamePath(typed, root) || typed.StartsWith(root + "\\", StringComparison.OrdinalIgnoreCase))
                             ? "That folder is part of your ROM set. Choose the separate folder you download missing ROMs into." : null;
            if (problem != null)
            {
                ThemedBox.Show(this, problem, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                dlBox.Text = settings.IncomingFolder;
                return;
            }
            settings.IncomingFolder = typed;
            if (watcher != null || autoBox.Checked) UpdateWatcher(false);
        }

        bool ChooseIncomingFolder()
        {
            using (var d = new FolderBrowserDialog { Description = "Choose your missing ROMs folder (where you download missing ROMs to)", ShowNewFolderButton = true })
            {
                string current = settings.IncomingFolder;
                string downloads = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
                d.SelectedPath = Directory.Exists(current) ? current : (Directory.Exists(downloads) ? downloads : "");
                if (d.ShowDialog(this) != DialogResult.OK) return false;
                string root = romBox.Text.Trim();
                string chosen = d.SelectedPath.TrimEnd('\\');
                if (root.Length > 0 && (SamePath(chosen, root) || chosen.StartsWith(root.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase)))
                {
                    ThemedBox.Show(this, "That folder is part of your ROM set. Choose the separate folder you download missing ROMs into.", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return false;
                }
                settings.IncomingFolder = chosen;
                dlBox.Text = chosen;
                if (watcher != null && !SamePath(watcher.Folder, chosen)) BeginInvoke((Action)(() => UpdateWatcher(false)));
                return true;
            }
        }

        async void AddFromFolder()
        {
            if (busy) return;
            if (!results.Any(r => r.Error == null))
            {
                ThemedBox.Show(this, "Scan your ROM folder first, so the app knows which ROMs are missing.", Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            if (!Directory.Exists(settings.IncomingFolder) && !ChooseIncomingFolder()) return;

            List<IntakeFile> plan;
            bool remove;
            while (true)
            {
                string folder = settings.IncomingFolder;
                SetBusy(true);
                cts = new CancellationTokenSource();
                var ct = cts.Token;
                var idx = index;
                var snapshot = results.ToList();
                var report = new Progress<int[]>(p => Progress("Checking " + folder + ": " + p[0] + " of " + p[1] + " files", p[0], p[1]));
                try
                {
                    plan = await Task.Run(() => Intake.Plan(folder, snapshot, idx, (d, t) => ((IProgress<int[]>)report).Report(new[] { d, t }), ct), ct);
                }
                catch (OperationCanceledException) { statusLabel.Text = "Stopped"; return; }
                catch (Exception ex)
                {
                    ThemedBox.Show(this, "Couldn't check the folder:\n\n" + ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return;
                }
                finally { SetBusy(false); }
                statusLabel.Text = "Ready";

                using (var d = new IntakeForm(folder, plan, settings.RemoveDownloads))
                {
                    var answer = d.ShowDialog(this);
                    if (answer == DialogResult.Retry) { if (!ChooseIncomingFolder()) return; continue; }
                    if (answer != DialogResult.OK) return;
                    remove = d.RemoveDownloads;
                    settings.RemoveDownloads = remove;
                }
                break;
            }

            SetBusy(true);
            cts = new CancellationTokenSource();
            var token = cts.Token;
            var progressAdd = new Progress<int[]>(p => Progress("Adding ROMs: " + p[0] + " of " + p[1], p[0], p[1]));
            List<string> lines;
            try
            {
                lines = await Task.Run(() => Intake.Apply(plan, remove, (d, t) => ((IProgress<int[]>)progressAdd).Report(new[] { d, t }), token), token);
            }
            catch (OperationCanceledException) { lines = new List<string> { "Stopped part way." }; }
            finally { SetBusy(false); }

            await Rescan(Intake.Adds(plan).Select(m => m.System.Folder));
            int added = lines.Count(l => l.StartsWith("Added "));
            var failed = lines.Where(l => !l.StartsWith("Added ")).ToList();
            string summary = "Added " + Count(added, "ROM") + " to your set.";
            statusLabel.Text = summary + (failed.Count > 0 ? " " + failed.Count + " didn't work." : "");
            if (failed.Count == 0) ThemedBox.Show(this, summary, Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
            else using (var f = new TextForm(Text, summary + " These didn't work:", string.Join(Environment.NewLine, failed))) f.ShowDialog(this);
        }

        void ExportReport()
        {
            // Saved in the system's own folder, with a "!" at the start so it sorts to the top.
            // Scans ignore these reports, so they're never listed as unneeded.
            string fileName = Scanner.ReportPrefix + " " + DateTime.Now.ToString("yyyy-MM-dd") + ".txt";
            var sys = SelectedSystem();
            if (sys == null) { SaveReportPerSystem(fileName); return; }
            string folder = sys.IsPack ? Path.GetDirectoryName(sys.Folder) : sys.Folder;
            if (sys.IsPack) fileName = PackReportName(fileName, sys);
            using (var d = new SaveFileDialog { Title = "Save Report", Filter = "Text files (*.txt)|*.txt", FileName = fileName })
            {
                if (Directory.Exists(folder)) d.InitialDirectory = folder;
                if (d.ShowDialog(this) != DialogResult.OK) return;
                try
                {
                    File.WriteAllText(d.FileName, Report.Build(Scope(), miaBox.Checked), System.Text.Encoding.UTF8);
                    if (SamePath(Path.GetDirectoryName(d.FileName), folder)) RemoveOldReports(sys, d.FileName);
                    Process.Start(d.FileName);
                }
                catch (Exception ex) { ThemedBox.Show(this, "Couldn't save the report:\n\n" + ex.Message, Text); }
            }
        }

        // A TOSEC pack's report goes next to it, named after it, as several packs can share a folder.
        static string PackReportName(string fileName, SystemResult s)
        {
            string name = s.FolderName;
            foreach (char c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
            return Path.GetFileNameWithoutExtension(fileName) + " - " + name + ".txt";
        }

        // After saving a report: deletes the system's earlier reports in the same folder, so only the newest is kept.
        // Only files named exactly as the app names them are removed ("!ROM Check report 2026-11-23.txt", or for a
        // TOSEC pack "!ROM Check report 2026-11-23 - <pack>.txt", so other packs' reports in the folder are left alone).
        internal static void RemoveOldReports(SystemResult s, string kept)
        {
            string folder = Path.GetDirectoryName(kept);
            string ending = s.IsPack ? PackReportName("x.txt", s).Substring(1) : ".txt";   // " - <pack>.txt" or ".txt"
            var pattern = new Regex("^" + Regex.Escape(Scanner.ReportPrefix) + @" \d{4}-\d{2}-\d{2}" + Regex.Escape(ending) + "$", RegexOptions.IgnoreCase);
            foreach (var p in Directory.GetFiles(folder, Scanner.ReportPrefix + "*.txt"))
            {
                if (SamePath(p, kept) || !pattern.IsMatch(Path.GetFileName(p))) continue;
                try { File.Delete(p); Log.Write("Deleted older report \"" + p + "\""); } catch { }
            }
        }

        // With all systems selected: one report in each system's own folder.
        void SaveReportPerSystem(string fileName)
        {
            var systems = results.Where(s => s.Error == null && (Directory.Exists(s.Folder) || s.IsPack)).ToList();
            if (systems.Count == 0) return;
            string msg = "Save a report in each of the " + Count(systems.Count, "system folder") + ", named:\n\n" + fileName +
                         "\n\nEach system's older reports are deleted, so only the newest is kept. Folders with no matching DAT are skipped.";
            if (ThemedBox.Show(this, msg, Text, MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;

            var failed = new List<string>();
            foreach (var s in systems)
            {
                try
                {
                    string path = s.IsPack ? Path.Combine(Path.GetDirectoryName(s.Folder), PackReportName(fileName, s)) : Path.Combine(s.Folder, fileName);
                    File.WriteAllText(path, Report.Build(new[] { s }, miaBox.Checked), System.Text.Encoding.UTF8);
                    RemoveOldReports(s, path);
                }
                catch (Exception ex) { failed.Add(s.FolderName + ": " + ex.Message); }
            }
            string summary = "Saved " + Count(systems.Count - failed.Count, "report") + ", one in each system folder.";
            statusLabel.Text = summary + (failed.Count > 0 ? " " + failed.Count + " couldn't be saved." : "");
            if (failed.Count == 0) ThemedBox.Show(this, summary, Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
            else using (var f = new TextForm(Text, summary + " These couldn't be saved:", string.Join(Environment.NewLine, failed))) f.ShowDialog(this);
        }
    }

    // ---------------------------------------------------------------- small dialogs

    class TextForm : Form
    {
        public TextForm(string title, string heading, string text)
        {
            Text = title;
            Font = new Font("Segoe UI", 9f);
            AutoScaleMode = AutoScaleMode.Dpi;
            Size = new Size(800, 450);
            StartPosition = FormStartPosition.CenterParent;
            var label = new Label { Text = heading, Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(8) };
            var box = new TextBox { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both, WordWrap = false, Dock = DockStyle.Fill, Text = text };
            var ok = new Button { Text = "OK", DialogResult = DialogResult.OK, Dock = DockStyle.Bottom, Height = 32 };
            Controls.Add(box);
            Controls.Add(label);
            Controls.Add(ok);
            box.BringToFront();
            AcceptButton = ok;
            Theme.Apply(this);
        }
    }

    class ChooseDatForm : Form
    {
        public string Choice;
        const string Automatic = "(Match automatically by folder name)";

        public ChooseDatForm(string folder, List<string> dats, string current)
        {
            Text = "Choose DAT";
            Font = new Font("Segoe UI", 9f);
            AutoScaleMode = AutoScaleMode.Dpi;
            Size = new Size(620, 560);
            StartPosition = FormStartPosition.CenterParent;
            MinimizeBox = MaximizeBox = false;

            var label = new Label { Text = "Which DAT is the folder \"" + folder + "\" checked against?", Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(8, 8, 8, 4) };
            var filter = new TextBox { Dock = DockStyle.Top };
            var list = new ListBox { Dock = DockStyle.Fill, IntegralHeight = false };
            var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Padding = new Padding(4) };
            var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, AutoSize = true };
            var ok = new Button { Text = "OK", AutoSize = true };
            buttons.Controls.Add(cancel);
            buttons.Controls.Add(ok);

            Action fill = () =>
            {
                list.BeginUpdate();
                list.Items.Clear();
                list.Items.Add(Automatic);
                foreach (var d in dats) if (d.IndexOf(filter.Text, StringComparison.OrdinalIgnoreCase) >= 0) list.Items.Add(d);
                list.EndUpdate();
                int i = current != null ? list.Items.IndexOf(current) : -1;
                list.SelectedIndex = i >= 0 ? i : (list.Items.Count > 1 && filter.Text.Length > 0 ? 1 : 0);
            };
            filter.TextChanged += (s, e) => fill();
            fill();
            EventHandler accept = (s, e) =>
            {
                if (list.SelectedItem == null) return;
                Choice = (string)list.SelectedItem == Automatic ? null : (string)list.SelectedItem;
                DialogResult = DialogResult.OK;
            };
            ok.Click += accept;
            list.DoubleClick += accept;

            Controls.Add(list);
            Controls.Add(filter);
            Controls.Add(label);
            Controls.Add(buttons);
            list.BringToFront();
            AcceptButton = ok;
            CancelButton = cancel;
            Theme.Apply(this);
            Shown += (s, e) => filter.Focus();
        }
    }

    class RegionForm : Form
    {
        public string Order, Ignored;
        public bool UseCollection;   // the system's own regions were removed: it uses the collection's again

        // system: set the regions for this system only (own: it already has its own).
        public RegionForm(string order, string ignored, string system = null, bool own = false)
        {
            Text = system == null ? "Regions" : "Region Overrides for " + system;
            Font = new Font("Segoe UI", 9f);
            AutoScaleMode = AutoScaleMode.Dpi;
            Size = new Size(560, 680);
            MinimumSize = new Size(460, 450);
            StartPosition = FormStartPosition.CenterParent;
            MinimizeBox = MaximizeBox = false;
            ShowIcon = false;
            ShowInTaskbar = false;

            var help = new Label
            {
                Dock = DockStyle.Top, AutoSize = true, MaximumSize = new Size(520, 0), Padding = new Padding(10, 10, 10, 8),
                Text = (system == null ? "" : "These regions are for " + system + " only. Other systems keep the collection's regions.\n\n") +
                       "Untick a region to leave out ROMs released only there. They're listed as unneeded and don't count as missing. " +
                       "A ROM also released in a ticked region, such as (USA, Brazil), is kept.\n\n" +
                       "With \"Use 1G1R\" on, the version from the highest region in this list is kept."
            };

            // Saved order first, then any other regions No-Intro uses.
            var items = Preference.ParseList(order);
            foreach (var r in Preference.AllRegions()) if (!items.Contains(r, StringComparer.OrdinalIgnoreCase)) items.Add(r);
            var list = new TickList(items, Settings.RegionSet(ignored)) { Dock = DockStyle.Fill };
            Tips.Set(list, "Tick a region to keep its ROMs, untick it to leave out ROMs released only there.\nThe order sets which version 1G1R keeps.");

            var side = new FlowLayoutPanel { Dock = DockStyle.Right, FlowDirection = FlowDirection.TopDown, AutoSize = true, Padding = new Padding(6, 0, 8, 0) };
            Func<string, EventHandler, Button> btn = (t, h) => { var b = new Button { Text = t, Width = 120, Height = 30 }; b.Click += h; side.Controls.Add(b); return b; };
            Tips.Set(btn("Move Up", (s, e) => list.Move(-1)), "Moves the selected region up. With 1G1R, regions higher up win.");
            Tips.Set(btn("Move Down", (s, e) => list.Move(1)), "Moves the selected region down.");
            Tips.Set(btn("Tick All", (s, e) => list.TickAll()), "Ticks every region, so no ROMs are left out because of their region.");
            Tips.Set(btn("Reset", (s, e) => list.Reset(Preference.AllRegions())), "Puts the regions back in the usual order, all ticked.");

            var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Padding = new Padding(8) };
            var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, AutoSize = true, MinimumSize = new Size(90, 30) };
            var ok = new Button { Text = "OK", AutoSize = true, MinimumSize = new Size(90, 30) };
            ok.Click += (s, e) =>
            {
                Order = string.Join(", ", list.Items.Cast<string>());
                Ignored = string.Join(", ", list.Items.Cast<string>().Where(r => list.Unticked.Contains(r)));
                DialogResult = DialogResult.OK;
            };
            buttons.Controls.Add(cancel);
            buttons.Controls.Add(ok);
            if (own)
            {
                var shared = new Button { Text = "Use the Collection's Regions", AutoSize = true, MinimumSize = new Size(90, 30) };
                shared.Click += (s, e) => { UseCollection = true; DialogResult = DialogResult.OK; };
                Tips.Set(shared, "Removes this system's own regions, so it uses the same regions as the rest of the collection.");
                buttons.Controls.Add(shared);
            }

            Controls.Add(list);
            Controls.Add(side);
            Controls.Add(help);
            Controls.Add(buttons);
            list.BringToFront();
            AcceptButton = ok;
            CancelButton = cancel;
            Theme.Apply(this);
        }
    }

    // A list with a tick box on each line, drawn to match the app's theme.
    class TickList : ListBox
    {
        public readonly HashSet<string> Unticked;
        int hover = -1;

        public TickList(IEnumerable<string> items, HashSet<string> unticked)
        {
            Unticked = new HashSet<string>(unticked, StringComparer.OrdinalIgnoreCase);
            DrawMode = DrawMode.OwnerDrawFixed;
            IntegralHeight = false;
            foreach (var i in items) Items.Add(i);
            HandleCreated += (s, e) => ItemHeight = (int)Math.Round(24 * DeviceDpi / 96f);
            MouseDown += (s, e) =>
            {
                int i = IndexFromPoint(e.Location);
                if (i >= 0 && e.X < BoxRect(GetItemRectangle(i)).Right + 6) Toggle(i);
            };
            MouseMove += (s, e) => { int i = IndexFromPoint(e.Location); if (i != hover) { hover = i; Invalidate(); } };
            MouseLeave += (s, e) => { hover = -1; Invalidate(); };
            DoubleClick += (s, e) => { if (SelectedIndex >= 0) Toggle(SelectedIndex); };
            KeyDown += (s, e) => { if (e.KeyCode == Keys.Space && SelectedIndex >= 0) { Toggle(SelectedIndex); e.Handled = true; } };
        }

        Rectangle BoxRect(Rectangle row)
        {
            int size = (int)Math.Round(14 * DeviceDpi / 96f);
            return new Rectangle(row.Left + 6, row.Top + (row.Height - size) / 2, size, size);
        }

        void Toggle(int i)
        {
            string r = (string)Items[i];
            if (!Unticked.Remove(r)) Unticked.Add(r);
            Invalidate(GetItemRectangle(i));
        }

        public void TickAll() { Unticked.Clear(); Invalidate(); }

        public void Reset(List<string> regions)
        {
            Items.Clear();
            Unticked.Clear();
            foreach (var r in regions) Items.Add(r);
        }

        public void Move(int by)
        {
            int i = SelectedIndex, j = i + by;
            if (i < 0 || j < 0 || j >= Items.Count) return;
            var item = Items[i];
            Items.RemoveAt(i);
            Items.Insert(j, item);
            SelectedIndex = j;
        }

        protected override void OnDrawItem(DrawItemEventArgs e)
        {
            if (e.Index < 0) return;
            var p = Theme.Current;
            bool selected = (e.State & DrawItemState.Selected) != 0;
            Color back = selected ? (p.Dark ? Color.FromArgb(0, 84, 153) : Color.FromArgb(204, 232, 255)) : BackColor;
            using (var b = new SolidBrush(back)) e.Graphics.FillRectangle(b, e.Bounds);
            string r = (string)Items[e.Index];
            bool ticked = !Unticked.Contains(r);
            var box = BoxRect(e.Bounds);
            Theme.DrawTick(e.Graphics, box, ticked, e.Index == hover, true, DeviceDpi / 96f);
            var text = new Rectangle(box.Right + 8, e.Bounds.Top, e.Bounds.Width - box.Right - 8, e.Bounds.Height);
            Color colour = ticked ? (selected && p.Dark ? Color.White : p.Text) : p.Gray;
            TextRenderer.DrawText(e.Graphics, ticked ? r : r + "  (left out)", Font, text, colour,
                TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);
        }
    }
}
