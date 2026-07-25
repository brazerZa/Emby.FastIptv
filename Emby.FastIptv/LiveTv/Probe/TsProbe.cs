using System;
using System.Collections.Generic;

namespace Emby.FastIptv.LiveTv.Probe
{
    // What one parse pass over the bytes collected so far concluded.
    internal sealed class TsProbeSnapshot
    {
        // Streams in PMT order. Their Index values are what ffmpeg will use, so this list is
        // only usable when every entry in the PMT was understood — see Rejection.
        public List<ProbedStream> Streams { get; set; }

        // Set when this stream must not be described by us at all (unknown stream type,
        // multi-program transport, scrambled, not MPEG-TS). The caller then advertises
        // nothing and lets Emby run its own probe.
        public string Rejection { get; set; }

        public bool SyncFound { get; set; }
        public bool HasProgramMap { get; set; }
        public bool HasVideoDimensions { get; set; }
        public bool HasVideoPayload { get; set; }
        public bool HasAudioDetails { get; set; }

        // True once there is nothing left to gain from reading more bytes.
        public bool IsComplete => Rejection != null || (HasVideoDimensions && HasAudioDetails);

        // True once the codecs are known. Resolution may still be missing, which is survivable:
        // Emby then applies the stream's native resolution, exactly as it did when the plugin
        // advertised fixed codecs without any resolution at all.
        public bool HasCodecs
        {
            get
            {
                if (Streams == null) return false;
                foreach (var stream in Streams)
                    if (stream.Kind == ProbedStreamKind.Video && !HasVideoPayload)
                        return false;
                return true;
            }
        }
    }

    // Minimal MPEG-TS demuxer: PAT -> PMT -> per-PID elementary stream bytes, just far enough
    // to name the codecs and read the video parameter set. Deliberately stateless — every call
    // re-parses the whole buffer collected so far, which costs a couple of milliseconds per MB
    // and avoids a pile of resumable-parser state.
    internal static class TsProbe
    {
        private const int MaxAudioScanBytes = 64 * 1024;
        private static readonly int[] CandidatePacketSizes = { 188, 192, 204 };

        public static TsProbeSnapshot Parse(byte[] data, int length)
        {
            var result = new TsProbeSnapshot();

            var packetSize = DetectPacketSize(data, length, 0, out var offset);
            if (packetSize == 0) return result;
            result.SyncFound = true;

            var pat = new SectionAssembler();
            var pmt = new SectionAssembler();
            var pmtPid = -1;
            List<ProbedStream> streams = null;
            Dictionary<int, EsBuffer> esBuffers = null;

            for (var p = offset; p + packetSize <= length; p += packetSize)
            {
                if (data[p] != 0x47)
                {
                    // Lost alignment (dropped bytes upstream). Try to pick the pattern back up.
                    var size = DetectPacketSize(data, length, p, out var resync);
                    if (size == 0) break;
                    packetSize = size;
                    p = resync - packetSize;
                    continue;
                }

                if ((data[p + 1] & 0x80) != 0) continue; // transport_error_indicator

                var pid = ((data[p + 1] & 0x1F) << 8) | data[p + 2];
                var payloadUnitStart = (data[p + 1] & 0x40) != 0;
                var scrambled = ((data[p + 3] >> 6) & 0x03) != 0;
                var adaptationControl = (data[p + 3] >> 4) & 0x03;

                if ((adaptationControl & 0x01) == 0) continue; // no payload

                var payload = p + 4;
                if ((adaptationControl & 0x02) != 0)
                    payload += 1 + data[p + 4];

                var payloadLength = p + 188 - payload; // the 188-byte packet body, even for 192/204
                if (payloadLength <= 0 || payload + payloadLength > length) continue;

                if (pid == 0)
                {
                    pat.Feed(data, payload, payloadLength, payloadUnitStart);
                    if (pat.IsComplete && pmtPid < 0)
                    {
                        pmtPid = ParsePat(pat, result);
                        if (result.Rejection != null) return result;
                    }
                    continue;
                }

                if (pid == pmtPid)
                {
                    pmt.Feed(data, payload, payloadLength, payloadUnitStart);
                    if (pmt.IsComplete && streams == null)
                    {
                        streams = ParsePmt(pmt, result);
                        if (result.Rejection != null) return result;

                        result.HasProgramMap = true;
                        result.Streams = streams;
                        esBuffers = CreateEsBuffers(streams, length);
                    }
                    continue;
                }

                if (esBuffers == null || !esBuffers.TryGetValue(pid, out var buffer)) continue;

                if (scrambled)
                {
                    result.Rejection = "stream is scrambled";
                    return result;
                }

                if (payloadUnitStart)
                {
                    var esOffset = PesPayloadOffset(data, payload, payloadLength);
                    if (esOffset < 0) continue;
                    buffer.Append(data, payload + esOffset, payloadLength - esOffset);
                }
                else
                {
                    buffer.Append(data, payload, payloadLength);
                }
            }

            if (streams == null || esBuffers == null) return result;

            FillStreamDetails(streams, esBuffers, result);
            return result;
        }

        // ── PSI ────────────────────────────────────────────────────────────────

        private static int ParsePat(SectionAssembler pat, TsProbeSnapshot result)
        {
            var s = pat.Buffer;
            var total = pat.SectionTotalLength;

            if (s[0] != 0x00)
            {
                result.Rejection = "PID 0 is not a PAT";
                return -1;
            }

            if (s[7] != 0x00)
            {
                result.Rejection = "multi-section PAT";
                return -1;
            }

            var pmtPid = -1;
            var programs = 0;
            for (var i = 8; i + 4 <= total - 4; i += 4)
            {
                var programNumber = (s[i] << 8) | s[i + 1];
                if (programNumber == 0) continue; // network information table

                programs++;
                pmtPid = ((s[i + 2] & 0x1F) << 8) | s[i + 3];
            }

            if (programs != 1)
            {
                // ffmpeg exposes every program's streams, and their index order then depends on
                // PMT arrival order rather than on anything we can see here.
                result.Rejection = programs == 0 ? "PAT has no programs" : "transport carries multiple programs";
                return -1;
            }

            return pmtPid;
        }

        private static List<ProbedStream> ParsePmt(SectionAssembler pmt, TsProbeSnapshot result)
        {
            var s = pmt.Buffer;
            var total = pmt.SectionTotalLength;

            if (s[0] != 0x02)
            {
                result.Rejection = "PMT PID does not carry a PMT";
                return null;
            }

            var programInfoLength = ((s[10] & 0x0F) << 8) | s[11];
            var streams = new List<ProbedStream>();
            var index = 0;

            for (var i = 12 + programInfoLength; i + 5 <= total - 4;)
            {
                var streamType = s[i];
                var pid = ((s[i + 1] & 0x1F) << 8) | s[i + 2];
                var esInfoLength = ((s[i + 3] & 0x0F) << 8) | s[i + 4];
                var descriptorsStart = i + 5;
                if (descriptorsStart + esInfoLength > total - 4)
                {
                    result.Rejection = "truncated PMT";
                    return null;
                }

                var stream = StreamTypes.Classify(streamType, s, descriptorsStart, esInfoLength);
                if (stream == null)
                {
                    // An entry we cannot name would shift every following stream index, so the
                    // whole channel goes to Emby's probe instead of being described wrongly.
                    result.Rejection = $"unsupported PMT stream_type 0x{streamType:X2}";
                    return null;
                }

                stream.Index = index++;
                stream.Pid = pid;
                streams.Add(stream);

                i = descriptorsStart + esInfoLength;
            }

            if (streams.Count == 0)
            {
                result.Rejection = "PMT lists no streams";
                return null;
            }

            return streams;
        }

        // ── elementary streams ─────────────────────────────────────────────────

        private static Dictionary<int, EsBuffer> CreateEsBuffers(List<ProbedStream> streams, int availableBytes)
        {
            var buffers = new Dictionary<int, EsBuffer>();
            var videoTaken = false;
            // The video elementary stream cannot be larger than the transport we were given, so
            // sizing on that keeps the parameter-set search reaching as far as the caller read.
            var videoCapacity = Math.Min(availableBytes, VideoParameterSets.MaxScanBytes);

            foreach (var stream in streams)
            {
                if (stream.Kind == ProbedStreamKind.Video && !videoTaken)
                {
                    videoTaken = true;
                    buffers[stream.Pid] = new EsBuffer(videoCapacity);
                }
                else if (stream.Kind == ProbedStreamKind.Audio)
                {
                    buffers[stream.Pid] = new EsBuffer(MaxAudioScanBytes);
                }
            }

            return buffers;
        }

        private static void FillStreamDetails(
            List<ProbedStream> streams, Dictionary<int, EsBuffer> esBuffers, TsProbeSnapshot result)
        {
            foreach (var stream in streams)
            {
                if (!esBuffers.TryGetValue(stream.Pid, out var buffer) || buffer.Length == 0)
                    continue;

                if (stream.Kind == ProbedStreamKind.Video)
                {
                    result.HasVideoPayload = true;
                    if (VideoParameterSets.TryParse(stream.Codec, buffer.Data, buffer.Length, stream))
                        result.HasVideoDimensions = true;
                }
                else if (stream.Kind == ProbedStreamKind.Audio)
                {
                    // MPEG audio only reveals its layer in the frame header.
                    if (stream.Codec == "mp2")
                    {
                        var detected = AudioHeaders.DetectMpegAudioCodec(buffer.Data, buffer.Length);
                        if (detected != null) stream.Codec = detected;
                    }

                    AudioHeaders.TryParse(stream.Codec, buffer.Data, buffer.Length, stream);
                }
            }

            var audioDescribed = true;
            foreach (var stream in streams)
                if (stream.Kind == ProbedStreamKind.Audio && stream.SampleRate == null)
                    audioDescribed = false;

            result.HasAudioDetails = audioDescribed;
        }

        private static int PesPayloadOffset(byte[] data, int start, int count)
        {
            if (count < 9) return -1;
            if (data[start] != 0x00 || data[start + 1] != 0x00 || data[start + 2] != 0x01) return -1;
            if ((data[start + 6] & 0xC0) != 0x80) return -1; // not an MPEG-2 PES header

            var headerLength = data[start + 8];
            var offset = 9 + headerLength;
            return offset < count ? offset : -1;
        }

        // Looks for four consecutive sync bytes at one of the known packet strides.
        private static int DetectPacketSize(byte[] data, int length, int from, out int offset)
        {
            offset = 0;
            var searchLimit = Math.Min(length, from + 8 * 1024);

            for (var start = from; start < searchLimit; start++)
            {
                if (data[start] != 0x47) continue;

                foreach (var size in CandidatePacketSizes)
                {
                    if (start + size * 3 >= length) continue;
                    if (data[start + size] == 0x47 &&
                        data[start + size * 2] == 0x47 &&
                        data[start + size * 3] == 0x47)
                    {
                        offset = start;
                        return size;
                    }
                }
            }

            return 0;
        }

        // ── small helpers ──────────────────────────────────────────────────────

        private sealed class EsBuffer
        {
            private readonly int _capacity;

            public EsBuffer(int capacity)
            {
                _capacity = capacity;
                Data = new byte[Math.Min(capacity, 64 * 1024)];
            }

            public byte[] Data { get; private set; }
            public int Length { get; private set; }

            public void Append(byte[] source, int start, int count)
            {
                if (count <= 0 || Length >= _capacity) return;
                count = Math.Min(count, _capacity - Length);

                if (Length + count > Data.Length)
                {
                    var grown = new byte[Math.Min(_capacity, Math.Max(Data.Length * 2, Length + count))];
                    Buffer.BlockCopy(Data, 0, grown, 0, Length);
                    Data = grown;
                }

                Buffer.BlockCopy(source, start, Data, Length, count);
                Length += count;
            }
        }

        // Reassembles one PSI section, which may span several transport packets.
        private sealed class SectionAssembler
        {
            private const int MaxSectionLength = 4096;
            private bool _started;

            public byte[] Buffer { get; } = new byte[MaxSectionLength];
            public int Length { get; private set; }
            public int SectionTotalLength { get; private set; }
            public bool IsComplete { get; private set; }

            public void Feed(byte[] source, int start, int count, bool payloadUnitStart)
            {
                if (IsComplete) return;

                if (payloadUnitStart)
                {
                    if (count < 1) return;
                    var pointerField = source[start];
                    var sectionStart = start + 1 + pointerField;
                    var sectionCount = count - 1 - pointerField;
                    if (sectionCount <= 0) return;

                    Length = 0;
                    SectionTotalLength = 0;
                    _started = true;
                    Append(source, sectionStart, sectionCount);
                }
                else if (_started)
                {
                    Append(source, start, count);
                }

                if (Length >= 3 && SectionTotalLength == 0)
                    SectionTotalLength = (((Buffer[1] & 0x0F) << 8) | Buffer[2]) + 3;

                if (SectionTotalLength <= 0 || SectionTotalLength > MaxSectionLength || Length < SectionTotalLength)
                    return;

                // A section that fails its CRC is dropped rather than rejected: PAT and PMT
                // repeat every few hundred milliseconds, so the next copy is moments away.
                if (Crc32Mpeg.IsSectionValid(Buffer, SectionTotalLength))
                    IsComplete = true;
                else
                    Reset();
            }

            private void Reset()
            {
                _started = false;
                Length = 0;
                SectionTotalLength = 0;
            }

            private void Append(byte[] source, int start, int count)
            {
                count = Math.Min(count, MaxSectionLength - Length);
                if (count <= 0) return;
                System.Buffer.BlockCopy(source, start, Buffer, Length, count);
                Length += count;
            }
        }
    }
}
