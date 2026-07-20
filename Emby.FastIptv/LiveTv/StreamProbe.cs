using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace Emby.FastIptv.LiveTv
{
    /// <summary>
    /// Probes an HLS playlist's near-live TS segment for real codecs (PMT stream_type).
    /// Avoids advertising hardcoded h264/1080p for HEVC/4K channels.
    /// </summary>
    internal static class StreamProbe
    {
        public sealed class Result
        {
            public string VideoCodec { get; set; }
            public string AudioCodec { get; set; }
            public int Width { get; set; }
            public int Height { get; set; }
        }

        public static Result DetectFromTsBuffer(byte[] buffer)
        {
            var result = new Result();
            if (buffer == null || buffer.Length < 188)
                return result;

            for (var i = 0; i + 188 <= buffer.Length; i += 188)
            {
                if (buffer[i] != 0x47) continue;
                if ((buffer[i + 1] & 0x40) == 0) continue;

                var adaptation = (buffer[i + 3] >> 4) & 0x3;
                var offset = 4;
                if ((adaptation & 0x2) != 0)
                {
                    var adapLen = buffer[i + 4];
                    offset = 5 + adapLen;
                }
                if (offset >= 188) continue;

                var pointer = buffer[i + offset];
                var tableStart = offset + 1 + pointer;
                if (tableStart + 12 >= 188) continue;
                if (buffer[i + tableStart] != 0x02) continue; // PMT

                var sectionLength = ((buffer[i + tableStart + 1] & 0x0F) << 8) | buffer[i + tableStart + 2];
                var programInfoLength = ((buffer[i + tableStart + 10] & 0x0F) << 8) | buffer[i + tableStart + 11];
                var pos = i + tableStart + 12 + programInfoLength;
                var end = Math.Min(i + 188, i + tableStart + 3 + sectionLength - 4);

                while (pos + 5 <= end)
                {
                    var streamType = buffer[pos];
                    var esInfoLen = ((buffer[pos + 3] & 0x0F) << 8) | buffer[pos + 4];
                    ApplyStreamType(result, streamType);
                    pos += 5 + esInfoLen;
                }
            }

            if (string.IsNullOrEmpty(result.VideoCodec))
            {
                if (IndexOf(buffer, new byte[] { 0x48, 0x45, 0x56, 0x43 }) >= 0)
                    result.VideoCodec = "hevc";
                else if (IndexOf(buffer, new byte[] { 0x61, 0x76, 0x63, 0x31 }) >= 0 ||
                         IndexOf(buffer, new byte[] { 0x00, 0x00, 0x00, 0x01, 0x67 }) >= 0)
                    result.VideoCodec = "h264";
            }

            return result;
        }

        public static async Task<Result> ProbePlaylistAsync(
            HttpClient http,
            Uri playlistUri,
            string userAgent,
            CancellationToken cancellationToken)
        {
            var result = new Result();
            if (http == null || playlistUri == null)
                return result;

            using (var req = new HttpRequestMessage(HttpMethod.Get, playlistUri))
            {
                if (!string.IsNullOrEmpty(userAgent))
                    req.Headers.TryAddWithoutValidation("User-Agent", userAgent);

                using (var resp = await http.SendAsync(req, HttpCompletionOption.ResponseContentRead, cancellationToken)
                    .ConfigureAwait(false))
                {
                    resp.EnsureSuccessStatusCode();
                    var body = await resp.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

                    // Prefer RESOLUTION from a master playlist if present.
                    TryParseResolution(body, result);

                    var segmentLines = body.Split('\n')
                        .Select(l => l.Trim())
                        .Where(l => l.Length > 0 && !l.StartsWith("#", StringComparison.Ordinal))
                        .ToList();
                    if (segmentLines.Count == 0)
                        return result;

                    // Near live edge — oldest segment may be a different codec after reconnects.
                    var probeIndex = Math.Max(0, segmentLines.Count - 4);
                    var segmentLine = segmentLines[probeIndex];
                    var segmentUri = segmentLine.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                        ? new Uri(segmentLine)
                        : new Uri(playlistUri, segmentLine);

                    using (var segReq = new HttpRequestMessage(HttpMethod.Get, segmentUri))
                    {
                        if (!string.IsNullOrEmpty(userAgent))
                            segReq.Headers.TryAddWithoutValidation("User-Agent", userAgent);

                        using (var segResp = await http.SendAsync(
                                segReq, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                            .ConfigureAwait(false))
                        {
                            segResp.EnsureSuccessStatusCode();
                            using (var stream = await segResp.Content.ReadAsStreamAsync(cancellationToken)
                                .ConfigureAwait(false))
                            {
                                var buffer = new byte[188 * 128];
                                var read = 0;
                                while (read < buffer.Length)
                                {
                                    var n = await stream.ReadAsync(buffer, read, buffer.Length - read, cancellationToken)
                                        .ConfigureAwait(false);
                                    if (n <= 0) break;
                                    read += n;
                                }

                                if (read < 188)
                                    return result;

                                var sliced = new byte[read];
                                Buffer.BlockCopy(buffer, 0, sliced, 0, read);
                                var detected = DetectFromTsBuffer(sliced);
                                if (!string.IsNullOrEmpty(detected.VideoCodec))
                                    result.VideoCodec = detected.VideoCodec;
                                if (!string.IsNullOrEmpty(detected.AudioCodec))
                                    result.AudioCodec = detected.AudioCodec;
                                return result;
                            }
                        }
                    }
                }
            }
        }

        private static void TryParseResolution(string body, Result result)
        {
            if (string.IsNullOrEmpty(body)) return;
            foreach (var line in body.Split('\n'))
            {
                var idx = line.IndexOf("RESOLUTION=", StringComparison.OrdinalIgnoreCase);
                if (idx < 0) continue;
                var rest = line.Substring(idx + "RESOLUTION=".Length);
                var end = 0;
                while (end < rest.Length && (char.IsDigit(rest[end]) || rest[end] == 'x' || rest[end] == 'X'))
                    end++;
                var res = rest.Substring(0, end);
                var parts = res.Split(new[] { 'x', 'X' }, 2);
                if (parts.Length == 2 &&
                    int.TryParse(parts[0], out var w) &&
                    int.TryParse(parts[1], out var h) &&
                    w > 0 && h > 0)
                {
                    result.Width = w;
                    result.Height = h;
                    return;
                }
            }
        }

        private static void ApplyStreamType(Result result, byte streamType)
        {
            switch (streamType)
            {
                case 0x1B:
                    result.VideoCodec = result.VideoCodec ?? "h264";
                    break;
                case 0x24:
                    result.VideoCodec = "hevc";
                    break;
                case 0x0F:
                case 0x11:
                    result.AudioCodec = result.AudioCodec ?? "aac";
                    break;
                case 0x03:
                case 0x04:
                    result.AudioCodec = result.AudioCodec ?? "mp3";
                    break;
            }
        }

        private static int IndexOf(byte[] haystack, byte[] needle)
        {
            if (haystack == null || needle == null || needle.Length == 0 || haystack.Length < needle.Length)
                return -1;
            for (var i = 0; i <= haystack.Length - needle.Length; i++)
            {
                var ok = true;
                for (var j = 0; j < needle.Length; j++)
                {
                    if (haystack[i + j] != needle[j]) { ok = false; break; }
                }
                if (ok) return i;
            }
            return -1;
        }
    }
}
