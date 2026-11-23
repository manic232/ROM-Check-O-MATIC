// ROM Check O-MATIC - reads DATs in the older ClrMamePro text format, such as Redump's BIOS DATs:
//
//   clrmamepro ( name "Sony - PlayStation - BIOS Images" version 2016-10-21 )
//   game ( name "ps-10j" rom ( name ps-10j.bin size 524288 crc 3b601fc8 ) )

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace RomCheckOMatic
{
    static class Cmp
    {
        // True when the file doesn't start with "<" (an XML DAT always does).
        public static bool IsCmp(string path)
        {
            using (var r = new StreamReader(path, Encoding.UTF8, true))
            {
                int c;
                while ((c = r.Read()) >= 0)
                    if (!char.IsWhiteSpace((char)c)) return c != '<';
            }
            return false;
        }

        // Words, "quoted strings", "(" and ")".
        static List<string> Tokens(string text)
        {
            var list = new List<string>();
            int i = 0;
            while (i < text.Length)
            {
                char c = text[i];
                if (char.IsWhiteSpace(c)) { i++; continue; }
                if (c == '(' || c == ')') { list.Add(c.ToString()); i++; continue; }
                if (c == '"')
                {
                    int end = text.IndexOf('"', i + 1);
                    if (end < 0) end = text.Length;
                    list.Add(text.Substring(i + 1, end - i - 1));
                    i = end + 1;
                    continue;
                }
                int start = i;
                while (i < text.Length && !char.IsWhiteSpace(text[i]) && text[i] != '(' && text[i] != ')') i++;
                list.Add(text.Substring(start, i - start));
            }
            return list;
        }

        // One "( key value key value sub ( ... ) )" block, from the "(" at tokens[i]. Leaves i after the ")".
        class Block
        {
            public Dictionary<string, string> Values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            public List<KeyValuePair<string, Block>> Children = new List<KeyValuePair<string, Block>>();
        }

        static Block ReadBlock(List<string> t, ref int i)
        {
            var b = new Block();
            i++;   // skip "("
            while (i < t.Count && t[i] != ")")
            {
                string key = t[i++];
                if (i < t.Count && t[i] == "(") b.Children.Add(new KeyValuePair<string, Block>(key, ReadBlock(t, ref i)));
                else if (i < t.Count && t[i] != ")") { if (!b.Values.ContainsKey(key)) b.Values[key] = t[i]; i++; }
            }
            i++;   // skip ")"
            return b;
        }

        static IEnumerable<KeyValuePair<string, Block>> TopBlocks(string path)
        {
            var t = Tokens(File.ReadAllText(path));
            int i = 0;
            while (i < t.Count)
            {
                if (i + 1 < t.Count && t[i + 1] == "(")
                {
                    string key = t[i];
                    i++;
                    yield return new KeyValuePair<string, Block>(key, ReadBlock(t, ref i));
                }
                else i++;
            }
        }

        static string Get(Block b, string key)
        {
            string v;
            return b.Values.TryGetValue(key, out v) ? v : null;
        }

        public static void ReadHeader(string path, DatInfo info)
        {
            foreach (var kv in TopBlocks(path))
            {
                if (!kv.Key.Equals("clrmamepro", StringComparison.OrdinalIgnoreCase) && !kv.Key.Equals("header", StringComparison.OrdinalIgnoreCase)) continue;
                info.Name = (Get(kv.Value, "name") ?? "").Trim();
                info.Version = (Get(kv.Value, "version") ?? "").Trim();
                info.Author = ((Get(kv.Value, "author") ?? "") + " " + (Get(kv.Value, "homepage") ?? "")).Trim();
                return;
            }
        }

        public static void Read(DatFile dat, string path, string datName)
        {
            foreach (var kv in TopBlocks(path))
            {
                string key = kv.Key.ToLowerInvariant();
                if (key != "game" && key != "machine" && key != "resource") continue;
                var g = kv.Value;
                // These DATs link versions by name ("cloneof"), so the name doubles as the id.
                string name = Get(g, "name") ?? "";
                var game = new DatGame { Name = name, Id = name, CloneOf = Get(g, "cloneof") };
                string category = Get(g, "category");
                if (category != null) game.Categories.Add(category);
                foreach (var child in g.Children.Where(c => c.Key.Equals("rom", StringComparison.OrdinalIgnoreCase)))
                {
                    var r = child.Value;
                    DatFile.AddRom(game, Get(r, "name"), Get(r, "size"), Get(r, "crc"), Get(r, "flags") ?? Get(r, "status") ?? "", false);
                }
                DatFile.AddGame(dat, game, datName);
            }
        }
    }
}
