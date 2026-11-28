// ROM Check O-MATIC - the last scan's results, saved so they can be shown when the app opens without scanning
// again. One file per collection beside the .exe ("ROM-Check-O-MATIC - Redump.results"), compressed.
//
// The results are only shown if they still fit: the same folders and DAT files, and for each system the same
// DAT, regions, ROM types and ignore list. Anything that changes files scans again first, as files may have
// changed since.

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace RomCheckOMatic
{
    class SavedScan
    {
        public string Key;          // the folders and DAT files they were scanned with
        public DateTime ScannedAt;  // when the full scan was made
        public List<SystemResult> Systems = new List<SystemResult>();
    }

    static class SavedResults
    {
        const string Magic = "ROM Check O-MATIC results";
        const int Format = 1;

        public static string PathFor(string collection)
        {
            string name = collection;
            foreach (char c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
            return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ROM-Check-O-MATIC - " + name + ".results");
        }

        // Every collection's saved results (for turning the feature off).
        public static void DeleteAll()
        {
            try
            {
                foreach (var f in Directory.GetFiles(AppDomain.CurrentDomain.BaseDirectory, "ROM-Check-O-MATIC - *.results")) File.Delete(f);
            }
            catch { }
        }

        public static void Delete(string collection)
        {
            try { File.Delete(PathFor(collection)); } catch { }
        }

        // The folders and every DAT's name and version. If any of them change, the saved results aren't shown.
        public static string Key(string romFolder, string datFolder, string cueFolder, DatIndex index)
        {
            var sb = new StringBuilder();
            sb.Append(Format).Append('\n');
            foreach (var f in new[] { romFolder, datFolder, cueFolder }) sb.Append((f ?? "").Trim().TrimEnd('\\').ToLowerInvariant()).Append('\n');
            if (index != null)
                foreach (var d in index.ByName.Values.OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase))
                    sb.Append(d.Name).Append('\t').Append(d.Version).Append('\n');
            return Hash(sb.ToString());
        }

        // What a system was scanned with: its regions, ROM types and ignore list (its own if it has them).
        public static string SystemKey(SystemResult s, ScanOptions options)
        {
            if (s.Dat == null) return "";
            SystemOverride own;
            if (options.Systems != null && options.Systems.TryGetValue(s.Dat.Name, out own)) options = options.With(own);
            HashSet<string> ignore = null;
            if (options.Ignore != null) options.Ignore.TryGetValue(s.Dat.Name, out ignore);
            var sb = new StringBuilder();
            sb.Append(options.OneGamePerTitle).Append('\n')
              .Append(options.RegionPriority).Append('\n')
              .Append(string.Join(",", options.IgnoredRegions.Select(r => r.ToLowerInvariant()).OrderBy(r => r, StringComparer.Ordinal))).Append('\n')
              .Append((int)options.Exclude).Append('\n')
              .Append(options.KeepPacks).Append('\n')
              .Append(ignore == null ? "" : string.Join("\t", ignore.Select(n => n.ToLowerInvariant()).OrderBy(n => n, StringComparer.Ordinal)));
            return Hash(sb.ToString());
        }

        static string Hash(string text)
        {
            using (var sha = SHA1.Create())
                return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(text))).Replace("-", "");
        }

        // ------------------------------------------------------------ saving

        public static void Save(string path, SavedScan scan)
        {
            string temp = path + ".new";
            try
            {
                using (var fs = File.Create(temp))
                using (var gz = new GZipStream(fs, CompressionLevel.Fastest))
                using (var w = new BinaryWriter(gz, Encoding.UTF8))
                {
                    w.Write(Magic);
                    w.Write(Format);
                    w.Write(scan.Key);
                    w.Write(scan.ScannedAt.ToBinary());
                    w.Write(scan.Systems.Count);
                    foreach (var s in scan.Systems) WriteSystem(w, s);
                }
                if (File.Exists(path)) File.Delete(path);
                File.Move(temp, path);
            }
            catch
            {
                // Saved results are a convenience: never let them get in the way.
                try { File.Delete(temp); } catch { }
            }
        }

        static void Str(BinaryWriter w, string s)
        {
            w.Write(s != null);
            if (s != null) w.Write(s);
        }

        static void Dat(BinaryWriter w, DatInfo d)
        {
            w.Write(d != null);
            if (d == null) return;
            Str(w, d.Name); Str(w, d.Version); Str(w, d.Author); Str(w, d.Path);
        }

        static void Rom(BinaryWriter w, DatRom r)
        {
            Str(w, r.Name); w.Write(r.Size); w.Write(r.Crc); w.Write(r.BadDump); w.Write(r.Mia); Str(w, r.Sha1);
        }

        static void Items(BinaryWriter w, List<ZipItem> items)
        {
            w.Write(items == null ? -1 : items.Count);
            if (items == null) return;
            foreach (var i in items)
            {
                Str(w, i.Name); w.Write(i.Size); w.Write(i.Crc); w.Write(i.Index); Str(w, i.Sha1); Str(w, i.RawSha1); Str(w, i.FullName);
                w.Write(i.Method); w.Write(i.DosTime); w.Write(i.DosDate); w.Write(i.Flags); w.Write(i.CompressedSize); w.Write(i.LocalOffset);
            }
        }

        static void WriteSystem(BinaryWriter w, SystemResult s)
        {
            Str(w, s.Folder); Str(w, s.FolderName);
            Dat(w, s.Dat);
            w.Write(s.ExtraDats.Count);
            foreach (var d in s.ExtraDats) Dat(w, d);
            Str(w, s.Error);
            w.Write(s.IsPack); w.Write(s.KeepPack); w.Write(s.OneGame); w.Write((int)s.Excluded);
            w.Write(s.IgnoredRegions.Count);
            foreach (var r in s.IgnoredRegions) Str(w, r);
            w.Write(s.OwnRegions); w.Write(s.OwnTypes); w.Write(s.Total); w.Write(s.Have);
            Str(w, s.Key);

            // Each game once, then the missing games and files point at it by number.
            var games = new List<DatGame>();
            var number = new Dictionary<DatGame, int>();
            Func<DatGame, int> numberOf = g =>
            {
                if (g == null) return -1;
                int n;
                if (!number.TryGetValue(g, out n)) { n = games.Count; games.Add(g); number[g] = n; }
                return n;
            };
            var missing = s.Missing.Select(numberOf).ToList();
            var fileGames = s.Files.Select(f => numberOf(f.Game)).ToList();

            w.Write(games.Count);
            foreach (var g in games)
            {
                Str(w, g.Name); Str(w, g.Id); Str(w, g.CloneOf); w.Write((int)g.Type);
                w.Write(g.Parent == g);
                w.Write(g.Categories.Count);
                foreach (var c in g.Categories) Str(w, c);
                w.Write(g.Roms.Count);
                foreach (var r in g.Roms) Rom(w, r);
                w.Write(g.Disks.Count);
                foreach (var r in g.Disks) Rom(w, r);
            }

            w.Write(missing.Count);
            for (int i = 0; i < missing.Count; i++)
            {
                w.Write(missing[i]);
                string note;
                s.MissingNote.TryGetValue(s.Missing[i], out note);
                Str(w, note);
            }

            w.Write(s.Files.Count);
            for (int i = 0; i < s.Files.Count; i++)
            {
                var f = s.Files[i];
                Str(w, f.Path); Str(w, f.Name); w.Write(f.Length);
                w.Write(f.IsZip); w.Write(f.Is7z); w.Write(f.IsFolder); w.Write(f.LooseChd); w.Write(f.InPack); Str(w, f.PackGame);
                w.Write(f.CueOnly); w.Write(f.FixCue); w.Write(f.OnIgnoreList);
                w.Write((int)f.Kind);
                w.Write(fileGames[i]);
                Str(w, f.Detail);
                Items(w, f.Items);
                w.Write(f.NeedsRebuild);
                Str(w, f.TargetPath);
                Items(w, f.ExtraItems);
            }
        }

        // ------------------------------------------------------------ loading

        // Null if there are none, or they can't be read (an older or damaged file is just ignored).
        public static SavedScan Load(string path)
        {
            try
            {
                if (!File.Exists(path)) return null;
                using (var fs = File.OpenRead(path))
                using (var gz = new GZipStream(fs, CompressionMode.Decompress))
                using (var r = new BinaryReader(gz, Encoding.UTF8))
                {
                    if (r.ReadString() != Magic || r.ReadInt32() != Format) return null;
                    var scan = new SavedScan { Key = r.ReadString(), ScannedAt = DateTime.FromBinary(r.ReadInt64()) };
                    int n = r.ReadInt32();
                    for (int i = 0; i < n; i++) scan.Systems.Add(ReadSystem(r));
                    return scan;
                }
            }
            catch { return null; }
        }

        static string Str(BinaryReader r)
        {
            return r.ReadBoolean() ? r.ReadString() : null;
        }

        static DatInfo Dat(BinaryReader r)
        {
            if (!r.ReadBoolean()) return null;
            return new DatInfo { Name = Str(r), Version = Str(r), Author = Str(r) ?? "", Path = Str(r) };
        }

        static DatRom Rom(BinaryReader r, DatGame g)
        {
            return new DatRom { Name = Str(r), Size = r.ReadInt64(), Crc = r.ReadUInt32(), BadDump = r.ReadBoolean(), Mia = r.ReadBoolean(), Sha1 = Str(r), Game = g };
        }

        static List<ZipItem> Items(BinaryReader r)
        {
            int n = r.ReadInt32();
            if (n < 0) return null;
            var list = new List<ZipItem>(n);
            for (int i = 0; i < n; i++)
                list.Add(new ZipItem
                {
                    Name = Str(r), Size = r.ReadInt64(), Crc = r.ReadUInt32(), Index = r.ReadInt32(), Sha1 = Str(r), RawSha1 = Str(r), FullName = Str(r),
                    Method = r.ReadInt32(), DosTime = r.ReadInt32(), DosDate = r.ReadInt32(), Flags = r.ReadInt32(), CompressedSize = r.ReadInt64(), LocalOffset = r.ReadInt64()
                });
            return list;
        }

        static SystemResult ReadSystem(BinaryReader r)
        {
            var s = new SystemResult { Folder = Str(r), FolderName = Str(r), Dat = Dat(r) };
            int n = r.ReadInt32();
            for (int i = 0; i < n; i++) s.ExtraDats.Add(Dat(r));
            s.Error = Str(r);
            s.IsPack = r.ReadBoolean(); s.KeepPack = r.ReadBoolean(); s.OneGame = r.ReadBoolean(); s.Excluded = (GameType)r.ReadInt32();
            n = r.ReadInt32();
            for (int i = 0; i < n; i++) s.IgnoredRegions.Add(Str(r));
            s.OwnRegions = r.ReadBoolean(); s.OwnTypes = r.ReadBoolean(); s.Total = r.ReadInt32(); s.Have = r.ReadInt32();
            s.Key = Str(r);

            n = r.ReadInt32();
            var games = new List<DatGame>(n);
            for (int i = 0; i < n; i++)
            {
                var g = new DatGame { Name = Str(r), Id = Str(r), CloneOf = Str(r), Type = (GameType)r.ReadInt32() };
                if (r.ReadBoolean()) g.Parent = g;
                int c = r.ReadInt32();
                for (int j = 0; j < c; j++) g.Categories.Add(Str(r));
                c = r.ReadInt32();
                for (int j = 0; j < c; j++) g.Roms.Add(Rom(r, g));
                c = r.ReadInt32();
                for (int j = 0; j < c; j++) g.Disks.Add(Rom(r, g));
                games.Add(g);
            }

            n = r.ReadInt32();
            for (int i = 0; i < n; i++)
            {
                var g = games[r.ReadInt32()];
                s.Missing.Add(g);
                string note = Str(r);
                if (note != null) s.MissingNote[g] = note;
            }

            n = r.ReadInt32();
            for (int i = 0; i < n; i++)
            {
                var f = new FileResult { System = s, Path = Str(r), Name = Str(r), Length = r.ReadInt64() };
                f.IsZip = r.ReadBoolean(); f.Is7z = r.ReadBoolean(); f.IsFolder = r.ReadBoolean(); f.LooseChd = r.ReadBoolean(); f.InPack = r.ReadBoolean(); f.PackGame = Str(r);
                f.CueOnly = r.ReadBoolean(); f.FixCue = r.ReadBoolean(); f.OnIgnoreList = r.ReadBoolean();
                f.Kind = (FileKind)r.ReadInt32();
                int g = r.ReadInt32();
                f.Game = g >= 0 ? games[g] : null;
                f.Detail = Str(r) ?? "";
                f.Items = Items(r);
                f.NeedsRebuild = r.ReadBoolean();
                f.TargetPath = Str(r);
                f.ExtraItems = Items(r);
                s.Files.Add(f);
            }
            return s;
        }
    }
}
