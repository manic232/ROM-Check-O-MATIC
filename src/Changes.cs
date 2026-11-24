// ROM Check O-MATIC - what changed since the last scan: ROMs added, and ROMs newly missing (lost, or new in a
// DAT). After each scan the app saves a compact record of every system (what you have and what's missing)
// beside the .exe, one file per collection, and the next scan is compared with it.

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;

namespace RomCheckOMatic
{
    class SystemSnapshot
    {
        public HashSet<string> Have = new HashSet<string>(StringComparer.Ordinal);
        public HashSet<string> Missing = new HashSet<string>(StringComparer.Ordinal);
    }

    class SystemChanges
    {
        public string System;
        public List<string> Added = new List<string>();
        public List<string> NowMissing = new List<string>();
    }

    static class ScanHistory
    {
        // "ROM-Check-O-MATIC - Redump.scan", beside the .exe (the app is portable, like its settings).
        public static string PathFor(string collection)
        {
            string name = collection;
            foreach (char c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
            return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ROM-Check-O-MATIC - " + name + ".scan");
        }

        // Systems are recorded by their DAT's name, so renaming a folder doesn't lose its history.
        static string KeyOf(SystemResult s) { return s.Dat == null ? null : s.Dat.Name; }

        public static SystemSnapshot Snapshot(SystemResult s)
        {
            var snap = new SystemSnapshot();
            foreach (var f in s.Files)
                if (f.Game != null && (f.Kind == FileKind.Ok || f.IsFixable || f.Kind == FileKind.CueOutdated))
                    snap.Have.Add(f.Game.Name);
            foreach (var g in s.Missing) snap.Missing.Add(g.Name);
            return snap;
        }

        public static Dictionary<string, SystemSnapshot> Load(string path)
        {
            var all = new Dictionary<string, SystemSnapshot>(StringComparer.Ordinal);
            try
            {
                if (!File.Exists(path)) return all;
                using (var fs = File.OpenRead(path))
                using (var gz = new GZipStream(fs, CompressionMode.Decompress))
                using (var reader = new StreamReader(gz, Encoding.UTF8))
                {
                    SystemSnapshot current = null;
                    string line;
                    while ((line = reader.ReadLine()) != null)
                    {
                        if (line.Length < 2) continue;
                        string value = line.Substring(2);
                        if (line[0] == 'S') all[value] = current = new SystemSnapshot();
                        else if (current != null && line[0] == 'H') current.Have.Add(value);
                        else if (current != null && line[0] == 'M') current.Missing.Add(value);
                    }
                }
            }
            catch { all.Clear(); }   // a damaged record is just started again
            return all;
        }

        public static void Save(string path, Dictionary<string, SystemSnapshot> all)
        {
            try
            {
                string temp = path + ".new";
                using (var fs = File.Create(temp))
                using (var gz = new GZipStream(fs, CompressionLevel.Fastest))
                using (var writer = new StreamWriter(gz, new UTF8Encoding(false)))
                    foreach (var kv in all.OrderBy(k => k.Key, StringComparer.Ordinal))
                    {
                        writer.Write("S\t"); writer.WriteLine(kv.Key);
                        foreach (var n in kv.Value.Have) { writer.Write("H\t"); writer.WriteLine(n); }
                        foreach (var n in kv.Value.Missing) { writer.Write("M\t"); writer.WriteLine(n); }
                    }
                if (File.Exists(path)) File.Delete(path);
                File.Move(temp, path);
            }
            catch { }   // the history is a convenience: never let it stop a scan
        }

        // Records the systems just scanned (others keep their earlier record).
        public static void Update(Dictionary<string, SystemSnapshot> all, IEnumerable<SystemResult> scanned)
        {
            foreach (var s in scanned)
            {
                string key = KeyOf(s);
                if (key != null && s.Error == null) all[key] = Snapshot(s);
            }
        }

        // What's changed in each system since its last record. Systems scanned for the first time aren't listed.
        public static List<SystemChanges> Compare(Dictionary<string, SystemSnapshot> before, IEnumerable<SystemResult> now)
        {
            var list = new List<SystemChanges>();
            foreach (var s in now)
            {
                string key = KeyOf(s);
                SystemSnapshot old;
                if (key == null || s.Error != null || !before.TryGetValue(key, out old)) continue;
                var snap = Snapshot(s);
                var c = new SystemChanges { System = s.FolderName };
                c.Added.AddRange(snap.Have.Where(n => !old.Have.Contains(n)).OrderBy(n => n, StringComparer.OrdinalIgnoreCase));
                c.NowMissing.AddRange(snap.Missing.Where(n => !old.Missing.Contains(n)).OrderBy(n => n, StringComparer.OrdinalIgnoreCase));
                if (c.Added.Count > 0 || c.NowMissing.Count > 0) list.Add(c);
            }
            return list;
        }

        public static string Summary(List<SystemChanges> changes)
        {
            int added = changes.Sum(c => c.Added.Count), missing = changes.Sum(c => c.NowMissing.Count);
            if (added == 0 && missing == 0) return "No changes since the last scan";
            var parts = new List<string>();
            if (added > 0) parts.Add(added.ToString("N0") + " added");
            if (missing > 0) parts.Add(missing.ToString("N0") + " newly missing");
            return "Since the last scan: " + string.Join(", ", parts);
        }

        public static string Details(List<SystemChanges> changes)
        {
            var sb = new StringBuilder();
            foreach (var c in changes)
            {
                sb.AppendLine(c.System);
                sb.AppendLine();
                if (c.Added.Count > 0)
                {
                    sb.AppendLine("  Added (" + c.Added.Count + ")");
                    foreach (var n in c.Added) sb.AppendLine("    " + n);
                    sb.AppendLine();
                }
                if (c.NowMissing.Count > 0)
                {
                    sb.AppendLine("  Newly missing (" + c.NowMissing.Count + ") - removed, renamed, or new in the DAT");
                    foreach (var n in c.NowMissing) sb.AppendLine("    " + n);
                    sb.AppendLine();
                }
            }
            return sb.ToString();
        }
    }
}
