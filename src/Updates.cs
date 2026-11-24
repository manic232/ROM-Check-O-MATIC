// ROM Check O-MATIC - checks GitHub for a newer release (Options > Check for Updates). Off unless you turn it on.

using System;
using System.IO;
using System.Net;
using System.Text.RegularExpressions;

namespace RomCheckOMatic
{
    class UpdateInfo
    {
        public Version Version;   // e.g. 1.2
        public string Tag;        // e.g. "v1.2"
        public string Page;       // the release's page on GitHub
    }

    static class Updates
    {
        public const string Repository = "manic232/ROM-Check-O-MATIC";

        public static Version Current { get { var v = typeof(Updates).Assembly.GetName().Version; return new Version(v.Major, v.Minor); } }

        // Asks GitHub for the latest release. Throws if GitHub can't be reached.
        public static UpdateInfo Latest()
        {
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            var request = (HttpWebRequest)WebRequest.Create("https://api.github.com/repos/" + Repository + "/releases/latest");
            request.UserAgent = "ROM-Check-O-MATIC";
            request.Accept = "application/vnd.github+json";
            request.Timeout = 10000;
            string json;
            using (var response = request.GetResponse())
            using (var reader = new StreamReader(response.GetResponseStream()))
                json = reader.ReadToEnd();

            var tag = Regex.Match(json, "\"tag_name\"\\s*:\\s*\"([^\"]+)\"");
            var page = Regex.Match(json, "\"html_url\"\\s*:\\s*\"(https://github\\.com/[^\"]+/releases/tag/[^\"]+)\"");
            var number = Regex.Match(tag.Groups[1].Value, @"(\d+)\.(\d+)");
            if (!tag.Success || !number.Success) throw new InvalidDataException("GitHub's answer didn't include a version");
            return new UpdateInfo
            {
                Tag = tag.Groups[1].Value,
                Version = new Version(int.Parse(number.Groups[1].Value), int.Parse(number.Groups[2].Value)),
                Page = page.Success ? page.Groups[1].Value : "https://github.com/" + Repository + "/releases/latest"
            };
        }
    }
}
