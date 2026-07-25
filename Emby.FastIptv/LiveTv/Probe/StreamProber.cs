using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Emby.FastIptv.LiveTv.Probe
{
    internal sealed class ProbeOptions
    {
        public int TimeoutSeconds { get; set; } = 4;
        public int MaxBytes { get; set; } = 4 * 1024 * 1024;
        public TimeSpan CacheTtl { get; set; } = TimeSpan.FromHours(24);
        public TimeSpan FailureCacheTtl { get; set; } = TimeSpan.FromMinutes(15);

        // Where to persist results so a server restart does not re-probe every channel.
        public string CacheFilePath { get; set; }
    }

    internal sealed class ProbeResult
    {
        public List<ProbedStream> Streams { get; set; }
        public string Rejection { get; set; }
        public long ElapsedMs { get; set; }
        public int BytesRead { get; set; }
        public bool FromCache { get; set; }

        public bool IsUsable => Streams != null && Streams.Count > 0;
    }

    // Reads just enough of a live stream to name its codecs, then caches the answer.
    //
    // The point is to give Emby real stream details up front so it does not have to run its own
    // probe on every tune, without ever handing it a guess: anything the parser is not sure
    // about comes back as "no streams", and Emby falls back to probing that channel itself.
    internal static class StreamProber
    {
        // Bump when the parsers change what they produce, so persisted results are discarded.
        private const int CacheFormatVersion = 1;

        private const int ReadChunkSize = 64 * 1024;
        private const int MinParseIntervalBytes = 64 * 1024;
        private const int MinBytesBeforeSyncGiveUp = 192 * 1024;
        private const int MaxPlaylistBytes = 512 * 1024;
        private const int InitialBufferBytes = 256 * 1024;

        // Codecs come out of the PMT within the first packets; resolution needs a key frame, which
        // on a long-GOP feed can be megabytes in. Past this point the probe settles for the codecs
        // it has rather than making the user wait — Emby then applies the native resolution, which
        // is what it did all along when the plugin advertised fixed codecs.
        private const int ResolutionDeadlineMs = 1200;

        private static readonly HttpClient Http = new HttpClient
        {
            Timeout = Timeout.InfiniteTimeSpan // per-call deadline comes from a linked CTS
        };

        private static readonly Dictionary<string, CacheEntry> Cache = new Dictionary<string, CacheEntry>(StringComparer.Ordinal);
        private static readonly Dictionary<string, Task<ProbeResult>> InFlight = new Dictionary<string, Task<ProbeResult>>(StringComparer.Ordinal);
        private static readonly object Gate = new object();
        private static bool _diskCacheLoaded;

        public static async Task<ProbeResult> ProbeAsync(
            string url,
            string userAgent,
            IDictionary<string, string> headers,
            ProbeOptions options,
            CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(url)) return new ProbeResult { Rejection = "no url" };

            EnsureDiskCacheLoaded(options);

            Task<ProbeResult> probe;
            lock (Gate)
            {
                if (Cache.TryGetValue(url, out var cached) && !cached.IsExpired(options))
                    return cached.ToResult();

                // Two clients starting the same channel at once should cost one probe, not two
                // connections against a provider that may only allow one.
                if (!InFlight.TryGetValue(url, out probe))
                {
                    probe = RunProbeAsync(url, userAgent, headers, options, ct);
                    InFlight[url] = probe;
                }
            }

            try
            {
                return await probe.ConfigureAwait(false);
            }
            finally
            {
                lock (Gate) { InFlight.Remove(url); }
            }
        }

        // Drops everything, so re-saving a tuner is a way out of a stale result.
        public static void ClearCache(ProbeOptions options)
        {
            lock (Gate)
            {
                Cache.Clear();
                var path = options?.CacheFilePath;
                if (string.IsNullOrEmpty(path)) return;
                try { if (File.Exists(path)) File.Delete(path); } catch { /* best effort */ }
            }
        }

        private static async Task<ProbeResult> RunProbeAsync(
            string url,
            string userAgent,
            IDictionary<string, string> headers,
            ProbeOptions options,
            CancellationToken ct)
        {
            var stopwatch = Stopwatch.StartNew();
            var result = new ProbeResult();

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, options.TimeoutSeconds)));

            try
            {
                var target = url;
                if (IsPlaylistUrl(url))
                {
                    target = await ResolveHlsSegmentAsync(url, userAgent, headers, cts.Token).ConfigureAwait(false);
                    if (target == null)
                        result.Rejection = "HLS playlist not usable for fast probing";
                }

                if (result.Rejection == null)
                    await ReadAndParseAsync(target, userAgent, headers, options, result, cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException)
            {
                result.Rejection = "probe timed out";
            }
            catch (Exception ex)
            {
                result.Rejection = ex.GetType().Name + ": " + ex.Message;
            }

            stopwatch.Stop();
            result.ElapsedMs = stopwatch.ElapsedMilliseconds;

            Store(url, result, options);
            return result;
        }

        private static async Task ReadAndParseAsync(
            string url,
            string userAgent,
            IDictionary<string, string> headers,
            ProbeOptions options,
            ProbeResult result,
            CancellationToken ct)
        {
            var maxBytes = Math.Max(64 * 1024, options.MaxBytes);

            using var request = BuildRequest(HttpMethod.Get, url, userAgent, headers);
            using var response = await Http
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct)
                .ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);

            var accumulated = new byte[Math.Min(maxBytes, InitialBufferBytes)];
            var chunk = new byte[ReadChunkSize];
            var total = 0;
            var nextParseAt = MinParseIntervalBytes;
            var clock = Stopwatch.StartNew();
            TsProbeSnapshot snapshot = null;
            var parsedAt = -1;

            while (total < maxBytes)
            {
                var wanted = Math.Min(chunk.Length, maxBytes - total);
                var read = await stream.ReadAsync(chunk, 0, wanted, ct).ConfigureAwait(false);
                if (read <= 0) break;

                if (total + read > accumulated.Length)
                {
                    var grown = new byte[Math.Min(maxBytes, Math.Max(accumulated.Length * 2, total + read))];
                    Buffer.BlockCopy(accumulated, 0, grown, 0, total);
                    accumulated = grown;
                }

                Buffer.BlockCopy(chunk, 0, accumulated, total, read);
                total += read;

                if (total < nextParseAt) continue;

                // Re-parsing costs a few milliseconds per megabyte, so the interval grows with the
                // buffer instead of paying that on every chunk.
                nextParseAt = total + Math.Max(MinParseIntervalBytes, total / 2);

                snapshot = TsProbe.Parse(accumulated, total);
                parsedAt = total;
                if (snapshot.IsComplete) break;

                if (snapshot.HasCodecs && clock.ElapsedMilliseconds >= ResolutionDeadlineMs) break;

                // Not MPEG-TS at all (an error page, or fragmented MP4) — stop wasting time.
                if (!snapshot.SyncFound && total >= MinBytesBeforeSyncGiveUp) break;
            }

            result.BytesRead = total;
            if (parsedAt != total)
                snapshot = TsProbe.Parse(accumulated, total);

            Accept(snapshot ?? new TsProbeSnapshot(), result);
        }

        // Decides whether a snapshot is solid enough to advertise. Everything questionable is
        // turned into a rejection, because a wrong codec breaks playback outright while a
        // rejection only costs the time of Emby's own probe.
        private static void Accept(TsProbeSnapshot snapshot, ProbeResult result)
        {
            if (snapshot.Rejection != null)
            {
                result.Rejection = snapshot.Rejection;
                return;
            }

            if (!snapshot.SyncFound)
            {
                result.Rejection = "not an MPEG-TS stream";
                return;
            }

            if (snapshot.Streams == null)
            {
                result.Rejection = "no PMT within the probe budget";
                return;
            }

            var video = snapshot.Streams.Find(s => s.Kind == ProbedStreamKind.Video);
            if (video != null)
            {
                if (!snapshot.HasVideoPayload)
                {
                    result.Rejection = "no video payload within the probe budget";
                    return;
                }

                // 10-bit HEVC is where HDR lives, and colour range needs VUI/SEI parsing this
                // probe deliberately does not do. Advertising such a channel as plain SDR would
                // suppress tone mapping, so those go to Emby's probe.
                if (video.BitDepth > 8)
                {
                    result.Rejection = "high bit depth video needs a full probe";
                    return;
                }
            }

            result.Streams = snapshot.Streams;
        }

        // ── HLS ────────────────────────────────────────────────────────────────

        // Returns the URL of a segment worth probing, or null when the playlist is not something
        // we can describe safely (several variants, or fragmented MP4).
        private static async Task<string> ResolveHlsSegmentAsync(
            string url, string userAgent, IDictionary<string, string> headers, CancellationToken ct)
        {
            var playlist = await GetTextAsync(url, userAgent, headers, ct).ConfigureAwait(false);
            if (playlist == null) return null;

            var variants = CollectVariantUrls(playlist, url);
            if (variants.Count > 1) return null; // ffmpeg exposes every variant; index order is not ours to guess

            if (variants.Count == 1)
            {
                url = variants[0];
                playlist = await GetTextAsync(url, userAgent, headers, ct).ConfigureAwait(false);
                if (playlist == null) return null;
                if (CollectVariantUrls(playlist, url).Count > 0) return null; // nested master
            }

            if (playlist.IndexOf("#EXT-X-MAP", StringComparison.OrdinalIgnoreCase) >= 0)
                return null; // fragmented MP4, not MPEG-TS

            return FindFirstSegmentUrl(playlist, url);
        }

        private static List<string> CollectVariantUrls(string playlist, string baseUrl)
        {
            var variants = new List<string>();
            var lines = playlist.Split('\n');

            for (var i = 0; i < lines.Length; i++)
            {
                if (!lines[i].StartsWith("#EXT-X-STREAM-INF", StringComparison.OrdinalIgnoreCase)) continue;

                for (var j = i + 1; j < lines.Length; j++)
                {
                    var candidate = lines[j].Trim();
                    if (candidate.Length == 0 || candidate[0] == '#') continue;
                    variants.Add(ResolveUrl(baseUrl, candidate));
                    break;
                }
            }

            return variants;
        }

        private static string FindFirstSegmentUrl(string playlist, string baseUrl)
        {
            var lines = playlist.Split('\n');
            for (var i = 0; i < lines.Length; i++)
            {
                if (!lines[i].StartsWith("#EXTINF", StringComparison.OrdinalIgnoreCase)) continue;

                for (var j = i + 1; j < lines.Length; j++)
                {
                    var candidate = lines[j].Trim();
                    if (candidate.Length == 0 || candidate[0] == '#') continue;
                    return ResolveUrl(baseUrl, candidate);
                }
            }

            return null;
        }

        private static string ResolveUrl(string baseUrl, string relative)
            => Uri.TryCreate(new Uri(baseUrl), relative, out var absolute) ? absolute.ToString() : relative;

        private static async Task<string> GetTextAsync(
            string url, string userAgent, IDictionary<string, string> headers, CancellationToken ct)
        {
            using var request = BuildRequest(HttpMethod.Get, url, userAgent, headers);
            using var response = await Http
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct)
                .ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return null;

            using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            var buffer = new byte[MaxPlaylistBytes];
            var total = 0;
            while (total < buffer.Length)
            {
                var read = await stream.ReadAsync(buffer, total, buffer.Length - total, ct).ConfigureAwait(false);
                if (read <= 0) break;
                total += read;
            }

            return Encoding.UTF8.GetString(buffer, 0, total);
        }

        public static bool IsPlaylistUrl(string url)
        {
            if (string.IsNullOrEmpty(url)) return false;
            var path = url.Split('?')[0];
            return path.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase)
                || path.EndsWith(".m3u", StringComparison.OrdinalIgnoreCase);
        }

        private static HttpRequestMessage BuildRequest(
            HttpMethod method, string url, string userAgent, IDictionary<string, string> headers)
        {
            var request = new HttpRequestMessage(method, url);

            if (headers != null)
                foreach (var kv in headers)
                    request.Headers.TryAddWithoutValidation(kv.Key, kv.Value);

            if (!string.IsNullOrEmpty(userAgent))
                request.Headers.TryAddWithoutValidation("User-Agent", userAgent);

            return request;
        }

        // ── cache ──────────────────────────────────────────────────────────────

        private sealed class CacheEntry
        {
            public List<ProbedStream> Streams { get; set; }
            public string Rejection { get; set; }
            public DateTime Stored { get; set; }

            public bool IsExpired(ProbeOptions options)
            {
                var ttl = Streams != null ? options.CacheTtl : options.FailureCacheTtl;
                return DateTime.UtcNow - Stored > ttl;
            }

            public ProbeResult ToResult() => new ProbeResult
            {
                Streams = Streams,
                Rejection = Rejection,
                FromCache = true
            };
        }

        private static void Store(string url, ProbeResult result, ProbeOptions options)
        {
            lock (Gate)
            {
                Cache[url] = new CacheEntry
                {
                    Streams = result.Streams,
                    Rejection = result.Rejection,
                    Stored = DateTime.UtcNow
                };
            }

            // Failures are worth remembering in memory but not worth persisting: they are often
            // a provider hiccup, and a restart is a reasonable moment to try again.
            if (result.Streams != null)
                SaveDiskCache(options);
        }

        private static void EnsureDiskCacheLoaded(ProbeOptions options)
        {
            lock (Gate)
            {
                if (_diskCacheLoaded) return;
                _diskCacheLoaded = true;

                var path = options?.CacheFilePath;
                if (string.IsNullOrEmpty(path) || !File.Exists(path)) return;

                try
                {
                    var json = File.ReadAllText(path);
                    var file = System.Text.Json.JsonSerializer.Deserialize<CacheFile>(json);
                    if (file == null || file.Version != CacheFormatVersion || file.Entries == null) return;

                    foreach (var entry in file.Entries)
                    {
                        if (string.IsNullOrEmpty(entry.Url) || entry.Streams == null) continue;
                        Cache[entry.Url] = new CacheEntry
                        {
                            Streams = entry.Streams,
                            Stored = entry.Stored
                        };
                    }
                }
                catch
                {
                    // A damaged cache file is not worth a plugin failure; it just gets rewritten.
                }
            }
        }

        private static void SaveDiskCache(ProbeOptions options)
        {
            var path = options?.CacheFilePath;
            if (string.IsNullOrEmpty(path)) return;

            try
            {
                CacheFile file;
                lock (Gate)
                {
                    file = new CacheFile { Version = CacheFormatVersion, Entries = new List<CacheFileEntry>() };
                    foreach (var kv in Cache)
                    {
                        if (kv.Value.Streams == null) continue;
                        file.Entries.Add(new CacheFileEntry
                        {
                            Url = kv.Key,
                            Stored = kv.Value.Stored,
                            Streams = kv.Value.Streams
                        });
                    }
                }

                var directory = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

                var json = System.Text.Json.JsonSerializer.Serialize(file);
                var temp = path + ".tmp";
                File.WriteAllText(temp, json);
                File.Copy(temp, path, true);
                File.Delete(temp);
            }
            catch
            {
                // Persistence is an optimisation; losing it only costs one probe per channel.
            }
        }

        internal sealed class CacheFile
        {
            public int Version { get; set; }
            public List<CacheFileEntry> Entries { get; set; }
        }

        internal sealed class CacheFileEntry
        {
            public string Url { get; set; }
            public DateTime Stored { get; set; }
            public List<ProbedStream> Streams { get; set; }
        }
    }
}
