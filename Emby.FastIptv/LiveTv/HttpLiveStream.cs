using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipelines;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Dto;

namespace Emby.FastIptv.LiveTv
{
    public class HttpLiveStream : ILiveStream
    {
        // Infinite timeout — per-request timeouts are applied via a linked CancellationTokenSource in Open().
        private static readonly HttpClient Http = new HttpClient
        {
            Timeout = System.Threading.Timeout.InfiniteTimeSpan
        };

        // 188 bytes is the MPEG-TS packet size; multiples keep reads packet-aligned to reduce
        // partial-packet splits that force extra syscalls and cause micro-stutters.
        private const int TsBufferSize = 188 * 1024;

        private readonly string _url;
        private readonly string _userAgent;
        private readonly Dictionary<string, string> _customHeaders;
        private readonly int _timeoutSeconds;
        private HttpResponseMessage _response;
        private Stream _stream;

        public HttpLiveStream(
            string url,
            MediaSourceInfo mediaSource,
            string tunerHostId,
            string userAgent = null,
            Dictionary<string, string> customHeaders = null,
            int timeoutSeconds = 15)
        {
            _url = url;
            _userAgent = userAgent;
            _customHeaders = customHeaders;
            _timeoutSeconds = timeoutSeconds > 0 ? timeoutSeconds : 15;
            MediaSource = mediaSource;
            TunerHostId = tunerHostId;
            UniqueId = Guid.NewGuid().ToString("N");
            OriginalStreamId = UniqueId;
            DateOpened = DateTimeOffset.UtcNow;
        }

        public int ConsumerCount { get; set; }
        public string OriginalStreamId { get; set; }
        public string TunerHostId { get; }
        public bool EnableStreamSharing => false;
        public MediaSourceInfo MediaSource { get; set; }
        public string UniqueId { get; }
        public DateTimeOffset DateOpened { get; }
        public bool SupportsCopyTo => true;

        // Emby 4.10+ ILiveStream members.
        public void AddConsumer(string id) => ConsumerCount++;

        public void RemoveConsumer(string id)
        {
            if (ConsumerCount > 0)
                ConsumerCount--;
        }

        public Task Open(CancellationToken cancellationToken) => OpenInternalAsync(cancellationToken);

        public Task Close()
        {
            _stream?.Dispose();
            _response?.Dispose();
            _stream = null;
            _response = null;
            return Task.CompletedTask;
        }

        public async Task CopyToAsync(
            Stream outputStream,
            DateTimeOffset? startAt,
            Action<SegmentedStreamSegmentInfo> segmentAction,
            CancellationToken cancellationToken)
        {
            if (_stream == null)
                throw new InvalidOperationException("Stream has not been opened.");

            var buffer = new byte[TsBufferSize];
            while (!cancellationToken.IsCancellationRequested)
            {
                int read;
                try
                {
                    read = await _stream.ReadAsync(buffer, 0, buffer.Length, cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception)
                {
                    // Network drop mid-stream — attempt one reconnect and resume from live position.
                    if (cancellationToken.IsCancellationRequested ||
                        !await TryReconnectAsync(cancellationToken).ConfigureAwait(false))
                        break;
                    continue;
                }

                if (read == 0) break;

                try
                {
                    await outputStream.WriteAsync(buffer, 0, read, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
            }
        }

        public async Task CopyToAsync(PipeWriter writer, CancellationToken cancellationToken)
        {
            if (_stream == null)
                throw new InvalidOperationException("Stream has not been opened.");

            await _stream.CopyToAsync(writer.AsStream(), TsBufferSize, cancellationToken)
                .ConfigureAwait(false);
        }

        // ── private helpers ──────────────────────────────────────────────────────

        private async Task OpenInternalAsync(CancellationToken ct)
        {
            // Connection timeout only — reading the live stream itself is unbounded.
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(_timeoutSeconds));

            using var req = new HttpRequestMessage(HttpMethod.Get, _url);

            if (!string.IsNullOrEmpty(_userAgent))
                req.Headers.TryAddWithoutValidation("User-Agent", _userAgent);

            if (_customHeaders != null)
                foreach (var kv in _customHeaders)
                    req.Headers.TryAddWithoutValidation(kv.Key, kv.Value);

            _response = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token)
                .ConfigureAwait(false);
            _response.EnsureSuccessStatusCode();
            _stream = await _response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        }

        private async Task<bool> TryReconnectAsync(CancellationToken ct)
        {
            try
            {
                _stream?.Dispose();
                _response?.Dispose();
                _stream = null;
                _response = null;

                await Task.Delay(500, ct).ConfigureAwait(false);
                await OpenInternalAsync(ct).ConfigureAwait(false);
                return true;
            }
            catch
            {
                return false;
            }
        }
    }
}
