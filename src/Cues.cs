// ROM Check O-MATIC - finds the exact cue sheets the DATs expect, in Redump's cue sheet packs.
//
// Redump publishes a zip of every system's cue sheets ("Sony - PlayStation - Cuesheets (10914) (...).zip").
// When a disc is renamed its cue sheet changes (it lists the track file names), so the right one is
// taken from these packs, matched by the size and CRC the DAT gives.

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;

namespace RomCheckOMatic
{
    class CueSource
    {
        public string Path;      // the pack zip, or a loose cue sheet
        public int Index = -1;   // the entry in the zip (-1 for a loose file)
        public string Name;
    }

    class CueIndex
    {
        readonly Dictionary<string, CueSource> byKey = new Dictionary<string, CueSource>();
        public int Count { get { return byKey.Count; } }
        public List<string> Packs = new List<string>();
        public int Loose;   // cue sheets found as separate files

        public string Summary
        {
            get
            {
                if (Count == 0) return "no cue sheets found";
                return Count.ToString("N0") + " cue sheets" + (Packs.Count > 0 ? " from " + Packs.Count + " pack" + (Packs.Count == 1 ? "" : "s") : "") +
                       (Loose > 0 ? (Packs.Count > 0 ? " and " : " from ") + Loose.ToString("N0") + " file" + (Loose == 1 ? "" : "s") : "");
            }
        }

        public bool Has(string key) { return byKey.ContainsKey(key); }
        public CueSource Get(string key) { CueSource s; return byKey.TryGetValue(key, out s) ? s : null; }

        static bool IsPackName(string file)
        {
            string n = System.IO.Path.GetFileName(file);
            return n.IndexOf("cuesheet", StringComparison.OrdinalIgnoreCase) >= 0 || n.IndexOf("cue sheet", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   n.IndexOf("GDI Files", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        static bool IsSheet(string name)
        {
            return name.EndsWith(".cue", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".gdi", StringComparison.OrdinalIgnoreCase);
        }

        // Looks in the chosen cue sheets folder (every zip and cue sheet in it, subfolders too), then in the
        // ROM folder's "!" folders (such as "!Cuesheets") for packs and loose cue sheets, and anywhere in the
        // DAT folder for packs.
        public static CueIndex Build(string romRoot, string datRoot, string cueFolder = null)
        {
            var index = new CueIndex();
            var zips = new List<string>();
            var loose = new List<string>();
            try
            {
                if (!string.IsNullOrEmpty(cueFolder) && Directory.Exists(cueFolder))
                    foreach (var f in Files.Enumerate(cueFolder, "*"))
                    {
                        if (f.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) zips.Add(f);
                        else if (IsSheet(f)) loose.Add(f);
                    }
            }
            catch { }
            try
            {
                if (Directory.Exists(romRoot))
                    foreach (var d in Directory.GetDirectories(romRoot).Where(d => System.IO.Path.GetFileName(d).StartsWith("!")))
                        foreach (var f in Files.Enumerate(d, "*"))
                        {
                            if (f.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) zips.Add(f);
                            else if (IsSheet(f)) loose.Add(f);
                        }
            }
            catch { }
            try
            {
                if (Directory.Exists(datRoot))
                    zips.AddRange(Files.Enumerate(datRoot, "*.zip").Where(IsPackName));
            }
            catch { }

            foreach (var z in zips.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                try
                {
                    bool any = false;
                    foreach (var item in ZipReader.Read(z).Where(i => IsSheet(i.Name)))
                    {
                        if (!index.byKey.ContainsKey(item.Key)) index.byKey[item.Key] = new CueSource { Path = z, Index = item.Index, Name = item.Name };
                        any = true;
                    }
                    if (any) index.Packs.Add(z);
                }
                catch { }
            }
            foreach (var f in loose.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                try
                {
                    long len = new FileInfo(f).Length;
                    string key = RomKey.Of(len, Crc32.OfFile(f, System.Threading.CancellationToken.None));
                    if (!index.byKey.ContainsKey(key)) index.byKey[key] = new CueSource { Path = f, Name = System.IO.Path.GetFileName(f) };
                    index.Loose++;
                }
                catch { }
            }
            return index;
        }

        // The cue sheet's contents (they're small, so read into memory).
        public Stream Open(string key)
        {
            var s = Get(key);
            if (s == null) throw new FileNotFoundException("That cue sheet isn't in your cue sheet packs");
            if (s.Index < 0) return new MemoryStream(File.ReadAllBytes(s.Path));
            var ms = new MemoryStream();
            using (var zip = ZipFile.OpenRead(s.Path))
            using (var input = zip.Entries[s.Index].Open())
                input.CopyTo(ms);
            ms.Position = 0;
            return ms;
        }

        // Writes the cue sheet to a file with the name the DAT gives it.
        public void Extract(string key, string toPath)
        {
            var s = Get(key);
            if (s == null) throw new FileNotFoundException("That cue sheet isn't in your cue sheet packs");
            if (s.Index < 0) { File.Copy(s.Path, toPath, true); return; }
            using (var zip = ZipFile.OpenRead(s.Path))
            using (var input = zip.Entries[s.Index].Open())
            using (var output = File.Create(toPath))
                input.CopyTo(output);
        }
    }
}
