using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace Emby.FastIptv.LiveTv
{
    public static class M3uParser
    {
        public static List<M3uChannel> Parse(string m3u)
        {
            var channels = new List<M3uChannel>();
            var lines = m3u.Split('\n');

            M3uChannel pending = null;
            int autoNumber = 1;

            foreach (var raw in lines)
            {
                var line = raw.Trim();

                if (line.StartsWith("#EXTINF:", StringComparison.OrdinalIgnoreCase))
                {
                    pending = ParseExtInf(line, autoNumber++);
                }
                else if (!line.StartsWith("#") && !string.IsNullOrEmpty(line) && pending != null)
                {
                    pending.Url = line;
                    channels.Add(pending);
                    pending = null;
                }
            }

            return channels;
        }

        private static M3uChannel ParseExtInf(string line, int fallbackNumber)
        {
            var channel = new M3uChannel();

            // Display name is everything after the last comma
            var commaIdx = line.LastIndexOf(',');
            var displayName = commaIdx >= 0 ? line.Substring(commaIdx + 1).Trim() : string.Empty;

            var tvgName = Attr(line, "tvg-name");
            var tvgId = Attr(line, "tvg-id");
            var tvgChno = Attr(line, "tvg-chno");

            channel.Name = !string.IsNullOrEmpty(tvgName) ? tvgName : displayName;
            channel.TvgId = tvgId;
            channel.TvgLogo = Attr(line, "tvg-logo");
            channel.Group = Attr(line, "group-title");
            channel.IsRadio = string.Equals(Attr(line, "radio"), "true", StringComparison.OrdinalIgnoreCase)
                           || string.Equals(Attr(line, "type"), "radio", StringComparison.OrdinalIgnoreCase);

            if (!int.TryParse(tvgChno, out var chno))
                chno = fallbackNumber;
            channel.Number = chno;

            // Stable ID: prefer tvg-id, fall back to sanitised name
            channel.Id = !string.IsNullOrEmpty(tvgId)
                ? tvgId
                : Regex.Replace(channel.Name ?? string.Empty, @"[^a-zA-Z0-9]", "_").ToLowerInvariant();

            if (string.IsNullOrEmpty(channel.Id))
                channel.Id = Guid.NewGuid().ToString("N");

            return channel;
        }

        private static string Attr(string line, string name)
        {
            var m = Regex.Match(line, $@"{Regex.Escape(name)}=""([^""]*)""", RegexOptions.IgnoreCase);
            return m.Success ? m.Groups[1].Value : null;
        }
    }
}
