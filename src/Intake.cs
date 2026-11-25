// ROM Check O-MATIC - add missing ROMs from a folder of downloads.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading;

namespace RomCheckOMatic
{
    // One file found in the downloads folder, and what will happen to it.
    class IntakeFile
    {
        public string Path;
        public string Name;              // relative to the downloads folder
        public string Kind;              // "zip", "7z" (anything 7-Zip opens) or "file"
        public List<ZipItem> Items;
        public List<IntakeMatch> Matches = new List<IntakeMatch>();   // the ROMs this file is (part of)
        public string Outcome = "";      // shown in the list
        public bool RemoveAfter;         // remove the download once its ROMs are added
    }

    // One missing ROM that can be added. Its files may come from one download or from several
    // (a game on four tapes downloaded as four loose files, for example).
    class IntakeMatch
    {
        public SystemResult System;
        public DatGame Game;
        public string Target;
        public string Error;
        public Dictionary<string, Tuple<IntakeFile, ZipItem>> Parts = new Dictionary<string, Tuple<IntakeFile, ZipItem>>();
        public List<IntakeFile> Sources { get { return Parts.Values.Select(p => p.Item1).Distinct().ToList(); } }
    }

    static class Intake
    {
        public static bool IsPartial(string path)
        {
            return Partial.Contains(System.IO.Path.GetExtension(path));
        }

        static readonly HashSet<string> Partial = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { ".crdownload", ".part", ".partial", ".tmp", ".download", ".opdownload", ".!ut", ".!qb", ".romcheck-tmp" };
        static readonly HashSet<string> Junk = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { ".txt", ".nfo", ".diz", ".url", ".jpg", ".jpeg", ".png", ".gif", ".htm", ".html", ".pdf", ".sfv", ".md5", ".sha1", ".xml", ".dat", ".ini" };
        static readonly HashSet<string> SevenZipTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { ".7z", ".rar", ".gz", ".tar", ".tgz", ".bz2", ".xz", ".zst" };
        static readonly HashSet<string> Ignored = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "desktop.ini", "thumbs.db", ".ds_store" };

        // CRCs of loose files already worked out, so watching the folder doesn't read the same file again and again.
        static readonly Dictionary<string, Tuple<long, DateTime, uint>> crcCache = new Dictionary<string, Tuple<long, DateTime, uint>>(StringComparer.OrdinalIgnoreCase);

        static uint CachedCrc(string path, long length, CancellationToken ct)
        {
            var when = File.GetLastWriteTimeUtc(path);
            Tuple<long, DateTime, uint> c;
            lock (crcCache)
                if (crcCache.TryGetValue(path, out c) && c.Item1 == length && c.Item2 == when) return c.Item3;
            uint crc = Crc32.OfFile(path, ct);
            lock (crcCache) crcCache[path] = Tuple.Create(length, when, crc);
            return crc;
        }

        // Works out what each file in the folder is, without changing anything.
        public static List<IntakeFile> Plan(string folder, List<SystemResult> results, DatIndex index, Action<int, int> progress, CancellationToken ct)
        {
            // Every ROM that's missing, by the size and CRC of its files.
            var missing = new Dictionary<string, List<Tuple<SystemResult, DatGame>>>();
            var have = new HashSet<string>();
            var inDat = new HashSet<string>();
            var sizes = new HashSet<long>();   // only loose files of these sizes can possibly be a ROM we know
            // TOSEC packs aren't added to (unpack them first, and missing games can be added to the folder).
            foreach (var s in results.Where(r => r.Error == null && r.Dat != null && !r.IsPack))
            {
                foreach (var g in s.Missing)
                    foreach (var r in g.Roms)
                    {
                        List<Tuple<SystemResult, DatGame>> list;
                        if (!missing.TryGetValue(r.Key, out list)) missing[r.Key] = list = new List<Tuple<SystemResult, DatGame>>();
                        list.Add(Tuple.Create(s, g));
                        sizes.Add(r.Size);
                    }
                foreach (var f in s.Files.Where(f => f.HoldsGame))
                    foreach (var r in f.Game.Roms) { have.Add(r.Key); sizes.Add(r.Size); }
                try { foreach (var k in index.Load(s.Dat).ByKey.Keys) inDat.Add(k); } catch { }
            }

            // 1. Read every file: names, sizes and CRCs.
            // CHDs (MAME disks) aren't zipped, so they're left for you to move into place.
            var paths = Files.Enumerate(folder, "*").Where(p => !Ignored.Contains(System.IO.Path.GetFileName(p)) && File.Exists(p) && !Chd.IsChd(p)).ToList();
            var plan = new List<IntakeFile>();
            int done = 0;
            foreach (var path in paths)
            {
                ct.ThrowIfCancellationRequested();
                if (progress != null) progress(done++, paths.Count);
                var f = new IntakeFile { Path = path, Name = FileResult.RelativeTo(folder, path), Items = new List<ZipItem>() };
                plan.Add(f);
                string ext = System.IO.Path.GetExtension(path);
                try
                {
                    if (Partial.Contains(ext)) { f.Outcome = "Still downloading, or an unfinished download"; continue; }
                    if (Junk.Contains(ext)) { f.Outcome = "Not a ROM file"; continue; }
                    if (ext.Equals(".zip", StringComparison.OrdinalIgnoreCase)) { f.Kind = "zip"; f.Items = ZipReader.Read(path); }
                    else if (SevenZipTypes.Contains(ext))
                    {
                        if (SevenZip.Exe == null) { f.Outcome = "Needs 7-Zip installed to open " + ext + " files"; continue; }
                        f.Kind = "7z";
                        f.Items = SevenZip.List(path);
                    }
                    else
                    {
                        f.Kind = "file";
                        long len = new FileInfo(path).Length;
                        // Checking the size first means big unrelated downloads (videos, installers) aren't read.
                        if (!sizes.Contains(len)) { f.Outcome = "Not a ROM from your DATs (or a bad dump)"; continue; }
                        f.Items = new List<ZipItem> { new ZipItem { Name = System.IO.Path.GetFileName(path), Size = len, Crc = CachedCrc(path, len, ct) } };
                    }
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { f.Outcome = "Couldn't read it: " + ex.Message; f.Items = new List<ZipItem>(); continue; }
            }

            // 2. Pool every file's contents, so a ROM made of several files can be put together
            //    from separate downloads.
            var pool = new Dictionary<string, List<Tuple<IntakeFile, ZipItem>>>();
            foreach (var f in plan)
                foreach (var i in f.Items)
                {
                    List<Tuple<IntakeFile, ZipItem>> list;
                    if (!pool.TryGetValue(i.Key, out list)) pool[i.Key] = list = new List<Tuple<IntakeFile, ZipItem>>();
                    list.Add(Tuple.Create(f, i));
                }

            // 3. Every missing ROM that has all its files somewhere in the folder.
            var candidates = new List<Tuple<SystemResult, DatGame>>();
            foreach (var k in pool.Keys)
            {
                List<Tuple<SystemResult, DatGame>> list;
                if (missing.TryGetValue(k, out list)) foreach (var t in list) if (!candidates.Contains(t)) candidates.Add(t);
            }
            var taken = new HashSet<DatGame>();
            var partOf = new Dictionary<IntakeFile, string>();
            foreach (var t in candidates)
            {
                var game = t.Item2;
                var present = game.Roms.Where(r => pool.ContainsKey(r.Key)).ToList();
                if (present.Count < game.Roms.Count)
                {
                    // Some of its files are here but not all: say so, and wait for the rest.
                    string note = "Part of \"" + game.Name + "\": " + present.Count + " of " + game.Roms.Count + " files are here";
                    foreach (var r in present) foreach (var p in pool[r.Key]) if (!partOf.ContainsKey(p.Item1)) partOf[p.Item1] = note;
                    continue;
                }
                if (taken.Contains(game)) continue;
                var m = new IntakeMatch { System = t.Item1, Game = game, Target = System.IO.Path.Combine(t.Item1.Folder, game.Name + ".zip") };
                // Prefer one download that holds the whole ROM; otherwise take each file from wherever it is.
                var whole = plan.FirstOrDefault(f => f.Items.Count > 0 && game.Roms.All(r => f.Items.Any(i => i.Key == r.Key)));
                foreach (var r in game.Roms)
                    m.Parts[r.Key] = whole != null ? Tuple.Create(whole, whole.Items.First(i => i.Key == r.Key)) : pool[r.Key][0];
                if (File.Exists(m.Target)) m.Error = "\"" + System.IO.Path.GetFileName(m.Target) + "\" is already in the system folder, but isn't right. Delete unneeded files first";
                else taken.Add(game);
                foreach (var f in m.Sources) f.Matches.Add(m);
            }

            // 4. What happens to each file.
            foreach (var f in plan)
            {
                if (f.Outcome.Length > 0) continue;
                var keys = new HashSet<string>(f.Items.Select(i => i.Key));
                var good = f.Matches.Where(m => m.Error == null).ToList();
                if (good.Count > 0)
                {
                    var used = new HashSet<string>(good.SelectMany(m => m.Parts.Where(p => p.Value.Item1 == f).Select(p => p.Key)));
                    // Remove the download afterwards only if nothing else of value is in it.
                    f.RemoveAfter = f.Items.All(i => used.Contains(i.Key) || Junk.Contains(System.IO.Path.GetExtension(i.Name)) || have.Contains(i.Key));
                    var first = good[0];
                    int others = first.Sources.Count - 1;
                    f.Outcome = good.Count == 1 ? "Add to " + first.System.FolderName : "Add " + good.Count + " ROMs";
                    if (others > 0) f.Outcome += " (together with " + others + " other file" + (others == 1 ? "" : "s") + ")";
                    if (!f.RemoveAfter) f.Outcome += " (the download is kept, as it holds other files too)";
                }
                else if (f.Matches.Count > 0) f.Outcome = f.Matches[0].Error;
                else if (partOf.ContainsKey(f)) f.Outcome = partOf[f];
                else if (f.Items.Count > 0 && f.Items.Any(i => have.Contains(i.Key)) && keys.All(k => have.Contains(k) || !inDat.Contains(k)))
                    f.Outcome = "You already have this";
                else if (f.Items.Any(i => missing.ContainsKey(i.Key))) f.Outcome = "Another download in the folder has this ROM too";
                else if (f.Items.Any(i => inDat.Contains(i.Key))) f.Outcome = "In the DAT, but not needed (another version is kept, or your ROM Types leave it out)";
                else f.Outcome = "Not a ROM from your DATs (or a bad dump)";
            }
            if (progress != null) progress(paths.Count, paths.Count);
            return plan;
        }

        // The ROMs a plan will add (each once, even when several downloads go into it).
        public static List<IntakeMatch> Adds(List<IntakeFile> plan)
        {
            return plan.SelectMany(f => f.Matches).Where(m => m.Error == null).Distinct().ToList();
        }

        // Adds the planned ROMs to their system folders. Returns one line per ROM or problem.
        public static List<string> Apply(List<IntakeFile> plan, bool removeDownloads, Action<int, int> progress, CancellationToken ct)
        {
            var lines = new List<string>();
            var adds = Adds(plan);
            var extracted = new Dictionary<IntakeFile, string>();
            var failed = new HashSet<IntakeFile>();
            int done = 0;
            try
            {
                foreach (var m in adds)
                {
                    ct.ThrowIfCancellationRequested();
                    string err = null;
                    try
                    {
                        foreach (var f in m.Sources.Where(f => f.Kind == "7z" && !extracted.ContainsKey(f)))
                        {
                            string dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ROM-Check-O-MATIC", Guid.NewGuid().ToString("N"));
                            extracted[f] = dir;
                            SevenZip.Extract(f.Path, dir);
                        }
                        err = Add(m, extracted);
                    }
                    catch (Exception ex) { err = ex.Message; }
                    if (err == null) lines.Add("Added " + m.Game.Name + " to " + m.System.FolderName);
                    else { lines.Add("COULDN'T ADD " + m.Game.Name + ": " + err); foreach (var f in m.Sources) failed.Add(f); }
                    if (progress != null) progress(++done, adds.Count);
                }
            }
            finally
            {
                foreach (var dir in extracted.Values) try { Directory.Delete(dir, true); } catch { }
            }

            if (removeDownloads)
                foreach (var f in adds.SelectMany(m => m.Sources).Distinct())
                {
                    if (failed.Contains(f) || !f.RemoveAfter || !File.Exists(f.Path)) continue;
                    try { File.Delete(f.Path); Log.Write("Removed download \"" + f.Path + "\" after adding its ROMs"); }
                    catch (Exception ex) { lines.Add("Couldn't remove the download " + f.Name + ": " + ex.Message); }
                }
            return lines;
        }

        static string Add(IntakeMatch m, Dictionary<IntakeFile, string> extracted)
        {
            if (File.Exists(m.Target)) return "\"" + System.IO.Path.GetFileName(m.Target) + "\" is already there";
            string tmp = m.Target + ".romcheck-tmp";
            var zips = new Dictionary<IntakeFile, ZipArchive>();
            try
            {
                if (File.Exists(tmp)) File.Delete(tmp);
                using (var fs = new FileStream(tmp, FileMode.CreateNew, FileAccess.ReadWrite))
                using (var dst = new ZipArchive(fs, ZipArchiveMode.Create))
                {
                    foreach (var rom in m.Game.Roms)
                    {
                        var part = m.Parts[rom.Key];
                        var f = part.Item1;
                        var item = part.Item2;
                        Stream input;
                        DateTimeOffset time;
                        if (f.Kind == "zip")
                        {
                            ZipArchive src;
                            if (!zips.TryGetValue(f, out src)) zips[f] = src = ZipFile.OpenRead(f.Path);
                            var e = src.Entries[item.Index];
                            input = e.Open(); time = e.LastWriteTime;
                        }
                        else if (f.Kind == "7z")
                        {
                            string p = System.IO.Path.Combine(extracted[f], item.Name.Replace('/', '\\'));
                            input = File.OpenRead(p); time = File.GetLastWriteTime(p);
                        }
                        else { input = File.OpenRead(f.Path); time = File.GetLastWriteTime(f.Path); }
                        using (input)
                        {
                            var entry = dst.CreateEntry(rom.Name, CompressionLevel.Optimal);
                            try { entry.LastWriteTime = time; } catch { }
                            using (var output = entry.Open()) input.CopyTo(output, 1 << 20);
                        }
                    }
                }
                foreach (var z in zips.Values) z.Dispose();
                zips.Clear();
                // Check the new zip against the DAT before it goes into the set.
                if (!Scanner.ItemsExactly(ZipReader.Read(tmp), m.Game))
                    throw new InvalidDataException("The new zip didn't match the DAT, so it wasn't added (the download may be damaged)");
                File.Move(tmp, m.Target);
                Log.Write("Added \"" + m.Game.Name + "\" to \"" + m.System.Folder + "\" from " +
                          string.Join(", ", m.Sources.Select(s => "\"" + s.Path + "\"")));
                return null;
            }
            catch (Exception ex)
            {
                foreach (var z in zips.Values) try { z.Dispose(); } catch { }
                try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
                return ex.Message;
            }
        }
    }

    // Uses the installed 7-Zip to read .7z, .rar and other archives.
    static class SevenZip
    {
        static string exe;
        static bool looked;

        public static string Exe
        {
            get
            {
                if (looked) return exe;
                looked = true;
                var places = new List<string> {
                    System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "7-Zip", "7z.exe"),
                    @"C:\Program Files\7-Zip\7z.exe", @"C:\Program Files (x86)\7-Zip\7z.exe" };
                foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';'))
                    if (dir.Trim().Length > 0) places.Add(System.IO.Path.Combine(dir.Trim(), "7z.exe"));
                exe = places.FirstOrDefault(p => { try { return File.Exists(p); } catch { return false; } });
                return exe;
            }
        }

        static string Run(string args, int timeoutMs, string workingFolder = null)
        {
            var psi = new ProcessStartInfo(Exe, args)
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
            };
            if (workingFolder != null) psi.WorkingDirectory = workingFolder;
            using (var p = Process.Start(psi))
            {
                var errTask = p.StandardError.ReadToEndAsync();
                string output = p.StandardOutput.ReadToEnd();
                if (!p.WaitForExit(timeoutMs)) { try { p.Kill(); } catch { } throw new TimeoutException("7-Zip took too long"); }
                if (p.ExitCode > 1) throw new IOException("7-Zip couldn't open it: " + errTask.Result.Trim());
                return output;
            }
        }

        static string Quote(string s) { return "\"" + s + "\""; }

        // The files in the archive, with sizes and CRCs, read from 7-Zip's technical listing.
        public static List<ZipItem> List(string archive)
        {
            string output = Run("l -slt -sccUTF-8 -- " + Quote(archive), 120000);
            var items = new List<ZipItem>();
            int start = output.IndexOf("----------", StringComparison.Ordinal);
            if (start < 0) return items;
            string name = null; long size = -1; uint crc = 0; bool hasCrc = false, folder = false;
            Action flush = () =>
            {
                if (name != null && !folder && size >= 0 && hasCrc) items.Add(new ZipItem { Name = name.Replace('\\', '/'), Size = size, Crc = crc, Index = items.Count });
                name = null; size = -1; hasCrc = false; folder = false;
            };
            foreach (var raw in output.Substring(start).Split('\n'))
            {
                string line = raw.TrimEnd('\r');
                if (line.Length == 0) { flush(); continue; }
                int eq = line.IndexOf(" = ", StringComparison.Ordinal);
                if (eq < 0) continue;
                string key = line.Substring(0, eq), value = line.Substring(eq + 3);
                if (key == "Path") { flush(); name = value; }
                else if (key == "Size") long.TryParse(value, out size);
                else if (key == "CRC") hasCrc = uint.TryParse(value, System.Globalization.NumberStyles.HexNumber, null, out crc);
                else if (key == "Folder") folder = value == "+";
                else if (key == "Attributes" && value.StartsWith("D")) folder = true;
            }
            flush();
            return items;
        }

        public static void Extract(string archive, string toFolder)
        {
            Directory.CreateDirectory(toFolder);
            Run("x -y -sccUTF-8 -o" + Quote(toFolder) + " -- " + Quote(archive), 600000);
        }

        // Listings already read, so rescans don't run 7-Zip again for files that haven't changed.
        static readonly Dictionary<string, Tuple<long, DateTime, List<ZipItem>>> listCache =
            new Dictionary<string, Tuple<long, DateTime, List<ZipItem>>>(StringComparer.OrdinalIgnoreCase);

        public static List<ZipItem> ListCached(string archive)
        {
            var fi = new FileInfo(archive);
            Tuple<long, DateTime, List<ZipItem>> c;
            lock (listCache)
                if (listCache.TryGetValue(archive, out c) && c.Item1 == fi.Length && c.Item2 == fi.LastWriteTimeUtc) return c.Item3;
            var items = List(archive);
            lock (listCache) listCache[archive] = Tuple.Create(fi.Length, fi.LastWriteTimeUtc, items);
            return items;
        }

        // Renames files inside an archive. 7-Zip copies the packed data across without recompressing it.
        public static void Rename(string archive, IEnumerable<KeyValuePair<string, string>> renames)
        {
            Run("rn -sccUTF-8 -- " + Quote(archive) + " " + string.Join(" ", renames.Select(r => Quote(r.Key) + " " + Quote(r.Value))), 3600000);
        }

        public static void Delete(string archive, IEnumerable<string> names)
        {
            Run("d -sccUTF-8 -- " + Quote(archive) + " " + string.Join(" ", names.Select(Quote)), 3600000);
        }

        // Adds files (named relative to fromFolder) to an existing zip or .7z.
        public static void Add(string archive, string fromFolder, IEnumerable<string> names, bool is7z)
        {
            Run("a -t" + (is7z ? "7z" : "zip") + " -sccUTF-8 -- " + Quote(archive) + " " + string.Join(" ", names.Select(Quote)), 3600000, fromFolder);
        }
    }
}
