using System;
using System.Collections.Generic;
using System.Globalization;
using System.Xml.Linq;
using MediaBrowser.Controller.LiveTv;

namespace Emby.FastIptv.LiveTv
{
    internal static class XmlTvParser
    {
        // Returns ProgramInfo list for one channel, filtered to [rangeStart, rangeEnd).
        // embyChannelId  : the prefixed ID used by ChannelInfo.Id  (e.g. "fastiptv_CNN")
        // xmlTvChannelId : the raw tvg-id used in the XMLTV file    (e.g. "CNN")
        internal static List<ProgramInfo> ParsePrograms(
            XDocument doc,
            string embyChannelId,
            string xmlTvChannelId,
            DateTimeOffset rangeStart,
            DateTimeOffset rangeEnd)
        {
            var result = new List<ProgramInfo>();
            if (doc?.Root == null || string.IsNullOrEmpty(xmlTvChannelId))
                return result;

            foreach (var prog in doc.Root.Elements("programme"))
            {
                if (!string.Equals((string)prog.Attribute("channel"), xmlTvChannelId,
                        StringComparison.OrdinalIgnoreCase))
                    continue;

                var start = ParseXmlTvDate((string)prog.Attribute("start"));
                var stop  = ParseXmlTvDate((string)prog.Attribute("stop"));

                if (!start.HasValue || !stop.HasValue) continue;
                if (stop.Value <= rangeStart || start.Value >= rangeEnd) continue;

                var title     = prog.Element("title")?.Value ?? string.Empty;
                var desc      = prog.Element("desc")?.Value;
                var subTitle  = prog.Element("sub-title")?.Value;
                var iconSrc   = (string)prog.Element("icon")?.Attribute("src");

                var genres = new List<string>();
                foreach (var cat in prog.Elements("category"))
                {
                    var v = cat.Value?.Trim();
                    if (!string.IsNullOrEmpty(v)) genres.Add(v);
                }

                int? season = null, episode = null;
                foreach (var epNum in prog.Elements("episode-num"))
                {
                    if (string.Equals((string)epNum.Attribute("system"), "xmltv_ns",
                            StringComparison.OrdinalIgnoreCase))
                    {
                        ParseEpisodeNum(epNum.Value, out season, out episode);
                        break;
                    }
                }

                var rating = prog.Element("rating")?.Element("value")?.Value?.Trim();

                result.Add(new ProgramInfo
                {
                    Id            = "prog_" + embyChannelId + "_" + start.Value.ToUnixTimeSeconds(),
                    ChannelId     = embyChannelId,
                    Name          = title,
                    Overview      = desc,
                    EpisodeTitle  = subTitle,
                    ImageUrl      = iconSrc,
                    StartDate     = start.Value.UtcDateTime,
                    EndDate       = stop.Value.UtcDateTime,
                    Genres        = genres,
                    OfficialRating = rating,
                    SeasonNumber  = season,
                    EpisodeNumber = episode,
                    IsLive        = false,
                    IsNew         = false
                });
            }

            return result;
        }

        // Parses XMLTV timestamp format: YYYYMMDDHHmmss [±HHMM]
        internal static DateTimeOffset? ParseXmlTvDate(string value)
        {
            if (string.IsNullOrEmpty(value)) return null;

            var v       = value.Trim();
            var spaceIdx = v.IndexOf(' ');
            var datePart = spaceIdx > 0 ? v.Substring(0, spaceIdx) : v;
            var tzPart   = spaceIdx > 0 ? v.Substring(spaceIdx + 1).Trim() : null;

            if (!DateTime.TryParseExact(datePart, "yyyyMMddHHmmss",
                    CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt))
                return null;

            if (!string.IsNullOrEmpty(tzPart) && tzPart.Length >= 5
                && (tzPart[0] == '+' || tzPart[0] == '-'))
            {
                var sign = tzPart[0] == '-' ? -1 : 1;
                if (int.TryParse(tzPart.Substring(1, 2), out var h) &&
                    int.TryParse(tzPart.Substring(3, 2), out var m))
                {
                    return new DateTimeOffset(dt, TimeSpan.FromMinutes(sign * (h * 60 + m)));
                }
            }

            return new DateTimeOffset(dt, TimeSpan.Zero);
        }

        // Parses xmltv_ns episode number: "season.episode.part" (all 0-based, may include "/total")
        private static void ParseEpisodeNum(string value, out int? season, out int? episode)
        {
            season  = null;
            episode = null;
            if (string.IsNullOrEmpty(value)) return;

            var parts = value.Split('.');
            if (parts.Length >= 1 && TryParseXmlTvNsPart(parts[0], out var s)) season  = s + 1;
            if (parts.Length >= 2 && TryParseXmlTvNsPart(parts[1], out var e)) episode = e + 1;
        }

        // "2/10" or "2" → returns 2; empty/whitespace → false
        private static bool TryParseXmlTvNsPart(string raw, out int result)
        {
            result = 0;
            if (string.IsNullOrWhiteSpace(raw)) return false;
            var slash = raw.IndexOf('/');
            var token = slash > 0 ? raw.Substring(0, slash).Trim() : raw.Trim();
            return int.TryParse(token, out result);
        }
    }
}
