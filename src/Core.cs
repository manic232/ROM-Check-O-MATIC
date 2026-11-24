// ROM Check O-MATIC - core logic: DAT reading, zip reading, scanning, fixing.
// Written for the C# 5 compiler that ships with Windows (.NET Framework 4.x).

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading;
using System.Text.RegularExpressions;
using System.Xml;

namespace RomCheckOMatic
{
    // ---------------------------------------------------------------- DAT files

    class DatInfo
    {
        public string Name;     // e.g. "Sega - Mega Drive - Genesis"
        public string Version;  // e.g. "20261121-203036"
        public string Author = "";   // e.g. "redump.org"
        public string Path;
    }

    class DatRom
    {
        public string Name;
        public long Size;
        public uint Crc;
        public bool BadDump;
        public bool Mia;
        public DatGame Game;
        public string Sha1;      // MAME disks (CHDs), which the DAT identifies by SHA1 only
        public string Key { get { return Sha1 != null ? RomKey.OfSha1(Sha1) : RomKey.Of(Size, Crc); } }
    }

    class DatGame
    {
        public string Name;
        public string Id, CloneOf;
        public DatGame Parent;   // the group's parent game (itself if it is the parent)
        public List<DatRom> Roms = new List<DatRom>();   // only roms that have a CRC (can be checked)
        public List<DatRom> Disks = new List<DatRom>();  // MAME disks (CHDs); used as the game's files in a CHD DAT
        public List<string> Categories = new List<string>();
        public GameType Type;    // beta, prototype, unlicensed... worked out from the name and categories
        public bool Mia { get { return Roms.Count > 0 && Roms.All(r => r.Mia); } }
        public bool BadDump { get { return Roms.Any(r => r.BadDump); } }
    }

    class DatFile
    {
        public DatInfo Info;
        public List<DatGame> Games = new List<DatGame>();
        public Dictionary<string, DatGame> ByName = new Dictionary<string, DatGame>(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, List<DatRom>> ByKey = new Dictionary<string, List<DatRom>>();
        public bool HasAftermarket;   // false for packs downloaded without aftermarket games

        static XmlReaderSettings Settings()
        {
            var s = new XmlReaderSettings();
            s.DtdProcessing = DtdProcessing.Ignore;
            s.XmlResolver = null;
            s.IgnoreComments = true;
            s.IgnoreWhitespace = true;
            return s;
        }

        // Reads just the <header> block, so indexing hundreds of DATs is quick.
        public static DatInfo ReadHeader(string path)
        {
            var info = new DatInfo { Path = path };
            if (Cmp.IsCmp(path)) Cmp.ReadHeader(path, info);
            else
            using (var r = XmlReader.Create(path, Settings()))
            {
                bool inHeader = false, onNext = false;
                // ReadElementContentAsString moves on to the next node itself, so don't Read() past it.
                while (onNext || r.Read())
                {
                    onNext = false;
                    if (r.NodeType == XmlNodeType.Element)
                    {
                        if (r.Name == "header") inHeader = true;
                        else if (r.Name == "game" || r.Name == "machine") break;
                        else if (inHeader && r.Name == "name") { info.Name = r.ReadElementContentAsString().Trim(); onNext = true; }
                        else if (inHeader && r.Name == "version") { info.Version = r.ReadElementContentAsString().Trim(); onNext = true; }
                        else if (inHeader && (r.Name == "author" || r.Name == "homepage")) { info.Author += " " + r.ReadElementContentAsString().Trim(); onNext = true; }
                    }
                    else if (r.NodeType == XmlNodeType.EndElement && r.Name == "header") break;
                }
            }
            if (string.IsNullOrEmpty(info.Name))
                info.Name = Naming.StripTrailingGroups(System.IO.Path.GetFileNameWithoutExtension(path)).First();
            if (info.Version == null) info.Version = "";
            return info;
        }

        // Loads a system's DAT, plus any extra DATs for the same system (such as its separate Aftermarket DAT),
        // as one list of games.
        public static DatFile Load(DatInfo info, params DatInfo[] extras)
        {
            var dat = new DatFile { Info = info };
            dat.Extras.AddRange(extras);
            Read(dat, info.Path, info.Name);
            foreach (var x in extras) Read(dat, x.Path, x.Name);
            // Link each game to its parent, so versions of the same title can be grouped (across all the files).
            var byId = new Dictionary<string, DatGame>();
            foreach (var g in dat.Games) if (!string.IsNullOrEmpty(g.Id) && !byId.ContainsKey(g.Id)) byId[g.Id] = g;
            foreach (var g in dat.Games)
            {
                var p = g; int hops = 0;
                DatGame next;
                while (!string.IsNullOrEmpty(p.CloneOf) && byId.TryGetValue(p.CloneOf, out next) && next != p && hops++ < 10) p = next;
                g.Parent = p;
            }

            // Redump's DATs don't group versions, so 1G1R groups them by title instead: "Game (USA)" and
            // "Game (Europe) (Rev 1)" are versions of one title. Disc numbers stay part of the title, so each
            // disc of a game is kept: "Game (USA) (Disc 2)" goes with "Game (Europe) (Disc 2)", never with Disc 1.
            if (!dat.Games.Any(g => !string.IsNullOrEmpty(g.CloneOf)) &&
                new[] { info }.Concat(extras).Any(d => d.Author.IndexOf("redump", StringComparison.OrdinalIgnoreCase) >= 0))
            {
                var first = new Dictionary<string, DatGame>(StringComparer.OrdinalIgnoreCase);
                foreach (var g in dat.Games)
                {
                    string key = TitleKey(g.Name);
                    DatGame p;
                    if (!first.TryGetValue(key, out p)) first[key] = p = g;
                    g.Parent = p;
                }
            }
            return dat;
        }

        // Tags that make a different disc (or a different set of games), not another version of the same one.
        static readonly Regex DiscTag = new Regex(@"^(Disc|Disk|Side|Part|Volume|Vol\.?)\s|^\d+-in-1$", RegexOptions.IgnoreCase);

        // The title a version belongs to: the name before its tags, plus any disc number.
        // "Final Fantasy VII (Europe) (Fr) (Disc 2)" gives "final fantasy vii|disc 2".
        public static string TitleKey(string name)
        {
            int cut = name.IndexOf(" (", StringComparison.Ordinal);
            string title = (cut > 0 ? name.Substring(0, cut) : name).Trim();
            var discs = Preference.Tags(name).Where(t => DiscTag.IsMatch(t)).Select(t => t.ToLowerInvariant());
            return title.ToLowerInvariant() + "|" + string.Join("|", discs);
        }

        public List<DatInfo> Extras = new List<DatInfo>();

        // Adds a ROM to a game, if the DAT gives it a size and CRC (so it can be checked).
        internal static void AddRom(DatGame game, string name, string size, string crc, string status, bool mia)
        {
            uint c; long s;
            if (status == "nodump" || crc == null || size == null) return;
            if (!uint.TryParse(crc, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out c)) return;
            if (!long.TryParse(size, NumberStyles.Integer, CultureInfo.InvariantCulture, out s)) return;
            string n = (name ?? "").Replace('\\', '/');
            // MAME DATs can list the same ROM twice (used by two parts of a machine); a zip can only hold it once.
            if (game.Roms.Any(r => r.Crc == c && r.Size == s && r.Name == n)) return;
            game.Roms.Add(new DatRom { Name = n, Size = s, Crc = c, Game = game, BadDump = status == "baddump", Mia = mia });
        }

        // Adds a finished game to the DAT and its lookups.
        internal static void AddGame(DatFile dat, DatGame game, string datName)
        {
            if (game.Roms.Count == 0 || dat.ByName.ContainsKey(game.Name)) return;
            game.Type = GameTypes.Of(game.Name, game.Categories, datName);
            if ((game.Type & GameType.Aftermarket) != 0) dat.HasAftermarket = true;
            dat.Games.Add(game);
            dat.ByName[game.Name] = game;
            foreach (var rom in game.Roms)
            {
                List<DatRom> list;
                if (!dat.ByKey.TryGetValue(rom.Key, out list)) dat.ByKey[rom.Key] = list = new List<DatRom>();
                list.Add(rom);
                if (rom.Sha1 != null) continue;
                if (!dat.ByCrc.TryGetValue(rom.Crc, out list)) dat.ByCrc[rom.Crc] = list = new List<DatRom>();
                list.Add(rom);
            }
        }

        // By CRC alone: NKit discs keep the original's CRC but not its size.
        public Dictionary<uint, List<DatRom>> ByCrc = new Dictionary<uint, List<DatRom>>();

        static void Read(DatFile dat, string path, string datName)
        {
            if (Cmp.IsCmp(path)) { Cmp.Read(dat, path, datName); return; }
            // A MAME CHD DAT lists only disks; a DAT that also lists ROMs is checked for its ROMs only (as before),
            // since its zips can't hold the disks.
            var diskOnly = new List<DatGame>();
            int romGames = 0;
            using (var r = XmlReader.Create(path, Settings()))
            {
                DatGame game = null;
                bool onNext = false;
                while (onNext || r.Read())
                {
                    onNext = false;
                    if (r.NodeType == XmlNodeType.Element && (r.Name == "game" || r.Name == "machine"))
                    {
                        game = new DatGame { Name = r.GetAttribute("name") ?? "", Id = r.GetAttribute("id"), CloneOf = r.GetAttribute("cloneofid") };
                        if (r.IsEmptyElement) game = null;
                    }
                    else if (r.NodeType == XmlNodeType.Element && r.Name == "category" && game != null && !r.IsEmptyElement)
                    {
                        game.Categories.Add(r.ReadElementContentAsString().Trim());
                        onNext = true;
                    }
                    else if (r.NodeType == XmlNodeType.Element && r.Name == "rom" && game != null)
                    {
                        AddRom(game, r.GetAttribute("name"), r.GetAttribute("size"), r.GetAttribute("crc"),
                               r.GetAttribute("status") ?? "", r.GetAttribute("mia") == "yes");
                    }
                    else if (r.NodeType == XmlNodeType.Element && r.Name == "disk" && game != null)
                    {
                        string sha1 = (r.GetAttribute("sha1") ?? "").Trim().ToLowerInvariant();
                        string name = (r.GetAttribute("name") ?? "").Replace('\\', '/') + ".chd";
                        if (sha1.Length == 40 && r.GetAttribute("status") != "nodump" && !game.Disks.Any(d => d.Sha1 == sha1 && d.Name == name))
                            game.Disks.Add(new DatRom { Name = name, Sha1 = sha1, Game = game, BadDump = r.GetAttribute("status") == "baddump" });
                    }
                    else if (r.NodeType == XmlNodeType.EndElement && (r.Name == "game" || r.Name == "machine") && game != null)
                    {
                        if (game.Roms.Count > 0) { romGames++; AddGame(dat, game, datName); }
                        else if (game.Disks.Count > 0) diskOnly.Add(game);
                        game = null;
                    }
                }
            }
            if (romGames == 0)
                foreach (var g in diskOnly) { g.Roms = g.Disks; AddGame(dat, g, datName); }
        }

        public static List<List<DatGame>> Groups(IEnumerable<DatGame> games)
        {
            var groups = new Dictionary<DatGame, List<DatGame>>();
            var order = new List<List<DatGame>>();
            foreach (var g in games)
            {
                List<DatGame> list;
                if (!groups.TryGetValue(g.Parent, out list)) { groups[g.Parent] = list = new List<DatGame>(); order.Add(list); }
                list.Add(g);
            }
            return order;
        }
    }

    // Kinds of game that can be left out. Worked out from No-Intro's name tags, such as "(Beta 2)" or "(Unl)".
    [Flags]
    enum GameType
    {
        None = 0, Beta = 1, Proto = 2, Demo = 4, Unlicensed = 8, Pirate = 16, Aftermarket = 32, Bios = 64, Program = 128
    }

    static class GameTypes
    {
        public static readonly GameType[] All = {
            GameType.Beta, GameType.Proto, GameType.Demo, GameType.Unlicensed, GameType.Pirate,
            GameType.Aftermarket, GameType.Bios, GameType.Program };

        public static string Label(GameType t)
        {
            switch (t)
            {
                case GameType.Beta: return "Betas, alphas and release candidates";
                case GameType.Proto: return "Prototypes, debug and development builds";
                case GameType.Demo: return "Demos, samples and kiosk versions";
                case GameType.Unlicensed: return "Unlicensed ROMs";
                case GameType.Pirate: return "Pirate ROMs";
                case GameType.Aftermarket: return "Aftermarket ROMs (new releases for old systems)";
                case GameType.Bios: return "BIOS files";
                case GameType.Program: return "Applications and test programs";
                default: return t.ToString();
            }
        }

        public static string Short(GameType t)
        {
            switch (t)
            {
                case GameType.Beta: return "betas";
                case GameType.Proto: return "prototypes";
                case GameType.Demo: return "demos";
                case GameType.Unlicensed: return "unlicensed";
                case GameType.Pirate: return "pirate";
                case GameType.Aftermarket: return "aftermarket";
                case GameType.Bios: return "BIOS";
                case GameType.Program: return "applications";
                default: return t.ToString();
            }
        }

        public static string Describe(GameType set)
        {
            return string.Join(", ", All.Where(t => (set & t) != 0).Select(Short));
        }

        // Checked against every game name in the 2026-11-22 Love Pack (main and Aftermarket DATs).
        static readonly Regex BetaTag = new Regex(@"\b(Beta|Alpha)\b|^RC ?\d", RegexOptions.IgnoreCase);
        static readonly Regex ProtoTag = new Regex(@"\b(Proto|Prototype|Debug|Development|Developer Build)\b", RegexOptions.IgnoreCase);
        static readonly Regex DemoTag = new Regex(@"\b(Demo|Autodemo|Sample|Kiosk|Trial|Taikenban|Preview|Promo)\b", RegexOptions.IgnoreCase);
        static readonly Regex ProgramTag = new Regex(@"^((Test|Music) )?Program$", RegexOptions.IgnoreCase);

        public static GameType Of(string name, IEnumerable<string> categories, string datName = null)
        {
            var t = GameType.None;
            var tags = Preference.Tags(name);
            bool aftermarket = tags.Contains("Aftermarket"), pirate = tags.Contains("Pirate");
            foreach (var tag in tags)
            {
                if (BetaTag.IsMatch(tag)) t |= GameType.Beta;
                if (ProtoTag.IsMatch(tag)) t |= GameType.Proto;
                if (DemoTag.IsMatch(tag)) t |= GameType.Demo;
                if (ProgramTag.IsMatch(tag)) t |= GameType.Program;
            }
            if (aftermarket) t |= GameType.Aftermarket;
            if (pirate) t |= GameType.Pirate;
            // Aftermarket and pirate games are usually also "(Unl)"; they have their own options.
            if (tags.Contains("Unl") && !aftermarket && !pirate) t |= GameType.Unlicensed;
            if (name.StartsWith("[BIOS]")) t |= GameType.Bios;
            if (categories != null)
                foreach (var c in categories)
                {
                    if (c == "Demos") t |= GameType.Demo;
                    else if (c == "Applications") t |= GameType.Program;
                    // No-Intro files unfinished builds as "Preproduction", even when the name doesn't say so.
                    else if (c == "Preproduction" && (t & (GameType.Beta | GameType.Proto | GameType.Demo)) == 0) t |= GameType.Proto;
                }
            return t;
        }

        public static GameType Parse(string list)
        {
            var t = GameType.None;
            foreach (var part in (list ?? "").Split(','))
            {
                GameType one;
                if (Enum.TryParse(part.Trim(), true, out one)) t |= one;
            }
            return t;
        }

        public static string ToList(GameType set)
        {
            return string.Join(",", All.Where(t => (set & t) != 0).Select(t => t.ToString()));
        }
    }

    // Picks the one version of each title to keep, when "one game per title" is on.
    class Preference
    {
        public static readonly string DefaultRegions = "USA, World, Europe, United Kingdom, Australia, Canada, Japan, Asia, Korea, Brazil";

        // Every region used in the 2026-11-22 Love Pack (main and Aftermarket DATs), as No-Intro spells them.
        static readonly string[] UsedRegions = {
            "USA", "Japan", "Europe", "World", "Asia", "Unknown", "Korea", "Germany", "China", "Australia", "France",
            "Taiwan", "United Kingdom", "Brazil", "Spain", "Italy", "Russia", "Netherlands", "Canada", "Sweden", "Denmark",
            "Latin America", "Poland", "Hong Kong", "Finland", "Austria", "Norway", "New Zealand", "Portugal", "UAE",
            "Scandinavia", "Mexico", "Greece", "Peru", "Argentina", "India", "Hungary", "Czechia", "Belgium", "Romania", "Bulgaria" };

        // Also recognised, in case future DATs use them.
        static readonly HashSet<string> KnownRegions = new HashSet<string>(UsedRegions.Concat(new[] {
            "South Africa", "Switzerland", "Croatia", "Ireland", "Turkey", "Israel", "Singapore", "Thailand", "Chile",
            "Colombia", "Venezuela", "Indonesia", "Malaysia", "Philippines", "Vietnam", "Ukraine", "Slovakia",
            "Slovenia", "Serbia", "Estonia", "Latvia", "Lithuania", "Iceland", "Luxembourg" }), StringComparer.OrdinalIgnoreCase);

        // Other spellings, e.g. from settings saved before this list was checked against the DATs.
        static readonly Dictionary<string, string> Aliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) {
            { "UK", "United Kingdom" }, { "United Arab Emirates", "UAE" }, { "Czech", "Czechia" }, { "Czech Republic", "Czechia" } };

        public static string Canonical(string region)
        {
            string r = region.Trim(), c;
            return Aliases.TryGetValue(r, out c) ? c : r;
        }

        public static List<string> ParseList(string list)
        {
            var result = new List<string>();
            foreach (var r in (list ?? "").Split(',').Select(Canonical).Where(x => x.Length > 0))
                if (!result.Contains(r, StringComparer.OrdinalIgnoreCase)) result.Add(r);
            return result;
        }

        static readonly Regex NonFinal = new Regex(
            @"\b(Beta|Alpha|Proto|Prototype|Demo|Autodemo|Sample|Kiosk|Promo|Preview|Debug|Development|Developer Build|Pre-Production|Taikenban|Trial)\b|^RC ?\d",
            RegexOptions.IgnoreCase);
        static readonly Regex Language = new Regex(@"^[A-Z][a-z](-[A-Za-z]+)?(,[A-Z][a-z](-[A-Za-z]+)?)*$");
        static readonly Regex Rev = new Regex(@"^Rev ([0-9A-Z]+)$");
        static readonly Regex Ver = new Regex(@"^v(\d+(\.\d+)*)[a-z]?$");

        readonly List<string> regions;
        public Preference(string regionList)
        {
            regions = ParseList(regionList);
        }

        // Every region No-Intro uses, with the default order first and the rest A to Z.
        public static List<string> AllRegions()
        {
            var first = ParseList(DefaultRegions);
            return first.Concat(UsedRegions.Where(r => !first.Contains(r, StringComparer.OrdinalIgnoreCase)).OrderBy(r => r)).ToList();
        }

        // The regions a game was released in, from its first region tag: "(USA, Brazil)" gives USA and Brazil.
        public static List<string> RegionsOf(string name)
        {
            foreach (var tag in Tags(name))
            {
                var parts = tag.Split(',').Select(x => x.Trim()).ToList();
                if (parts.All(x => KnownRegions.Contains(x))) return parts;
            }
            return new List<string>();
        }

        public static List<string> Tags(string name)
        {
            var tags = new List<string>();
            foreach (Match m in Regex.Matches(name, @"\(([^()]*)\)")) tags.Add(m.Groups[1].Value.Trim());
            return tags;
        }

        class Score
        {
            public int NonFinal, Region, Extras, NoEnglish, Parent, Rev;
            public Version Ver;
        }

        Score Rate(DatGame g)
        {
            var s = new Score { Region = regions.Count, Ver = new Version(0, 0), Parent = g.Parent == g ? 0 : 1 };
            bool regionSeen = false, langSeen = false, english = false;
            foreach (var tag in Tags(g.Name))
            {
                var parts = tag.Split(',').Select(x => x.Trim()).ToList();
                Match m;
                if (!regionSeen && parts.All(x => KnownRegions.Contains(x)))
                {
                    regionSeen = true;
                    foreach (var x in parts)
                    {
                        string region = x;
                        int i = regions.FindIndex(r => r.Equals(region, StringComparison.OrdinalIgnoreCase));
                        if (i >= 0 && i < s.Region) s.Region = i;
                    }
                }
                else if (Language.IsMatch(tag)) { langSeen = true; english |= parts.Any(x => x.StartsWith("En")); }
                else if ((m = Rev.Match(tag)).Success)
                {
                    int n;
                    s.Rev = int.TryParse(m.Groups[1].Value, out n) ? n : m.Groups[1].Value[0] - 'A' + 1;
                }
                else if ((m = Ver.Match(tag)).Success)
                {
                    Version v;
                    string t = m.Groups[1].Value; if (!t.Contains(".")) t += ".0";
                    if (Version.TryParse(t, out v)) s.Ver = v;
                }
                else if (NonFinal.IsMatch(tag)) s.NonFinal = 1;
                else if (tag != "Unl" && tag != "Aftermarket") s.Extras++;   // re-releases, compilations, Alt, Virtual Console...
            }
            s.NoEnglish = langSeen && !english ? 1 : 0;
            return s;
        }

        // Best first.
        public List<DatGame> Rank(IEnumerable<DatGame> games)
        {
            return games.Select(g => new { g, s = Rate(g) })
                .OrderBy(x => x.s.NonFinal).ThenBy(x => x.s.Region).ThenBy(x => x.s.Extras).ThenBy(x => x.s.NoEnglish)
                .ThenByDescending(x => x.s.Ver).ThenByDescending(x => x.s.Rev).ThenBy(x => x.s.Parent)
                .ThenBy(x => x.g.Name, StringComparer.Ordinal)
                .Select(x => x.g).ToList();
        }
    }

    static class RomKey
    {
        public static string Of(long size, uint crc)
        {
            return size.ToString(CultureInfo.InvariantCulture) + ":" + crc.ToString("x8", CultureInfo.InvariantCulture);
        }

        public static string OfSha1(string sha1) { return "sha1:" + sha1; }
    }

    // MAME CHD files (compressed hard disks, CD-ROMs and laserdiscs). The SHA1 the DAT gives is stored in the
    // file's header, so a CHD is identified without reading the rest of it (they can be many gigabytes).
    static class Chd
    {
        public static bool IsChd(string path) { return path.EndsWith(".chd", StringComparison.OrdinalIgnoreCase); }

        // Current MAME DATs give a CHD's overall SHA1 (its data and metadata); older ones (MAME 2003 era) give
        // the SHA1 of the raw data only. The header holds both, so a CHD matches a DAT entry with either.
        public static string Sha1(string path) { return Read(path, false); }
        public static string RawSha1(string path) { return Read(path, true); }

        public static bool Matches(string path, DatRom rom)
        {
            return rom != null && (Sha1(path) == rom.Sha1 || RawSha1(path) == rom.Sha1);
        }

        static string Read(string path, bool raw)
        {
            var h = new byte[124];
            int n;
            using (var s = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 4096))
                n = s.Read(h, 0, h.Length);
            if (n < 16 || Encoding.ASCII.GetString(h, 0, 8) != "MComprHD") throw new InvalidDataException("Not a CHD file");
            int version = (h[12] << 24) | (h[13] << 16) | (h[14] << 8) | h[15];
            // Version 5: raw SHA1 at 64, overall at 84. Version 4: overall at 48, raw at 88. Version 3: one SHA1, at 80.
            int at = version == 5 ? (raw ? 64 : 84) : version == 4 ? (raw ? 88 : 48) : version == 3 ? 80 : -1;
            if (at < 0) throw new InvalidDataException("CHD version " + version + " is too old to check (update it with chdman)");
            if (n < at + 20) throw new InvalidDataException("The CHD's header is cut short");
            var sb = new StringBuilder(40);
            for (int i = at; i < at + 20; i++) sb.Append(h[i].ToString("x2", CultureInfo.InvariantCulture));
            return sb.ToString();
        }
    }

    static class Naming
    {
        // "Nintendo - Nintendo DS (Decrypted) (20240313-082215) (Retool, Europe) (7 MIA)" gives that name,
        // then without "(7 MIA)", then without "(Retool, Europe)", and so on.
        public static IEnumerable<string> StripTrailingGroups(string name)
        {
            string n = name.Trim();
            while (n.Length > 0)
            {
                yield return n;
                if (!(n.EndsWith(")") || n.EndsWith("]"))) yield break;
                char open = n.EndsWith(")") ? '(' : '[';
                int i = n.LastIndexOf(open);
                if (i <= 0) yield break;
                n = n.Substring(0, i).TrimEnd();
            }
        }

        public static string Size(long bytes)
        {
            if (bytes < 1024) return bytes + " B";
            string[] units = { "KB", "MB", "GB", "TB" };
            double v = bytes; int u = -1;
            do { v /= 1024; u++; } while (v >= 1024 && u < units.Length - 1);
            return v.ToString(v < 10 ? "0.0" : "0", CultureInfo.CurrentCulture) + " " + units[u];
        }
    }

    // All the DATs found under the DAT folder. Where a DAT appears more than once
    // (an old pack and a new pack side by side), the newest version wins.
    class DatIndex
    {
        public string Root;
        public Dictionary<string, DatInfo> ByName = new Dictionary<string, DatInfo>(StringComparer.OrdinalIgnoreCase);
        public List<string> Errors = new List<string>();

        // TOSEC's DATs say so in their header ("homepage: TOSEC").
        public bool IsTosec
        {
            get { return ByName.Values.Any(i => i.Author.IndexOf("TOSEC", StringComparison.OrdinalIgnoreCase) >= 0); }
        }

        // Disc sets (Redump's DATs, which say so in their header) use cue sheets.
        public bool UsesCueSheets
        {
            get { return ByName.Values.Any(i => i.Author.IndexOf("redump", StringComparison.OrdinalIgnoreCase) >= 0); }
        }
        readonly Dictionary<string, DatFile> loaded = new Dictionary<string, DatFile>(StringComparer.OrdinalIgnoreCase);
        readonly object sync = new object();

        public static DatIndex Build(string root, CancellationToken ct)
        {
            var index = new DatIndex { Root = root };
            if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) return index;
            // MAME's DATs often end in ".xml" instead of ".dat".
            foreach (var path in Files.Enumerate(root, "*.dat").Concat(Files.Enumerate(root, "*.xml").Where(IsXmlDat)))
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    var info = DatFile.ReadHeader(path);
                    DatInfo old;
                    if (!index.ByName.TryGetValue(info.Name, out old) || string.CompareOrdinal(info.Version, old.Version) >= 0)
                        index.ByName[info.Name] = info;
                }
                catch (Exception ex) { index.Errors.Add(path + ": " + ex.Message); }
            }
            return index;
        }

        // An .xml file is a DAT when it's a Logiqx "datafile" or a MAME "mame" list.
        static bool IsXmlDat(string path)
        {
            try
            {
                var buf = new char[8192];
                int n;
                using (var r = new StreamReader(path, true)) n = r.ReadBlock(buf, 0, buf.Length);
                string head = new string(buf, 0, n);
                return head.Contains("<datafile") || head.Contains("<mame");
            }
            catch { return false; }
        }

        public DatInfo Find(string folderName, IDictionary<string, string> manual)
        {
            string mapped; DatInfo info;
            if (manual != null && manual.TryGetValue(folderName, out mapped) && ByName.TryGetValue(mapped, out info)) return info;
            var names = Naming.StripTrailingGroups(folderName).ToList();
            foreach (var n in names)
                if (ByName.TryGetValue(n, out info)) return info;

            // MAME: "MAME 0.288 ROMs (non-merged)" matches "MAME ROMs (non-merged)" (the current DAT, which has
            // no version in its name), or "MAME 0.288 ROMs (non-merged)" if there's a DAT for that version.
            if (folderName.StartsWith("MAME", StringComparison.OrdinalIgnoreCase))
                foreach (var n in names)
                {
                    string key = Unversioned(n);
                    var hits = ByName.Values.Where(d => Unversioned(d.Name) == key).ToList();
                    if (hits.Count == 0) continue;
                    var same = hits.FirstOrDefault(d => MameVersion.Match(d.Name).Value == MameVersion.Match(n).Value);
                    var current = hits.Where(d => !MameVersion.IsMatch(d.Name)).ToList();
                    return same ?? (current.Count == 1 ? current[0] : hits.OrderByDescending(d => d.Version, StringComparer.Ordinal).First());
                }

            // No-Intro renames systems now and then, so folders made from older DATs may not match exactly.
            // Try looser matches, but only use one when exactly one DAT fits.
            var mains = ByName.Values.Where(d => !d.Name.EndsWith("(Aftermarket)", StringComparison.OrdinalIgnoreCase)).ToList();
            foreach (var n in names)
            {
                // "Atari - 7800 (BIN)" matches "Atari - Atari 7800 (BIN)".
                string key = Loose(n);
                var hits = mains.Where(d => Loose(d.Name) == key).ToList();
                if (hits.Count == 1) return hits[0];
            }
            foreach (var n in names)
            {
                // "Sega - SG-1000" matches "Sega - SG-1000 - SC-3000".
                if (!n.Contains(" - ")) continue;
                string key = Loose(n) + " - ";
                var hits = mains.Where(d => Loose(d.Name).StartsWith(key, StringComparison.Ordinal)).ToList();
                if (hits.Count == 1) return hits[0];
            }

            // Redump-style folder names leave out the separators and shorten some names:
            // "Sony PlayStation" for "Sony - PlayStation", "Sega Mega-CD - Sega CD" for "Sega - Mega CD & Sega CD",
            // "Atari Jaguar CD" for "Atari - Jaguar CD Interactive Multimedia System", "Sony PSP" for "Sony - PlayStation Portable".
            foreach (var n in names)
            {
                string key = Words(n);
                var hits = mains.Where(d => Words(d.Name) == key).ToList();
                if (hits.Count == 1) return hits[0];
            }
            foreach (var n in names)
            {
                string key = Words(n);
                if (key.IndexOf(' ') < 0) continue;   // at least a maker and a system
                var hits = mains.Where(d => Words(d.Name).StartsWith(key + " ", StringComparison.Ordinal)).ToList();
                if (hits.Count == 1) return hits[0];
            }
            return null;
        }

        // A MAME version, such as "0.288", "0.37b5" or "0.100u2".
        public static readonly Regex MameVersion = new Regex(@"\b\d+\.\d+[a-z]*\d*\b", RegexOptions.IgnoreCase);

        static string Unversioned(string name)
        {
            return Regex.Replace(MameVersion.Replace(name, " "), @"\s+", " ").Trim().ToLowerInvariant();
        }

        static readonly Dictionary<string, string> ShortNames = new Dictionary<string, string> {
            { "psp", "playstation portable" }, { "psx", "playstation" }, { "ps1", "playstation" }, { "ps2", "playstation 2" },
            { "ps3", "playstation 3" }, { "gc", "gamecube" }, { "ngc", "gamecube" },
            { "c64", "64" } };   // TOSEC's "Commodore C64" for a "Commodore 64" folder

        // Just the words, lower case: punctuation, "&" and "and" dropped, short names spelled out.
        // "Sega - Mega CD & Sega CD" and "Sega Mega-CD - Sega CD" both give "sega mega cd sega cd".
        static string Words(string name)
        {
            var sb = new StringBuilder();
            foreach (char c in name.ToLowerInvariant()) sb.Append(char.IsLetterOrDigit(c) ? c : ' ');
            var words = sb.ToString().Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries)
                          .Where(w => w != "and")
                          .Select(w => { string full; return ShortNames.TryGetValue(w, out full) ? full : w; });
            return string.Join(" ", words);
        }

        // Lower case, with a repeated maker name dropped: "Atari - Atari 7800 (BIN)" gives "atari - 7800 (bin)".
        static string Loose(string name)
        {
            string n = name.Trim().ToLowerInvariant();
            int i = n.IndexOf(" - ", StringComparison.Ordinal);
            if (i > 0)
            {
                string maker = n.Substring(0, i), rest = n.Substring(i + 3);
                if (rest.StartsWith(maker + " ", StringComparison.Ordinal)) n = maker + " - " + rest.Substring(maker.Length + 1);
            }
            return n;
        }

        public DatFile Load(DatInfo info)
        {
            lock (sync)
            {
                DatFile dat;
                if (!loaded.TryGetValue(info.Path, out dat))
                {
                    var extra = Companion(info);
                    loaded[info.Path] = dat = extra == null ? DatFile.Load(info) : DatFile.Load(info, extra);
                }
                return dat;
            }
        }

        // No-Intro puts aftermarket games in a separate DAT, e.g. "Sega - Mega Drive - Genesis (Aftermarket)".
        // Those games normally sit in the same folder as the rest, so the two are checked together.
        public DatInfo Companion(DatInfo info)
        {
            DatInfo extra;
            if (info.Name.EndsWith("(Aftermarket)", StringComparison.OrdinalIgnoreCase)) return null;
            return ByName.TryGetValue(info.Name + " (Aftermarket)", out extra) ? extra : null;
        }

        // Love Pack folders such as "No-Intro Love Pack (DAT) (2026-11-22)", newest last.
        public static List<string> PackFolders(string root)
        {
            try
            {
                return Directory.GetDirectories(root, "No-Intro Love Pack*")
                    .OrderBy(d => Path.GetFileName(d), StringComparer.OrdinalIgnoreCase).ToList();
            }
            catch { return new List<string>(); }
        }
    }

    static class Files
    {
        // Like Directory.EnumerateFiles(AllDirectories), but skips folders it can't read instead of failing.
        public static IEnumerable<string> Enumerate(string root, string pattern)
        {
            var stack = new Stack<string>();
            stack.Push(root);
            while (stack.Count > 0)
            {
                string dir = stack.Pop();
                string[] files = null, dirs = null;
                try { files = Directory.GetFiles(dir, pattern); Array.Sort(files, StringComparer.OrdinalIgnoreCase); } catch { }
                if (files != null) foreach (var f in files) yield return f;
                try { dirs = Directory.GetDirectories(dir); Array.Sort(dirs, StringComparer.OrdinalIgnoreCase); } catch { }
                if (dirs != null) for (int i = dirs.Length - 1; i >= 0; i--) stack.Push(dirs[i]);
            }
        }
    }

    // ---------------------------------------------------------------- zip and CRC reading

    class ZipItem
    {
        public string Name;
        public long Size;
        public uint Crc;
        public int Index;   // position in the zip's central directory (matches ZipArchive.Entries)
        public string Sha1; // a CHD's SHA1, from its header
        public string RawSha1; // and the SHA1 of its raw data (what older DATs give)
        public string FullName;   // in a TOSEC pack: the entry's whole path, "Game/Game.adf"
        // Where and how the entry is stored in its zip, for copying it to another zip as it is.
        public int Method, DosTime, DosDate, Flags;
        public long CompressedSize, LocalOffset = -1;
        public string Key { get { return Sha1 != null ? RomKey.OfSha1(Sha1) : RomKey.Of(Size, Crc); } }
        public bool IsNKit
        {
            get { return Name.EndsWith(".nkit.iso", StringComparison.OrdinalIgnoreCase) || Name.EndsWith(".nkit.gcz", StringComparison.OrdinalIgnoreCase); }
        }
    }

    static class ZipReader
    {
        static readonly Encoding Cp437 = Encoding.GetEncoding(437);

        static uint U32(byte[] b, int i) { return (uint)(b[i] | b[i + 1] << 8 | b[i + 2] << 16 | b[i + 3] << 24); }
        static int U16(byte[] b, int i) { return b[i] | b[i + 1] << 8; }
        static ulong U64(byte[] b, int i) { return U32(b, i) | (ulong)U32(b, i + 4) << 32; }

        static void ReadFully(Stream s, byte[] buf, int count)
        {
            int got = 0;
            while (got < count)
            {
                int n = s.Read(buf, got, count - got);
                if (n <= 0) throw new InvalidDataException("The zip file is cut short (damaged or still downloading)");
                got += n;
            }
        }

        // Reads the zip's central directory: names, sizes and CRCs, without unpacking anything.
        public static List<ZipItem> Read(string path)
        {
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096))
            {
                long len = fs.Length;
                if (len < 22) throw new InvalidDataException("Too small to be a zip file");
                int tail = (int)Math.Min(len, 22 + 65535 + 20);
                var buf = new byte[tail];
                fs.Seek(len - tail, SeekOrigin.Begin);
                ReadFully(fs, buf, tail);

                int eocd = -1;
                for (int i = tail - 22; i >= 0; i--)
                    if (U32(buf, i) == 0x06054b50) { eocd = i; break; }
                if (eocd < 0) throw new InvalidDataException("Not a zip file, or the zip is damaged");

                long count = U16(buf, eocd + 10);
                long cdSize = U32(buf, eocd + 12);
                long cdOff = U32(buf, eocd + 16);
                if (count == 0xFFFF || cdSize == 0xFFFFFFFF || cdOff == 0xFFFFFFFF)
                {
                    int loc = eocd - 20;
                    if (loc >= 0 && U32(buf, loc) == 0x07064b50)
                    {
                        long z64 = (long)U64(buf, loc + 8);
                        var rec = new byte[56];
                        fs.Seek(z64, SeekOrigin.Begin);
                        ReadFully(fs, rec, 56);
                        if (U32(rec, 0) != 0x06064b50) throw new InvalidDataException("The zip is damaged (bad Zip64 record)");
                        count = (long)U64(rec, 32);
                        cdSize = (long)U64(rec, 40);
                        cdOff = (long)U64(rec, 48);
                    }
                }
                if (cdOff < 0 || cdSize < 0 || cdOff + cdSize > len || cdSize > 256L * 1024 * 1024)
                    throw new InvalidDataException("The zip is damaged (bad central directory)");

                var cd = new byte[cdSize];
                fs.Seek(cdOff, SeekOrigin.Begin);
                ReadFully(fs, cd, (int)cdSize);

                var list = new List<ZipItem>();
                int p = 0, index = 0;
                while (p + 46 <= cd.Length && U32(cd, p) == 0x02014b50)
                {
                    int flags = U16(cd, p + 8);
                    int method = U16(cd, p + 10), time = U16(cd, p + 12), date = U16(cd, p + 14);
                    uint crc = U32(cd, p + 16);
                    long comp = U32(cd, p + 20);
                    long size = U32(cd, p + 24);
                    int nl = U16(cd, p + 28), xl = U16(cd, p + 30), cl = U16(cd, p + 32);
                    long local = U32(cd, p + 42);
                    if (p + 46 + nl + xl + cl > cd.Length) throw new InvalidDataException("The zip is damaged (bad entry)");
                    string name = ((flags & 0x800) != 0 ? Encoding.UTF8 : Cp437).GetString(cd, p + 46, nl);
                    if (size == 0xFFFFFFFF || comp == 0xFFFFFFFF || local == 0xFFFFFFFF)
                    {
                        // Zip64: the large values are in an extra field, in this order, only for those that overflowed.
                        int x = p + 46 + nl, end = x + xl;
                        while (x + 4 <= end)
                        {
                            int id = U16(cd, x), sz = U16(cd, x + 2);
                            if (id == 1)
                            {
                                int q = x + 4, qend = x + 4 + sz;
                                if (size == 0xFFFFFFFF && q + 8 <= qend) { size = (long)U64(cd, q); q += 8; }
                                if (comp == 0xFFFFFFFF && q + 8 <= qend) { comp = (long)U64(cd, q); q += 8; }
                                if (local == 0xFFFFFFFF && q + 8 <= qend) { local = (long)U64(cd, q); q += 8; }
                                break;
                            }
                            x += 4 + sz;
                        }
                    }
                    if (!name.EndsWith("/") && !name.EndsWith("\\"))
                        list.Add(new ZipItem { Name = name.Replace('\\', '/'), Size = size, Crc = crc, Index = index,
                                               Method = method, CompressedSize = comp, LocalOffset = local, DosTime = time, DosDate = date, Flags = flags });
                    index++;
                    p += 46 + nl + xl + cl;
                }
                if (index == 0 && count > 0) throw new InvalidDataException("The zip is damaged (no entries found)");
                return list;
            }
        }
    }

    // Writes a new zip by copying entries from another zip as they are (still compressed), under new names.
    // Nothing is recompressed, so even a pack of many gigabytes is only as slow as copying the file.
    static class ZipCopier
    {
        static void W16(Stream s, int v) { s.WriteByte((byte)v); s.WriteByte((byte)(v >> 8)); }
        static void W32(Stream s, uint v) { W16(s, (int)(v & 0xFFFF)); W16(s, (int)(v >> 16)); }
        static void W64(Stream s, long v) { W32(s, (uint)(v & 0xFFFFFFFF)); W32(s, (uint)((ulong)v >> 32)); }

        // entries: each source entry (from ZipReader.Read of source) and the name it gets in the new zip.
        public static void Write(string source, IList<Tuple<ZipItem, string>> entries, string target, Action<long> progress, CancellationToken ct)
        {
            var written = new List<Tuple<ZipItem, string, long, bool>>();   // entry, name, local header offset, utf8
            var buf = new byte[1 << 20];
            long copied = 0;
            using (var src = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16))
            using (var dst = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16))
            {
                var head = new byte[30];
                foreach (var e in entries)
                {
                    ct.ThrowIfCancellationRequested();
                    var item = e.Item1;
                    if (item.LocalOffset < 0) throw new InvalidDataException("Can't copy \"" + item.Name + "\"");
                    src.Seek(item.LocalOffset, SeekOrigin.Begin);
                    if (src.Read(head, 0, 30) != 30 || (head[0] | head[1] << 8 | head[2] << 16 | head[3] << 24) != 0x04034b50)
                        throw new InvalidDataException("The zip is damaged at \"" + item.Name + "\"");
                    long dataStart = item.LocalOffset + 30 + (head[26] | head[27] << 8) + (head[28] | head[29] << 8);

                    string name = e.Item2;
                    bool utf8 = name.Any(c => c > 126);
                    byte[] nameBytes = (utf8 ? Encoding.UTF8 : Encoding.ASCII).GetBytes(name);
                    bool big = item.Size >= 0xFFFFFFFFL || item.CompressedSize >= 0xFFFFFFFFL;
                    long offset = dst.Position;
                    int flags = (item.Flags & 0x0006) | (utf8 ? 0x800 : 0);   // keep the compression option bits only
                    W32(dst, 0x04034b50); W16(dst, big ? 45 : 20); W16(dst, flags); W16(dst, item.Method);
                    W16(dst, item.DosTime); W16(dst, item.DosDate); W32(dst, item.Crc);
                    W32(dst, big ? 0xFFFFFFFF : (uint)item.CompressedSize); W32(dst, big ? 0xFFFFFFFF : (uint)item.Size);
                    W16(dst, nameBytes.Length); W16(dst, big ? 20 : 0);
                    dst.Write(nameBytes, 0, nameBytes.Length);
                    if (big) { W16(dst, 1); W16(dst, 16); W64(dst, item.Size); W64(dst, item.CompressedSize); }

                    src.Seek(dataStart, SeekOrigin.Begin);
                    long left = item.CompressedSize;
                    while (left > 0)
                    {
                        int n = src.Read(buf, 0, (int)Math.Min(buf.Length, left));
                        if (n <= 0) throw new InvalidDataException("The zip is cut short at \"" + item.Name + "\"");
                        dst.Write(buf, 0, n);
                        left -= n;
                        copied += n;
                    }
                    written.Add(Tuple.Create(item, name, offset, utf8));
                    if (progress != null) progress(copied);
                }

                long cdStart = dst.Position;
                foreach (var w in written)
                {
                    var item = w.Item1;
                    byte[] nameBytes = (w.Item4 ? Encoding.UTF8 : Encoding.ASCII).GetBytes(w.Item2);
                    bool bigSize = item.Size >= 0xFFFFFFFFL || item.CompressedSize >= 0xFFFFFFFFL;
                    bool bigOff = w.Item3 >= 0xFFFFFFFFL;
                    int extra = (bigSize ? 16 : 0) + (bigOff ? 8 : 0);
                    W32(dst, 0x02014b50); W16(dst, 45); W16(dst, bigSize || bigOff ? 45 : 20);
                    W16(dst, (item.Flags & 0x0006) | (w.Item4 ? 0x800 : 0)); W16(dst, item.Method);
                    W16(dst, item.DosTime); W16(dst, item.DosDate); W32(dst, item.Crc);
                    W32(dst, bigSize ? 0xFFFFFFFF : (uint)item.CompressedSize); W32(dst, bigSize ? 0xFFFFFFFF : (uint)item.Size);
                    W16(dst, nameBytes.Length); W16(dst, extra > 0 ? extra + 4 : 0); W16(dst, 0);
                    W16(dst, 0); W16(dst, 0); W32(dst, 0);
                    W32(dst, bigOff ? 0xFFFFFFFF : (uint)w.Item3);
                    dst.Write(nameBytes, 0, nameBytes.Length);
                    if (extra > 0)
                    {
                        W16(dst, 1); W16(dst, extra);
                        if (bigSize) { W64(dst, item.Size); W64(dst, item.CompressedSize); }
                        if (bigOff) W64(dst, w.Item3);
                    }
                }
                long cdEnd = dst.Position, cdSize = cdEnd - cdStart;
                bool zip64 = written.Count >= 0xFFFF || cdStart >= 0xFFFFFFFFL || cdSize >= 0xFFFFFFFFL;
                if (zip64)
                {
                    W32(dst, 0x06064b50); W64(dst, 44); W16(dst, 45); W16(dst, 45); W32(dst, 0); W32(dst, 0);
                    W64(dst, written.Count); W64(dst, written.Count); W64(dst, cdSize); W64(dst, cdStart);
                    W32(dst, 0x07064b50); W32(dst, 0); W64(dst, cdEnd); W32(dst, 1);
                }
                W32(dst, 0x06054b50); W16(dst, 0); W16(dst, 0);
                W16(dst, zip64 ? 0xFFFF : written.Count); W16(dst, zip64 ? 0xFFFF : written.Count);
                W32(dst, zip64 ? 0xFFFFFFFF : (uint)cdSize); W32(dst, zip64 ? 0xFFFFFFFF : (uint)cdStart); W16(dst, 0);
            }
        }

    }

    static class Crc32
    {
        static readonly uint[] Table = MakeTable();

        static uint[] MakeTable()
        {
            var t = new uint[256];
            for (uint i = 0; i < 256; i++)
            {
                uint c = i;
                for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
                t[i] = c;
            }
            return t;
        }

        public static uint OfFile(string path, CancellationToken ct)
        {
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16))
                return OfStream(fs, ct);
        }

        public static uint OfStream(Stream s, CancellationToken ct)
        {
            uint c = 0xFFFFFFFF;
            var buf = new byte[1 << 20];
            int n;
            while ((n = s.Read(buf, 0, buf.Length)) > 0)
            {
                ct.ThrowIfCancellationRequested();
                for (int i = 0; i < n; i++) c = Table[(c ^ buf[i]) & 0xFF] ^ (c >> 8);
            }
            return ~c;
        }

        // Unpacks every file in the zip and checks it has exactly the expected names and CRCs: proof the zip is
        // sound before its source is removed. Returns null when it is, or what's wrong.
        public static string VerifyZip(string path, IEnumerable<Tuple<ZipItem, string>> expected, CancellationToken ct)
        {
            try
            {
                var want = expected.ToDictionary(e => e.Item2, e => e.Item1.Crc, StringComparer.Ordinal);
                int seen = 0;
                using (var zip = ZipFile.OpenRead(path))
                    foreach (var e in zip.Entries)
                    {
                        if (e.FullName.EndsWith("/")) continue;
                        uint crc, expectedCrc;
                        if (!want.TryGetValue(e.FullName, out expectedCrc)) return "\"" + e.FullName + "\" shouldn't be there";
                        using (var s = e.Open()) crc = OfStream(s, ct);
                        if (crc != expectedCrc) return "\"" + e.FullName + "\" doesn't unpack correctly";
                        seen++;
                    }
                return seen == want.Count ? null : "Some files are missing from the new zip";
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { return ex.Message; }
        }
    }

    // ---------------------------------------------------------------- scan results

    enum FileKind
    {
        Ok,          // right game, right name
        Misnamed,    // right game, wrong zip name and/or wrong name inside the zip
        NotZipped,   // right game, but a loose file rather than a zip
        Duplicate,   // a second copy of a game that's already there
        NotPreferred,// one game per title: another version of this title is kept instead
        Excluded,    // a kind of game the user doesn't keep (betas, prototypes...)
        NotInDat,    // not in the DAT at all
        Kept,        // not in the DAT, but kept anyway (aftermarket games, which the Love Pack leaves out)
        Incomplete,  // part of a multi-file game
        Mixed,       // a zip holding more than one game
        CueOutdated, // a disc whose tracks all match, but whose cue sheet is out of date or missing
        Unreadable,  // damaged zip, or couldn't be opened
        Unsupported  // .rar etc.
    }

    class FileResult
    {
        public SystemResult System;
        public string Path;
        public string Name;          // path relative to the system folder
        public long Length;
        public bool IsZip;
        public bool Is7z;
        public bool IsArchive { get { return IsZip || Is7z; } }
        public bool IsFolder;        // a MAME game's folder of CHDs ("sf2049\sf2049.chd"), checked as one set
        public bool LooseChd;        // a CHD on its own in the system folder, not in its game's folder
        public bool InPack;          // a game's folder inside a TOSEC pack (one archive holding a whole DAT)
        public string PackGame;      // that folder's name
        public bool IsSet { get { return IsArchive || IsFolder || InPack; } }   // holds a game's files under the game's name
        public bool CanChange { get { return true; } }
        internal bool CueOnly;       // matched by its tracks; the cue sheet doesn't match
        public bool FixCue;          // the cue sheet will be replaced from a cue sheet pack
        public bool OnIgnoreList;    // kept because it's on your ignore list
        public FileKind Kind;
        public DatGame Game;
        public string Detail = "";
        public List<ZipItem> Items;
        public bool NeedsRebuild;    // the zip must be rewritten (inner names, extra files, or zipping a loose file)
        public string TargetPath;
        internal List<DatGame> Candidates;

        public bool IsUnneeded { get { return Kind == FileKind.Duplicate || Kind == FileKind.NotInDat || Kind == FileKind.NotPreferred || Kind == FileKind.Excluded; } }
        public bool IsFixable { get { return Kind == FileKind.Misnamed || Kind == FileKind.NotZipped; } }
        public bool IsProblem { get { return Kind == FileKind.Incomplete || Kind == FileKind.Mixed || Kind == FileKind.CueOutdated || Kind == FileKind.Unreadable || Kind == FileKind.Unsupported; } }
        public string TargetName { get { return TargetPath == null ? "" : RelativeTo(System.Folder, TargetPath); } }

        public static string RelativeTo(string folder, string path)
        {
            string f = folder.TrimEnd('\\') + "\\";
            return path.StartsWith(f, StringComparison.OrdinalIgnoreCase) ? path.Substring(f.Length) : path;
        }
    }

    class SystemResult
    {
        public string Folder;
        public string FolderName;
        public DatInfo Dat;
        public List<DatInfo> ExtraDats = new List<DatInfo>();   // e.g. the system's Aftermarket DAT, checked together with it
        public string DatText { get { return Dat == null ? "" : string.Join(" + ", new[] { Dat }.Concat(ExtraDats).Select(d => d.Name + " (" + d.Version + ")")); } }
        public string Error;
        public bool IsPack;      // a TOSEC pack: Folder is the archive holding the whole system
        public bool KeepPack;    // fix the pack in place (rather than unpack it into one zip per game)
        public List<FileResult> Files = new List<FileResult>();
        public List<DatGame> Missing = new List<DatGame>();   // includes MIA games
        public Dictionary<DatGame, string> MissingNote = new Dictionary<DatGame, string>();
        public bool OneGame;
        public GameType Excluded;
        public List<string> IgnoredRegions = new List<string>();
        public CueIndex Cues;
        public int Total;
        public int Have;    // games present, counting ones that only need renaming

        public int MissingCount { get { return Missing.Count(g => !g.Mia); } }
        public int MiaCount { get { return Missing.Count(g => g.Mia); } }
        public int Count(Func<FileResult, bool> pick) { return Files.Count(pick); }
    }

    // ---------------------------------------------------------------- scanning

    class ScanOptions
    {
        public bool OneGamePerTitle;
        public string RegionPriority = Preference.DefaultRegions;
        public GameType Exclude = GameType.None;   // kinds of game to leave out
        public HashSet<string> IgnoredRegions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);   // games only from these are left out
        public CueIndex Cues;   // cue sheets from the cue sheet packs, for discs whose cue sheet is out of date
        public Dictionary<string, HashSet<string>> Ignore;   // DAT name -> files to keep anyway (your ignore list)
        public bool KeepPacks = true;   // TOSEC packs: fix inside them, or unpack them into one zip per game
    }

    static class Scanner
    {
        // Reports saved by the app ("!ROM Check report 2026-11-23.txt") are never part of the set.
        public const string ReportPrefix = "!ROM Check report";
        public static bool IsReport(string path)
        {
            string n = Path.GetFileName(path);
            return n.StartsWith(ReportPrefix, StringComparison.OrdinalIgnoreCase) && n.EndsWith(".txt", StringComparison.OrdinalIgnoreCase);
        }

        static readonly HashSet<string> Ignored = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "desktop.ini", "thumbs.db", ".ds_store" };
        static readonly HashSet<string> Archives = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { ".7z", ".rar", ".gz", ".tar", ".tgz", ".bz2", ".xz", ".zst" };

        // A TOSEC pack's name for matching to its DAT: the archive's own name ("Sinclair ZX81 - Games - [P]
        // (TOSEC-v2022-12-21).zip"), or with the folders it's in in front ("Sinclair ZX81\Games - [P] (...).zip").
        public static string PackName(string path, DatIndex index, IDictionary<string, string> manual)
        {
            string name = Path.GetFileNameWithoutExtension(path);
            if (index.Find(name, manual) != null) return name;
            var dir = new DirectoryInfo(Path.GetDirectoryName(path));
            for (int up = 0; up < 3 && dir != null; up++, dir = dir.Parent)
            {
                string candidate = dir.Name + " - " + name;
                if (index.Find(candidate, manual) != null) return candidate;
            }
            return null;
        }

        public static DatInfo PackDat(string path, DatIndex index, IDictionary<string, string> manual)
        {
            string name = PackName(path, index, manual);
            return name == null ? null : index.Find(name, manual);
        }

        public static SystemResult ScanSystem(string folder, DatIndex index, IDictionary<string, string> manual,
                                              ScanOptions options, Action<string, int, int> progress, CancellationToken ct)
        {
            bool pack = File.Exists(folder);   // a TOSEC pack: one archive holding the whole system
            var sys = new SystemResult { Folder = folder, FolderName = Path.GetFileName(folder.TrimEnd('\\')), OneGame = options.OneGamePerTitle, Excluded = options.Exclude, IgnoredRegions = options.IgnoredRegions.OrderBy(r => r).ToList(), Cues = options.Cues };
            if (pack)
            {
                sys.IsPack = true;
                sys.FolderName = PackName(folder, index, manual) ?? Path.GetFileNameWithoutExtension(folder);
            }
            sys.Dat = index.Find(sys.FolderName, manual);
            if (sys.Dat == null) { sys.Error = "No DAT found for this folder"; return sys; }

            DatFile dat;
            try { dat = index.Load(sys.Dat); sys.ExtraDats.AddRange(dat.Extras); }
            catch (Exception ex) { sys.Error = "Couldn't read the DAT: " + ex.Message; return sys; }
            sys.Total = dat.Games.Count;

            if (pack)
            {
                ScanPack(sys, dat, options, progress, ct);
                return sys;
            }

            var paths = Files.Enumerate(folder, "*").Where(p => !Ignored.Contains(Path.GetFileName(p)) && !IsReport(p)).ToList();

            // MAME CHDs live in a folder named after their game; each such folder is checked as one set.
            string root = folder.TrimEnd('\\');
            var chdFolders = paths.Where(p => Chd.IsChd(p) && !string.Equals(Path.GetDirectoryName(p), root, StringComparison.OrdinalIgnoreCase))
                                  .GroupBy(p => Path.GetDirectoryName(p), StringComparer.OrdinalIgnoreCase).ToList();
            var inFolders = new HashSet<string>(chdFolders.SelectMany(g => g), StringComparer.OrdinalIgnoreCase);
            paths = paths.Where(p => !inFolders.Contains(p)).ToList();
            foreach (var g in chdFolders)
            {
                ct.ThrowIfCancellationRequested();
                var f = new FileResult { System = sys, Path = g.Key, Name = FileResult.RelativeTo(folder, g.Key), IsFolder = true, Items = new List<ZipItem>() };
                sys.Files.Add(f);
                try
                {
                    foreach (var p in g.OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
                    {
                        long len = new FileInfo(p).Length;
                        f.Length += len;
                        f.Items.Add(new ZipItem { Name = Path.GetFileName(p), Size = len, Sha1 = Chd.Sha1(p), RawSha1 = Chd.RawSha1(p) });
                    }
                }
                catch (Exception ex)
                {
                    f.Kind = FileKind.Unreadable;
                    f.Detail = ex.Message;
                    continue;
                }
                Classify(f, dat, options);
            }

            int done = 0;
            foreach (var path in paths)
            {
                ct.ThrowIfCancellationRequested();
                if (progress != null && (done % 25 == 0)) progress(sys.FolderName, done, paths.Count);
                done++;
                var f = new FileResult { System = sys, Path = path, Name = FileResult.RelativeTo(folder, path) };
                sys.Files.Add(f);
                string ext = Path.GetExtension(path);
                try
                {
                    f.Length = new FileInfo(path).Length;
                    if (ext.Equals(".zip", StringComparison.OrdinalIgnoreCase))
                    {
                        f.IsZip = true;
                        f.Items = ZipReader.Read(path);
                    }
                    else if (ext.Equals(".7z", StringComparison.OrdinalIgnoreCase) && SevenZip.Exe != null)
                    {
                        f.Is7z = true;
                        f.Items = SevenZip.ListCached(path);
                    }
                    else if (Chd.IsChd(path))
                    {
                        f.LooseChd = true;
                        f.Items = new List<ZipItem> { new ZipItem { Name = Path.GetFileName(path), Size = f.Length, Sha1 = Chd.Sha1(path), RawSha1 = Chd.RawSha1(path) } };
                    }
                    else if (Archives.Contains(ext))
                    {
                        f.Kind = FileKind.Unsupported;
                        f.Detail = ext.Equals(".7z", StringComparison.OrdinalIgnoreCase) ? "Needs 7-Zip installed to check .7z files" : "Only .zip and .7z files can be checked";
                        continue;
                    }
                    else
                    {
                        f.Items = new List<ZipItem> {
                            new ZipItem { Name = Path.GetFileName(path), Size = f.Length, Crc = Crc32.OfFile(path, ct) } };
                    }
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    f.Kind = FileKind.Unreadable;
                    f.Detail = ex.Message;
                    continue;
                }
                Classify(f, dat, options);
            }
            if (progress != null) progress(sys.FolderName, paths.Count, paths.Count);

            Resolve(sys, dat, options);

            // Files on your ignore list are kept, whatever they are.
            ApplyIgnoreList(sys, options);
            return sys;
        }

        // A TOSEC pack: every game is a folder inside the one archive ("2D Labyrinth (19xx)(-)/2D Labyrinth (19xx)(-).p"),
        // so each folder is checked like a zip of its own. Only the archive's index is read, however big it is.
        static void ScanPack(SystemResult sys, DatFile dat, ScanOptions options, Action<string, int, int> progress, CancellationToken ct)
        {
            string path = sys.Folder;
            List<ZipItem> entries;
            try
            {
                if (path.EndsWith(".7z", StringComparison.OrdinalIgnoreCase))
                {
                    if (SevenZip.Exe == null) { sys.Error = "Needs 7-Zip installed to check .7z packs"; return; }
                    entries = SevenZip.ListCached(path);
                }
                else entries = ZipReader.Read(path);
            }
            catch (Exception ex) { sys.Error = "Couldn't read the pack: " + ex.Message; return; }

            var groups = new Dictionary<string, FileResult>(StringComparer.Ordinal);
            var order = new List<FileResult>();
            foreach (var e in entries)
            {
                string name = e.Name.Replace('\\', '/');
                if (name.EndsWith("/")) continue;   // a folder entry
                int slash = name.IndexOf('/');
                string game = slash > 0 ? name.Substring(0, slash) : Path.GetFileNameWithoutExtension(name);
                string inside = slash > 0 ? name.Substring(slash + 1) : name;
                FileResult f;
                if (!groups.TryGetValue(game, out f))
                {
                    f = new FileResult { System = sys, Path = Path.Combine(path, game), Name = game, InPack = true, PackGame = game, Items = new List<ZipItem>() };
                    groups[game] = f;
                    order.Add(f);
                }
                f.Items.Add(new ZipItem { Name = inside, FullName = e.Name, Size = e.Size, Crc = e.Crc, Index = e.Index });
                f.Length += e.Size;
            }
            for (int i = 0; i < order.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                if (progress != null && i % 500 == 0) progress(sys.FolderName, i, order.Count);
                sys.Files.Add(order[i]);
                Classify(order[i], dat, options);
            }
            if (progress != null) progress(sys.FolderName, order.Count, order.Count);
            Resolve(sys, dat, options);
            ApplyIgnoreList(sys, options);

            // Packs can be kept (games are fixed inside them) or unpacked into one zip per game. When unpacking,
            // every game in the pack is listed under Wrong Name, as it isn't in its own zip yet.
            sys.KeepPack = options.KeepPacks;
            if (!options.KeepPacks && path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            {
                string folder = Packs.UnpackFolder(sys);
                foreach (var f in sys.Files.Where(f => f.Game != null && (f.Kind == FileKind.Ok || f.Kind == FileKind.Misnamed)))
                {
                    f.TargetPath = Path.Combine(folder, f.Game.Name + ".zip");
                    if (f.Kind == FileKind.Ok) { f.Kind = FileKind.NotZipped; f.Detail = "Will be unpacked into its own zip"; }
                    else f.Detail = (f.Detail.Length > 0 ? f.Detail + "; " : "") + "unpacked into its own zip";
                }
            }
        }

        static void ApplyIgnoreList(SystemResult sys, ScanOptions options)
        {
            HashSet<string> ignore;
            if (options.Ignore != null && options.Ignore.TryGetValue(sys.Dat.Name, out ignore))
                foreach (var f in sys.Files)
                    if ((f.IsUnneeded || f.IsProblem) && ignore.Contains(f.Name))
                    {
                        f.Kind = FileKind.Kept;
                        f.OnIgnoreList = true;
                        f.Detail = "On your ignore list (kept)";
                    }
        }

        public static bool IsCue(DatRom r) { return r.Name.EndsWith(".cue", StringComparison.OrdinalIgnoreCase); }

        // NKit discs ("Game.nkit.iso", "Game.nkit.gcz") keep the original disc's CRC but are smaller,
        // so they're matched to the DAT's "Game.iso" by CRC alone, and keep their NKit name.
        public static bool SameData(ZipItem i, DatRom r)
        {
            if (i.Sha1 != null || r.Sha1 != null) return r.Sha1 != null && (i.Sha1 == r.Sha1 || i.RawSha1 == r.Sha1);
            return i.Crc == r.Crc && (i.Size == r.Size || i.IsNKit);
        }

        public static string ExpectedName(DatRom r, ZipItem i)
        {
            if (i != null && i.IsNKit && r.Name.EndsWith(".iso", StringComparison.OrdinalIgnoreCase))
                return r.Name.Substring(0, r.Name.Length - 4) + (i.Name.EndsWith(".nkit.gcz", StringComparison.OrdinalIgnoreCase) ? ".nkit.gcz" : ".nkit.iso");
            return r.Name;
        }

        // Every DAT key a file can count as: a CHD can match by either of its SHA1s.
        static IEnumerable<string> KeysOf(ZipItem i, DatFile dat)
        {
            yield return KeyOf(i, dat);
            if (i.RawSha1 != null && i.RawSha1 != i.Sha1) yield return RomKey.OfSha1(i.RawSha1);
        }

        // The DAT key a file counts as: its own size and CRC, or for an NKit disc, the original's.
        static string KeyOf(ZipItem i, DatFile dat)
        {
            List<DatRom> roms;
            if (i.IsNKit && dat.ByCrc.TryGetValue(i.Crc, out roms))
            {
                var isos = roms.Where(r => r.Name.EndsWith(".iso", StringComparison.OrdinalIgnoreCase)).Select(r => r.Key).Distinct().ToList();
                if (isos.Count == 1) return isos[0];
            }
            return i.Key;
        }

        static void Classify(FileResult f, DatFile dat, ScanOptions options)
        {
            var keys = new HashSet<string>(f.Items.SelectMany(i => KeysOf(i, dat)));
            string baseName = BaseName(f);

            // Quick path: the file holds the whole of the game it's named after (almost every file in a tidy set,
            // and for MAME, where thousands of machines share device ROMs, it saves checking all of them).
            DatGame own = null;
            if (dat.ByName.TryGetValue(baseName, out own) && f.IsSet && own.Name == baseName && own.Roms.Count > 0 && own.Roms.All(r => keys.Contains(r.Key)))
            {
                f.Candidates = new List<DatGame> { own };
                return;
            }

            var touched = new List<DatGame>();
            var seen = new HashSet<DatGame>();
            foreach (var k in keys)
            {
                List<DatRom> roms;
                if (dat.ByKey.TryGetValue(k, out roms))
                    foreach (var r in roms) if (seen.Add(r.Game)) touched.Add(r.Game);
            }
            if (touched.Count == 0)
            {
                f.Kind = FileKind.NotInDat;
                DatGame same;
                f.Detail = dat.ByName.TryGetValue(baseName, out same)
                    ? "Has the name of a ROM in the DAT, but the contents don't match (bad or outdated dump)"
                    : (f.Items.Count == 0 ? "Empty zip" : "Not in the DAT");
                // Only when the DAT has no aftermarket games at all: then they can only be recognised by name.
                if (!dat.HasAftermarket && (options.Exclude & GameType.Aftermarket) == 0 && f.Items.Count > 0 && baseName.IndexOf("(Aftermarket)", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    f.Kind = FileKind.Kept;
                    f.Detail = "Aftermarket ROM, not in this DAT (kept)";
                }
                return;
            }
            var full = touched.Where(g => g.Roms.All(r => keys.Contains(r.Key))).ToList();

            // A MAME game holds copies of its BIOS's and devices' ROMs. When it's out of date (the DAT now has
            // more or different ROMs for it), it still holds the whole BIOS, but it isn't a copy of the BIOS: it's
            // an incomplete copy of itself. So when every whole match is just part of the game the file is named
            // after, the file counts as that game, incomplete.
            if (own != null && seen.Contains(own) && own.Name == baseName && full.Count > 0)
            {
                var ownKeys = new HashSet<string>(own.Roms.Select(r => r.Key));
                if (full.All(g => g != own && g.Roms.All(r => ownKeys.Contains(r.Key))))
                {
                    f.Kind = FileKind.Incomplete;
                    f.Detail = MissingParts(own, keys, baseName);
                    return;
                }
            }
            if (full.Count == 0)
            {
                // A disc's cue sheet lists its track names, so it changes whenever the disc is renamed.
                // When every track matches, it's still the right disc; only the cue sheet needs replacing.
                full = touched.Where(g => g.Roms.Any(IsCue) && g.Roms.Any(r => !IsCue(r)) &&
                                          g.Roms.Where(r => !IsCue(r)).All(r => keys.Contains(r.Key))).ToList();
                if (full.Count > 0) f.CueOnly = true;
            }
            if (full.Count == 0)
            {
                // Name the game the file is named after, if it's one of those it partly holds.
                var part = own != null && seen.Contains(own) ? own : touched[0];
                f.Kind = FileKind.Incomplete;
                f.Detail = MissingParts(part, keys, baseName);
                return;
            }
            // Prefer the game whose name the file already has.
            f.Candidates = full.OrderBy(g => g.Name == baseName ? 0 : string.Equals(g.Name, baseName, StringComparison.OrdinalIgnoreCase) ? 1 : 2)
                               .ThenBy(g => g.Name, StringComparer.Ordinal).ToList();
        }

        // For a file holding only part of a game: the game's files it doesn't hold (missing, or there with
        // different contents), e.g. "Missing file - 720-eeprom.bin". The game is named too when it isn't the one
        // the file is named after. Long lists are cut short.
        static string MissingParts(DatGame g, HashSet<string> keys, string fileName)
        {
            var names = g.Roms.Where(r => !keys.Contains(r.Key)).Select(r => r.Name).Distinct().ToList();
            string of = g.Name == fileName ? "" : " for \"" + g.Name + "\"";
            if (names.Count == 0) return "Part of \"" + g.Name + "\"";
            const int show = 5;
            return (names.Count == 1 ? "Missing file" : "Missing files") + of + " - " + string.Join(", ", names.Take(show)) +
                   (names.Count > show ? " and " + (names.Count - show) + " more" : "");
        }

        static string BaseName(FileResult f)
        {
            if (f.InPack) return f.PackGame;
            return f.IsFolder ? Path.GetFileName(f.Path.TrimEnd('\\')) : Path.GetFileNameWithoutExtension(f.Path);
        }

        // True when the zip holds exactly the game's files, with the DAT's names.
        public static bool ItemsExactly(List<ZipItem> items, DatGame g)
        {
            if (items.Count != g.Roms.Count) return false;
            foreach (var r in g.Roms)
                if (!items.Any(i => SameData(i, r) && i.Name == ExpectedName(r, i))) return false;
            return true;
        }

        static void Resolve(SystemResult sys, DatFile dat, ScanOptions options)
        {
            var owner = new Dictionary<DatGame, FileResult>();
            var pending = sys.Files.Where(f => f.Candidates != null).ToList();

            // Pass 1: files that are already exactly right.
            foreach (var f in pending)
            {
                if (!f.IsSet) continue;
                string baseName = BaseName(f);
                var g = f.Candidates.FirstOrDefault(c => c.Name == baseName && ItemsExactly(f.Items, c));
                if (g == null) continue;
                FileResult first;
                if (owner.TryGetValue(g, out first)) { f.Kind = FileKind.Duplicate; f.Detail = "Another copy of \"" + first.Name + "\""; }
                else { owner[g] = f; f.Kind = FileKind.Ok; f.Game = g; }
            }

            // Pass 2: everything else that holds a whole game. Zips first, and ones whose name is nearly right.
            var rest = pending.Where(f => f.Game == null && f.Kind != FileKind.Duplicate)
                              .OrderBy(f => f.IsSet ? 0 : 1)
                              .ThenBy(f => string.Equals(f.Candidates[0].Name, BaseName(f), StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                              .ThenBy(f => f.Name, StringComparer.OrdinalIgnoreCase).ToList();
            foreach (var f in rest)
            {
                var g = f.Candidates.FirstOrDefault(c => !owner.ContainsKey(c));
                if (g == null)
                {
                    f.Kind = FileKind.Duplicate;
                    f.Detail = "Another copy of \"" + owner[f.Candidates[0]].Name + "\"";
                    continue;
                }
                owner[g] = f;
                f.Game = g;
                if (f.CueOnly)
                {
                    bool hasCue = f.Items.Any(i => i.Name.EndsWith(".cue", StringComparison.OrdinalIgnoreCase));
                    bool renamed = !string.Equals(BaseName(f), g.Name, StringComparison.Ordinal);
                    bool inPacks = options.Cues != null && g.Roms.Where(IsCue).All(r => options.Cues.Has(r.Key));
                    if (!inPacks || !f.IsArchive)
                    {
                        f.Kind = FileKind.CueOutdated;
                        f.Detail = "All tracks match" + (renamed ? " \"" + g.Name + "\"" : "") + ", but the cue sheet is " +
                                   (hasCue ? "out of date" : "missing") + (renamed ? " and the name has changed" : "") +
                                   (inPacks ? "" : ". The right cue sheet isn't in your cue sheet packs (get the latest pack from redump.org)");
                        continue;
                    }
                    // The right cue sheet is in a pack: this disc can be fixed like any other wrong name.
                    f.FixCue = true;
                    f.Kind = FileKind.Misnamed;
                    f.TargetPath = Path.Combine(Path.GetDirectoryName(f.Path), g.Name + (f.Is7z ? ".7z" : ".zip"));
                    f.NeedsRebuild = true;
                    int tracksRenamed = g.Roms.Where(r => !IsCue(r)).Count(r => !f.Items.Any(i => SameData(i, r) && i.Name == ExpectedName(r, i)));
                    var bits = new List<string>();
                    if (renamed) bits.Add((f.Is7z ? ".7z file" : "Zip") + " renamed");
                    if (tracksRenamed > 0) bits.Add(tracksRenamed + " track" + (tracksRenamed == 1 ? "" : "s") + " renamed");
                    bits.Add("cue sheet " + (hasCue ? "replaced" : "added") + " from your cue sheet pack");
                    f.Detail = string.Join("; ", bits);
                    if (g.BadDump) f.Detail += " (the DAT marks this as a bad dump)";
                    continue;
                }
                var gameKeys = new HashSet<string>(g.Roms.Select(r => r.Key));
                var others = f.Items.Where(i => !KeysOf(i, dat).Any(gameKeys.Contains) && KeysOf(i, dat).Any(dat.ByKey.ContainsKey)).ToList();
                if (others.Count > 0)
                {
                    f.Kind = FileKind.Mixed;
                    f.Detail = "Holds more than one ROM (" + g.Name + " and " + others.Count + " more file(s)). Not changed automatically";
                    continue;
                }
                if (f.InPack)
                {
                    f.Kind = FileKind.Misnamed;
                    f.TargetPath = Path.Combine(sys.Folder, g.Name);
                    f.NeedsRebuild = false;
                    var packParts = new List<string>();
                    if (!string.Equals(f.PackGame, g.Name, StringComparison.Ordinal)) packParts.Add("Folder in the pack renamed");
                    int wrongInside = g.Roms.Count(r => !f.Items.Any(i => SameData(i, r) && i.Name == r.Name));
                    if (wrongInside > 0) packParts.Add(wrongInside == 1 ? "File inside renamed" : wrongInside + " files inside renamed");
                    int extraInside = f.Items.Count - g.Roms.Count;
                    if (extraInside > 0) packParts.Add(extraInside + " extra file" + (extraInside == 1 ? "" : "s"));
                    f.Detail = string.Join("; ", packParts);
                    if (g.BadDump) f.Detail += " (the DAT marks this as a bad dump)";
                    continue;
                }
                if (f.IsFolder || f.LooseChd)
                {
                    // CHDs are never zipped: the game's folder is renamed (or made), and the CHDs in it.
                    f.Kind = FileKind.Misnamed;
                    f.TargetPath = f.IsFolder ? Path.Combine(Path.GetDirectoryName(f.Path.TrimEnd('\\')), g.Name)
                                              : Path.Combine(Path.GetDirectoryName(f.Path), g.Name);
                    f.NeedsRebuild = false;
                    var chdParts = new List<string>();
                    if (f.LooseChd) chdParts.Add("Will be moved into a folder called \"" + g.Name + "\"");
                    else if (!string.Equals(BaseName(f), g.Name, StringComparison.Ordinal)) chdParts.Add("Folder renamed");
                    int wrong = g.Roms.Count(r => !f.Items.Any(i => SameData(i, r) && i.Name == r.Name));
                    if (wrong > 0) chdParts.Add(wrong == 1 && g.Roms.Count == 1 ? "CHD renamed" : wrong + " CHD" + (wrong == 1 ? "" : "s") + " renamed");
                    int extraChds = f.Items.Count(i => !g.Roms.Any(r => SameData(i, r)));
                    if (extraChds > 0) chdParts.Add(extraChds + " CHD" + (extraChds == 1 ? "" : "s") + " not in the DAT deleted from the folder");
                    f.Detail = string.Join("; ", chdParts);
                    if (g.BadDump) f.Detail += " (the DAT marks this as a bad dump)";
                    continue;
                }
                f.Kind = f.IsArchive ? FileKind.Misnamed : FileKind.NotZipped;
                f.TargetPath = Path.Combine(Path.GetDirectoryName(f.Path), g.Name + (f.Is7z ? ".7z" : ".zip"));
                f.NeedsRebuild = !f.IsArchive || !ItemsExactly(f.Items, g);
                if (!f.IsArchive) f.Detail = "Will be zipped";
                else
                {
                    string what = f.Is7z ? ".7z file" : "Zip";
                    bool zipName = !string.Equals(f.TargetPath, f.Path, StringComparison.Ordinal);
                    int extra = f.Items.Count - g.Roms.Count;
                    bool innerNames = g.Roms.Any(r => !f.Items.Any(i => SameData(i, r) && i.Name == ExpectedName(r, i)));
                    var parts = new List<string>();
                    if (zipName && innerNames) parts.Add(what + " and the file inside renamed");
                    else if (zipName) parts.Add(what + " renamed");
                    else if (innerNames) parts.Add("File inside the " + what.ToLower() + " renamed");
                    if (extra > 0) parts.Add(extra + " extra file" + (extra == 1 ? "" : "s") + " removed from the " + what.ToLower());
                    f.Detail = string.Join("; ", parts);
                }
                if (g.BadDump) f.Detail += " (the DAT marks this as a bad dump)";
            }

            // Leave out the kinds of game, and the regions, the user doesn't keep: they're unneeded,
            // and don't count as missing.
            Func<DatGame, string> leftOut = g =>
            {
                var why = new List<string>();
                var types = g.Type & options.Exclude;
                if (types != GameType.None) why.Add(GameTypes.Describe(types));
                if (options.IgnoredRegions.Count > 0)
                {
                    var regions = Preference.RegionsOf(g.Name);
                    if (regions.Count > 0 && regions.All(r => options.IgnoredRegions.Contains(r)))
                        why.Add("region: " + string.Join(", ", regions));
                }
                return why.Count == 0 ? null : string.Join("; ", why);
            };
            var reasons = dat.Games.ToDictionary(g => g, leftOut);
            var kept = dat.Games.Where(g => reasons[g] == null).ToList();
            foreach (var kv in owner.ToList())
            {
                string why = reasons[kv.Key];
                if (why == null || kv.Value.Kind == FileKind.Mixed) continue;
                var f = kv.Value;
                f.Kind = FileKind.Excluded;
                f.TargetPath = null;
                f.Detail = "Left out (" + why + ")";
                owner.Remove(kv.Key);
            }
            sys.Total = kept.Count;

            if (!options.OneGamePerTitle)
            {
                foreach (var g in kept) if (!owner.ContainsKey(g)) sys.Missing.Add(g);
                sys.Have = owner.Count;
                return;
            }

            // One game per title: keep the preferred version. If it isn't here yet, keep the best version
            // that is here until it is, and mark the rest as unneeded.
            var pref = new Preference(options.RegionPriority);
            var groups = DatFile.Groups(kept);
            sys.Total = groups.Count;
            sys.Have = 0;
            foreach (var group in groups)
            {
                var ranked = pref.Rank(group);
                var wanted = ranked[0];
                var owned = ranked.Where(g => owner.ContainsKey(g) && owner[g].Kind != FileKind.Mixed).ToList();
                var keep = owned.FirstOrDefault();
                if (owner.ContainsKey(wanted)) { sys.Have++; keep = wanted; }
                else
                {
                    sys.Missing.Add(wanted);
                    if (keep != null)
                    {
                        sys.MissingNote[wanted] = "You have \"" + keep.Name + "\" for now";
                        var kf = owner[keep];
                        kf.Detail = (kf.Kind == FileKind.Ok ? "" : kf.Detail + "; ") + "Kept until \"" + wanted.Name + "\" is added";
                    }
                }
                foreach (var g in owned)
                {
                    if (g == keep) continue;
                    var f = owner[g];
                    f.Kind = FileKind.NotPreferred;
                    f.TargetPath = null;
                    f.Detail = "Another version of \"" + keep.Name + "\", which is kept instead";
                }
            }
        }
    }

    // ---------------------------------------------------------------- fixing

    // Sends files and folders to the Recycle Bin, using Windows' own delete. When something can't be recycled
    // (too big for the Recycle Bin, or on a network drive), Windows asks before deleting it permanently.
    static class RecycleBin
    {
        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential, CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        struct SHFILEOPSTRUCT
        {
            public IntPtr hwnd;
            public uint wFunc;
            public string pFrom, pTo;
            public ushort fFlags;
            [System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)] public bool fAnyOperationsAborted;
            public IntPtr hNameMappings;
            public string lpszProgressTitle;
        }

        [System.Runtime.InteropServices.DllImport("shell32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        static extern int SHFileOperation(ref SHFILEOPSTRUCT op);

        const uint FO_DELETE = 3;
        const ushort FOF_SILENT = 0x4, FOF_NOCONFIRMATION = 0x10, FOF_ALLOWUNDO = 0x40, FOF_NOERRORUI = 0x400, FOF_WANTNUKEWARNING = 0x4000;

        // Returns null when it's gone to the Recycle Bin (or was permanently deleted after you agreed), or what went wrong.
        public static string Send(string path)
        {
            string result = null;
            // The shell's warning needs a UI thread of its own.
            var t = new Thread(() =>
            {
                var op = new SHFILEOPSTRUCT
                {
                    wFunc = FO_DELETE,
                    pFrom = Path.GetFullPath(path) + "\0\0",
                    fFlags = FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_SILENT | FOF_NOERRORUI | FOF_WANTNUKEWARNING
                };
                int code = SHFileOperation(ref op);
                if (op.fAnyOperationsAborted) result = "Kept (you chose not to delete it permanently)";
                else if (code != 0) result = "Couldn't move it to the Recycle Bin (error " + code + ")";
                else if (File.Exists(path) || Directory.Exists(path)) result = "It's still there";
            });
            t.SetApartmentState(ApartmentState.STA);
            t.Start();
            t.Join();
            return result;
        }
    }

    static class Fixer
    {
        // Deletes send files to the Recycle Bin (Options > Delete to the Recycle Bin), rather than deleting them outright.
        public static bool UseRecycleBin = true;

        // Gives the file its DAT name (and the file inside the zip). Returns null on success, or what went wrong.
        public static string Fix(FileResult f)
        {
            if (!f.IsFixable || f.Game == null) return "Nothing to fix";
            if (f.InPack) return "Games in a TOSEC pack are changed a whole pack at a time";
            if (f.IsFolder || f.LooseChd) return FixChd(f);
            string target = f.TargetPath;
            bool sameFile = string.Equals(Path.GetFullPath(target), Path.GetFullPath(f.Path), StringComparison.OrdinalIgnoreCase);
            if (!sameFile && File.Exists(target))
                return "\"" + Path.GetFileName(target) + "\" already exists. Delete unneeded files first, then try again";
            if (!File.Exists(f.Path)) return "The file isn't there any more";

            if (!f.NeedsRebuild)
            {
                try
                {
                    if (sameFile)
                    {
                        // Only the letter case differs: Windows needs a two-step rename.
                        string step = f.Path + ".romcheck-rename";
                        File.Move(f.Path, step);
                        File.Move(step, target);
                    }
                    else File.Move(f.Path, target);
                    Log.Write("Renamed \"" + f.Path + "\" to \"" + Path.GetFileName(target) + "\"");
                    return null;
                }
                catch (Exception ex) { return ex.Message; }
            }

            // Zips and .7z files are fixed in place with 7-Zip: nothing is recompressed, which matters for big discs.
            // 7-Zip won't edit some archives in place (for example ones with extra data after their end);
            // those are rebuilt instead, from what's in them now.
            if (f.IsArchive && SevenZip.Exe != null)
            {
                string err = FixInPlace(f, target, sameFile);
                if (err == null || !File.Exists(f.Path) || (!sameFile && File.Exists(target))) return err;
                try { f.Items = f.Is7z ? SevenZip.List(f.Path) : ZipReader.Read(f.Path); }
                catch (Exception ex) { return ex.Message; }
                return f.Is7z ? Rebuild7z(f, target) : RebuildZip(f, target);
            }
            if (f.Is7z) return "Needs 7-Zip installed to fix this";
            return RebuildZip(f, target);
        }

        // Writes a new zip with the ROM's files under their DAT names (taking a cue sheet from a pack if needed),
        // checks it against the DAT, and only then replaces the original.
        static string RebuildZip(FileResult f, string target)
        {
            string tmp = Path.Combine(Path.GetDirectoryName(target), f.Game.Name + ".romcheck-tmp");
            try
            {
                if (File.Exists(tmp)) File.Delete(tmp);
                using (var fs = new FileStream(tmp, FileMode.CreateNew, FileAccess.ReadWrite))
                using (var dst = new ZipArchive(fs, ZipArchiveMode.Create))
                {
                    if (f.IsZip)
                    {
                        using (var src = ZipFile.OpenRead(f.Path))
                        {
                            foreach (var rom in f.Game.Roms)
                            {
                                var item = f.Items.FirstOrDefault(i => Scanner.SameData(i, rom));
                                if (item == null && f.FixCue && Scanner.IsCue(rom) && f.System.Cues != null)
                                {
                                    using (var cue = f.System.Cues.Open(rom.Key)) Add(dst, rom.Name, cue, DateTimeOffset.Now);
                                    continue;
                                }
                                if (item == null) throw new InvalidDataException("Part of \"" + f.Game.Name + "\" isn't in the file");
                                var se = src.Entries[item.Index];
                                using (var input = se.Open())
                                    Add(dst, Scanner.ExpectedName(rom, item), input, se.LastWriteTime);
                            }
                        }
                    }
                    else
                    {
                        using (var input = File.OpenRead(f.Path))
                            Add(dst, Scanner.ExpectedName(f.Game.Roms[0], f.Items[0]), input, File.GetLastWriteTime(f.Path));
                    }
                }

                var check = ZipReader.Read(tmp);
                if (!Scanner.ItemsExactly(check, f.Game))
                    throw new InvalidDataException("The rebuilt zip didn't match the DAT, so the original was left alone (it may be damaged)");

                File.Delete(f.Path);
                File.Move(tmp, target);
                Log.Write((f.IsZip ? "Rebuilt \"" : "Zipped \"") + f.Path + "\" as \"" + Path.GetFileName(target) + "\"");
                return null;
            }
            catch (Exception ex)
            {
                try { if (File.Exists(tmp) && File.Exists(f.Path)) File.Delete(tmp); } catch { }
                return ex.Message;
            }
        }

        // For a .7z that 7-Zip won't edit in place: unpacks it into a work folder beside it, puts the files under
        // their DAT names (adding a cue sheet from a pack if needed), packs a new .7z, checks it against the DAT,
        // and only then replaces the original. Slower than editing in place, as everything is recompressed.
        static string Rebuild7z(FileResult f, string target)
        {
            string work = Path.Combine(Path.GetDirectoryName(f.Path), ".romcheck-" + Guid.NewGuid().ToString("N"));
            string tmp = target + ".romcheck-tmp";
            try
            {
                SevenZip.Extract(f.Path, work);
                string outDir = Path.Combine(work, ".out");
                Directory.CreateDirectory(outDir);
                var names = new List<string>();
                var used = new HashSet<ZipItem>();
                foreach (var rom in f.Game.Roms)
                {
                    var item = f.Items.FirstOrDefault(i => Scanner.SameData(i, rom) && !used.Contains(i));
                    string name = Scanner.ExpectedName(rom, item);
                    string dest = Path.Combine(outDir, name.Replace('/', '\\'));
                    if (item != null) { used.Add(item); File.Move(Path.Combine(work, item.Name.Replace('/', '\\')), dest); }
                    else if (f.FixCue && Scanner.IsCue(rom) && f.System.Cues != null) f.System.Cues.Extract(rom.Key, dest);
                    else return "Part of \"" + f.Game.Name + "\" isn't in the file";
                    names.Add(name);
                }
                if (File.Exists(tmp)) File.Delete(tmp);
                SevenZip.Add(tmp, outDir, names, true);
                if (!Scanner.ItemsExactly(SevenZip.List(tmp), f.Game))
                    throw new InvalidDataException("The repacked .7z didn't match the DAT, so the original was left alone");
                File.Delete(f.Path);
                File.Move(tmp, target);
                Log.Write("Repacked \"" + f.Path + "\" as \"" + Path.GetFileName(target) + "\" (7-Zip couldn't edit it in place)");
                return null;
            }
            catch (Exception ex)
            {
                try { if (File.Exists(tmp) && File.Exists(f.Path)) File.Delete(tmp); } catch { }
                return ex.Message;
            }
            finally
            {
                try { if (Directory.Exists(work)) Directory.Delete(work, true); } catch { }
            }
        }

        // Fixes a zip or .7z in place with 7-Zip: removes files that don't belong (such as an old cue sheet),
        // renames the files inside, adds the right cue sheet from a cue sheet pack if needed, checks the result
        // against the DAT, and then renames the archive itself. 7-Zip copies the packed data across without
        // recompressing it, so even multi-GB discs are quick.
        static string FixInPlace(FileResult f, string target, bool sameFile)
        {
            string tmpDir = null;
            try
            {
                var used = new HashSet<ZipItem>();
                var renames = new List<KeyValuePair<string, string>>();
                var adds = new List<DatRom>();
                foreach (var rom in f.Game.Roms)
                {
                    var item = f.Items.FirstOrDefault(i => Scanner.SameData(i, rom) && !used.Contains(i));
                    if (item == null)
                    {
                        if (f.FixCue && Scanner.IsCue(rom)) { adds.Add(rom); continue; }
                        return "Part of \"" + f.Game.Name + "\" isn't in the file";
                    }
                    used.Add(item);
                    string want = Scanner.ExpectedName(rom, item);
                    if (item.Name != want) renames.Add(new KeyValuePair<string, string>(item.Name, want));
                }
                var extras = f.Items.Where(i => !used.Contains(i)).Select(i => i.Name).ToList();

                if (extras.Count > 0) SevenZip.Delete(f.Path, extras);
                if (renames.Count > 0) SevenZip.Rename(f.Path, renames);
                if (adds.Count > 0)
                {
                    var cues = f.System.Cues;
                    if (cues == null) return "No cue sheet packs were found";
                    tmpDir = Path.Combine(Path.GetTempPath(), "ROM-Check-O-MATIC", Guid.NewGuid().ToString("N"));
                    Directory.CreateDirectory(tmpDir);
                    foreach (var rom in adds) cues.Extract(rom.Key, Path.Combine(tmpDir, rom.Name));
                    SevenZip.Add(f.Path, tmpDir, adds.Select(r => r.Name), f.Is7z);
                }

                var now = f.Is7z ? SevenZip.List(f.Path) : ZipReader.Read(f.Path);
                if (!Scanner.ItemsExactly(now, f.Game))
                    throw new InvalidDataException("After fixing, the " + (f.Is7z ? ".7z" : "zip") + " didn't match the DAT. Check it in 7-Zip");
                if (!string.Equals(f.Path, target, StringComparison.Ordinal))
                {
                    if (sameFile) { string step = f.Path + ".romcheck-rename"; File.Move(f.Path, step); File.Move(step, target); }
                    else File.Move(f.Path, target);
                }
                Log.Write("Fixed \"" + f.Path + "\" as \"" + Path.GetFileName(target) + "\" (inside: renamed " + renames.Count +
                          ", removed " + extras.Count + ", added " + adds.Count + " from a cue sheet pack)");
                return null;
            }
            catch (Exception ex) { return ex.Message; }
            finally
            {
                if (tmpDir != null) try { Directory.Delete(tmpDir, true); } catch { }
            }
        }

        static void Add(ZipArchive zip, string name, Stream input, DateTimeOffset time)
        {
            var e = zip.CreateEntry(name, CompressionLevel.Optimal);
            try { e.LastWriteTime = time; } catch { }
            using (var output = e.Open()) input.CopyTo(output, 1 << 20);
        }

        // Permanently deletes the file. Returns null on success, or what went wrong.
        // MAME CHDs: renames the game's folder (or makes it, for a CHD on its own), then the CHDs in it, checking
        // each one's SHA1 against the DAT. CHDs in the folder that aren't in the DAT are deleted.
        static string FixChd(FileResult f)
        {
            string target = f.TargetPath;
            try
            {
                if (f.LooseChd)
                {
                    if (!File.Exists(f.Path)) return "The file isn't there any more";
                    var rom = f.Game.Roms.FirstOrDefault(r => Scanner.SameData(f.Items[0], r));
                    if (rom == null) return "Nothing to fix";
                    string to = Path.Combine(target, rom.Name);
                    if (File.Exists(to)) return "\"" + Path.GetFileName(target) + "\\" + rom.Name + "\" already exists. Delete unneeded files first, then try again";
                    Directory.CreateDirectory(target);
                    File.Move(f.Path, to);
                    Log.Write("Moved \"" + f.Path + "\" to \"" + to + "\"");
                    return Chd.Matches(to, rom) ? null : "The moved CHD doesn't match the DAT";
                }

                string from = f.Path.TrimEnd('\\');
                if (!Directory.Exists(from)) return "The folder isn't there any more";
                if (!string.Equals(from, target, StringComparison.Ordinal))
                {
                    if (string.Equals(from, target, StringComparison.OrdinalIgnoreCase))
                    {
                        string step = from + ".romcheck-rename";
                        Directory.Move(from, step);
                        Directory.Move(step, target);
                    }
                    else if (Directory.Exists(target) || File.Exists(target))
                        return "\"" + Path.GetFileName(target) + "\" already exists. Delete unneeded files first, then try again";
                    else Directory.Move(from, target);
                    Log.Write("Renamed folder \"" + from + "\" to \"" + Path.GetFileName(target) + "\"");
                }

                // Rename the CHDs inside (through a temporary name, so two can swap names), then delete any extras.
                var moves = new List<Tuple<string, string>>();
                foreach (var item in f.Items)
                {
                    var rom = f.Game.Roms.FirstOrDefault(r => Scanner.SameData(item, r));
                    string path = Path.Combine(target, item.Name);
                    if (rom == null)
                    {
                        File.SetAttributes(path, FileAttributes.Normal);
                        File.Delete(path);
                        Log.Write("Deleted \"" + path + "\" (CHD not in the DAT, in \"" + f.Game.Name + "\")");
                    }
                    else if (item.Name != rom.Name) moves.Add(Tuple.Create(path, Path.Combine(target, rom.Name)));
                }
                foreach (var m in moves) { File.Move(m.Item1, m.Item1 + ".romcheck-rename"); }
                foreach (var m in moves)
                {
                    if (File.Exists(m.Item2)) return "\"" + Path.GetFileName(m.Item2) + "\" already exists in the folder";
                    File.Move(m.Item1 + ".romcheck-rename", m.Item2);
                    Log.Write("Renamed \"" + m.Item1 + "\" to \"" + Path.GetFileName(m.Item2) + "\"");
                }
                foreach (var rom in f.Game.Roms)
                {
                    string path = Path.Combine(target, rom.Name);
                    if (!File.Exists(path) || !Chd.Matches(path, rom)) return "\"" + rom.Name + "\" doesn't match the DAT after renaming";
                }
                return null;
            }
            catch (Exception ex) { return ex.Message; }
        }

        public static string Delete(FileResult f)
        {
            if (f.InPack) return "Games in a TOSEC pack are changed a whole pack at a time";
            return DeleteFile(f);
        }

        static string DeleteFile(FileResult f)
        {
            try
            {
                if (UseRecycleBin && (f.IsFolder ? Directory.Exists(f.Path) : File.Exists(f.Path)))
                {
                    string err = RecycleBin.Send(f.Path);
                    if (err == null) Log.Write("Moved to the Recycle Bin: \"" + f.Path + "\" (" + f.Detail + ")");
                    return err;
                }
                if (f.IsFolder)
                {
                    // A MAME game's folder of CHDs.
                    if (!Directory.Exists(f.Path)) return null;
                    foreach (var p in Directory.GetFiles(f.Path, "*", SearchOption.AllDirectories)) File.SetAttributes(p, FileAttributes.Normal);
                    Directory.Delete(f.Path, true);
                    Log.Write("Deleted folder \"" + f.Path + "\" (" + f.Detail + ")");
                    return null;
                }
                if (!File.Exists(f.Path)) return null;
                var attr = File.GetAttributes(f.Path);
                if ((attr & FileAttributes.ReadOnly) != 0) File.SetAttributes(f.Path, attr & ~FileAttributes.ReadOnly);
                File.Delete(f.Path);
                Log.Write("Deleted \"" + f.Path + "\" (" + f.Detail + ")");
                return null;
            }
            catch (Exception ex) { return ex.Message; }
        }
    }

    // TOSEC packs (one zip holding a whole DAT, a folder per game). Both ways of fixing them copy the games'
    // data as it is, check the result by unpacking every file, and only then replace or remove the pack.
    static class Packs
    {
        // Where a pack is unpacked to: a folder beside it named after the DAT, e.g.
        // "Commodore Amiga - Games - [ADF] (2025-01-30)".
        public static string UnpackFolder(SystemResult sys)
        {
            string name = sys.Dat.Name + (string.IsNullOrEmpty(sys.Dat.Version) ? "" : " (" + sys.Dat.Version + ")");
            foreach (char c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
            return Path.Combine(Path.GetDirectoryName(sys.Folder), name.TrimEnd('.', ' '));
        }

        // The names a game's files get: the DAT's names for the files it knows, in a folder (or zip) named after
        // the game. Files the DAT doesn't list are left out. For games not matched to the DAT, the names stay.
        static List<Tuple<ZipItem, string>> Renamed(FileResult f, Dictionary<string, ZipItem> source, bool inFolder)
        {
            var list = new List<Tuple<ZipItem, string>>();
            if (f.Game != null && (f.Kind == FileKind.Misnamed || f.Kind == FileKind.NotZipped || f.Kind == FileKind.Ok))
            {
                foreach (var r in f.Game.Roms)
                {
                    var item = f.Items.FirstOrDefault(i => Scanner.SameData(i, r));
                    if (item == null) continue;
                    list.Add(Tuple.Create(source[item.FullName], inFolder ? f.Game.Name + "/" + r.Name : r.Name));
                }
            }
            else
                foreach (var i in f.Items)
                    list.Add(Tuple.Create(source[i.FullName], inFolder ? i.FullName : i.Name));
            return list;
        }

        static void NeedSpace(string path, long bytes)
        {
            var drive = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(path)));
            long need = bytes + 200L * 1024 * 1024;
            if (drive.AvailableFreeSpace < need)
                throw new IOException("Not enough free space on " + drive.Name + " (needs " + Naming.Size(need) + ", has " + Naming.Size(drive.AvailableFreeSpace) + ")");
        }

        // Keeps the pack: writes a new copy with games renamed and/or left out, checks it, then swaps it in.
        public static List<string> Rewrite(SystemResult sys, ICollection<FileResult> rename, ICollection<FileResult> remove, Action<int, int> progress, CancellationToken ct)
        {
            var lines = new List<string>();
            string pack = sys.Folder;
            string temp = pack + ".romcheck-new";
            try
            {
                if (!pack.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) throw new IOException("Only .zip packs can be changed");
                var entries = ZipReader.Read(pack);
                var source = entries.ToDictionary(e => e.Name, StringComparer.Ordinal);
                var renameSet = new HashSet<FileResult>(rename);
                var removeSet = new HashSet<FileResult>(remove);
                var owners = new Dictionary<string, FileResult>(StringComparer.Ordinal);
                foreach (var f in sys.Files) foreach (var i in f.Items) owners[i.FullName] = f;

                var output = new List<Tuple<ZipItem, string>>();
                var done = new HashSet<FileResult>();
                foreach (var e in entries)
                {
                    FileResult f;
                    if (!owners.TryGetValue(e.Name, out f)) { output.Add(Tuple.Create(e, e.Name)); continue; }
                    if (removeSet.Contains(f)) continue;
                    if (renameSet.Contains(f)) { if (done.Add(f)) output.AddRange(Renamed(f, source, true)); continue; }
                    output.Add(Tuple.Create(e, e.Name));
                }
                if (output.Select(o => o.Item2).Distinct(StringComparer.OrdinalIgnoreCase).Count() != output.Count)
                    throw new IOException("Two games would end up with the same name. Delete unneeded games first, then try again");

                NeedSpace(pack, output.Sum(o => o.Item1.CompressedSize));
                if (File.Exists(temp)) File.Delete(temp);
                long total = Math.Max(1, output.Sum(o => o.Item1.CompressedSize));
                ZipCopier.Write(pack, output, temp, copied => { if (progress != null) progress((int)(copied * 500 / total), 1000); }, ct);
                if (progress != null) progress(500, 1000);
                string err = Crc32.VerifyZip(temp, output, ct);
                if (err != null) throw new IOException("The new copy of the pack didn't check out (" + err + "), so the pack wasn't changed");
                if (progress != null) progress(1000, 1000);

                File.Replace(temp, pack, null);
                Log.Write("Rewrote pack \"" + pack + "\": " + rename.Count + " renamed, " + remove.Count + " removed");
                foreach (var f in rename) lines.Add("Renamed " + f.Name + " -> " + (f.Game != null ? f.Game.Name : f.Name) + " (in " + Path.GetFileName(pack) + ")");
                foreach (var f in remove) lines.Add("Deleted " + f.Name + " (from " + Path.GetFileName(pack) + ")");
            }
            catch (OperationCanceledException) { TryDelete(temp); throw; }
            catch (Exception ex)
            {
                TryDelete(temp);
                lines.Add("COULDN'T CHANGE " + Path.GetFileName(pack) + ": " + ex.Message);
            }
            return lines;
        }

        // Unpacks the pack: one zip per game in a folder beside it, each checked, then the pack is deleted.
        // Games the DAT knows get its names; others keep theirs (and show as unneeded afterwards).
        public static List<string> Unpack(SystemResult sys, Action<int, int> progress, CancellationToken ct)
        {
            var lines = new List<string>();
            string pack = sys.Folder;
            string folder = UnpackFolder(sys);
            var made = new List<string>();
            try
            {
                if (!pack.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) throw new IOException("Only .zip packs can be unpacked");
                var entries = ZipReader.Read(pack);
                var source = entries.ToDictionary(e => e.Name, StringComparer.Ordinal);
                var plan = new List<Tuple<string, List<Tuple<ZipItem, string>>>>();
                foreach (var f in sys.Files)
                {
                    bool known = f.Game != null && (f.Kind == FileKind.Misnamed || f.Kind == FileKind.NotZipped || f.Kind == FileKind.Ok);
                    plan.Add(Tuple.Create(Path.Combine(folder, (known ? f.Game.Name : f.PackGame) + ".zip"), Renamed(f, source, false)));
                }
                var dupes = plan.GroupBy(p => p.Item1, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1).Select(g => Path.GetFileName(g.Key)).ToList();
                if (dupes.Count > 0) throw new IOException("Two games would get the same zip name (" + dupes[0] + "). Delete unneeded games first, then try again");
                var clash = plan.FirstOrDefault(p => File.Exists(p.Item1));
                if (clash != null) throw new IOException("\"" + Path.GetFileName(clash.Item1) + "\" already exists in " + Path.GetFileName(folder));

                NeedSpace(pack, plan.Sum(p => p.Item2.Sum(e => e.Item1.CompressedSize)));
                Directory.CreateDirectory(folder);
                for (int i = 0; i < plan.Count; i++)
                {
                    ct.ThrowIfCancellationRequested();
                    if (progress != null && i % 25 == 0) progress(i, plan.Count * 2);
                    ZipCopier.Write(pack, plan[i].Item2, plan[i].Item1, null, ct);
                    made.Add(plan[i].Item1);
                }
                for (int i = 0; i < plan.Count; i++)
                {
                    if (progress != null && i % 25 == 0) progress(plan.Count + i, plan.Count * 2);
                    string err = Crc32.VerifyZip(plan[i].Item1, plan[i].Item2, ct);
                    if (err != null) throw new IOException(Path.GetFileName(plan[i].Item1) + " didn't check out (" + err + "), so the pack was kept");
                }
                File.SetAttributes(pack, FileAttributes.Normal);
                File.Delete(pack);
                Log.Write("Unpacked \"" + pack + "\" into " + plan.Count + " zips in \"" + folder + "\", then deleted the pack");
                lines.Add("Unpacked " + Path.GetFileName(pack) + " into " + plan.Count.ToString("N0") + " zips in " + Path.GetFileName(folder) + ", then deleted the pack");
            }
            catch (Exception ex)
            {
                // Undo: remove the zips made so far, so the pack (which is untouched) is the only copy again.
                foreach (var z in made) TryDelete(z);
                try { if (Directory.Exists(folder) && !Directory.EnumerateFileSystemEntries(folder).Any()) Directory.Delete(folder); } catch { }
                if (ex is OperationCanceledException) throw;
                lines.Add("COULDN'T UNPACK " + Path.GetFileName(pack) + ": " + ex.Message);
            }
            return lines;
        }

        static void TryDelete(string path) { try { if (File.Exists(path)) File.Delete(path); } catch { } }
    }

    // A record of every rename and delete, next to the .exe.
    static class Log
    {
        public static string Path = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ROM-Check-O-MATIC.log");
        static readonly object sync = new object();

        public static void Write(string line)
        {
            lock (sync)
            {
                try { File.AppendAllText(Path, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss  ", CultureInfo.InvariantCulture) + line + Environment.NewLine, Encoding.UTF8); }
                catch { }
            }
        }
    }

    // ---------------------------------------------------------------- report

    static class Report
    {
        public static string Build(IEnumerable<SystemResult> systems, bool includeMia)
        {
            var sb = new StringBuilder();
            sb.AppendLine("ROM Check O-MATIC report, " + DateTime.Now.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture));
            sb.AppendLine();
            foreach (var s in systems)
            {
                sb.AppendLine(new string('=', 78));
                sb.AppendLine(s.FolderName);
                sb.AppendLine();
                if (s.Error != null) { sb.AppendLine("  " + s.Error); sb.AppendLine(); continue; }
                sb.AppendLine("  DAT: " + s.DatText);
                sb.AppendLine();
                if (s.Excluded != GameType.None) { sb.AppendLine("  Excluded ROM types: " + GameTypes.Describe(s.Excluded)); sb.AppendLine(); }
                if (s.IgnoredRegions.Count > 0) { sb.AppendLine("  Excluded ROM regions: " + string.Join(", ", s.IgnoredRegions)); sb.AppendLine(); }
                sb.AppendLine("  Have " + s.Have + " of " + s.Total + (s.OneGame ? " titles (1G1R)" : " ROMs") +
                              ", missing " + s.MissingCount + " (plus " + s.MiaCount + " MIA)");
                Section(sb, "Missing", s.Missing.Where(g => includeMia || !g.Mia).Select(g => g.Name + (g.Mia ? "  [MIA]" : "") +
                    (s.MissingNote.ContainsKey(g) ? "  (" + s.MissingNote[g] + ")" : "")));
                Section(sb, "Incorrectly named", s.Files.Where(f => f.IsFixable).Select(f => f.Name + "  ->  " + f.TargetName + "  (" + f.Detail + ")"));
                Section(sb, "Unneeded", s.Files.Where(f => f.IsUnneeded).Select(f => f.Name + "  (" + f.Detail + ")"));
                Section(sb, "Problems", s.Files.Where(f => f.IsProblem).Select(f => f.Name + "  (" + f.Detail + ")"));
                Section(sb, "Not in the DAT but kept", s.Files.Where(f => f.Kind == FileKind.Kept).Select(f => f.Name));
                sb.AppendLine();
            }
            return sb.ToString();
        }

        static void Section(StringBuilder sb, string title, IEnumerable<string> lines)
        {
            var list = lines.ToList();
            sb.AppendLine();
            sb.AppendLine("  " + title + " (" + list.Count + ")");
            foreach (var l in list) sb.AppendLine("    " + l);
        }
    }
}
