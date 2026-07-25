using System.Text;

namespace Emby.FastIptv.LiveTv.Probe
{
    // Maps a PMT entry (stream_type plus its descriptors) onto an ffmpeg codec name.
    //
    // Returning null means "not understood", and the caller then declines to describe the whole
    // channel. That is intentional: every unrecognised entry would shift the ffmpeg stream
    // indices of everything after it, and Emby builds "-map 0:<index>" from what we advertise.
    // Only PES stream types appear here — section-carried types (DSM-CC, private sections) are
    // rejected because ffmpeg does not necessarily create a stream for them.
    internal static class StreamTypes
    {
        public static ProbedStream Classify(int streamType, byte[] section, int descriptorsStart, int descriptorsLength)
        {
            var descriptors = new DescriptorSet(section, descriptorsStart, descriptorsLength);
            var stream = FromStreamType(streamType, descriptors);
            if (stream == null) return null;

            stream.Language = descriptors.Language;
            return stream;
        }

        private static ProbedStream FromStreamType(int streamType, DescriptorSet descriptors)
        {
            switch (streamType)
            {
                case 0x01: return Video("mpeg1video");
                case 0x02: return Video("mpeg2video");
                case 0x03:
                case 0x04: return Audio("mp2"); // exact layer comes from the frame header
                case 0x0F: return Audio("aac");
                case 0x11: return Audio("aac_latm");
                case 0x10: return Video("mpeg4");
                case 0x1B: return Video("h264");
                case 0x24: return Video("hevc");
                case 0x81: return Audio("ac3");
                case 0x82: return Audio("dts");
                case 0x84: return Audio("eac3");
                case 0x85:
                case 0x86: return Audio("dts");
                case 0x87: return Audio("eac3");
                case 0x8A: return Audio("dts");
                case 0xEA: return Video("vc1");

                // Private PES: only the descriptors say what is actually inside.
                case 0x06: return FromPrivateDescriptors(descriptors);

                default: return null;
            }
        }

        private static ProbedStream FromPrivateDescriptors(DescriptorSet descriptors)
        {
            if (descriptors.Has(0x6A)) return Audio("ac3");           // DVB AC-3
            if (descriptors.Has(0x7A)) return Audio("eac3");          // DVB enhanced AC-3
            if (descriptors.Has(0x7B)) return Audio("dts");           // DVB DTS
            if (descriptors.Has(0x7C)) return Audio("aac");           // DVB AAC
            if (descriptors.Has(0x59)) return Subtitle("dvb_subtitle");
            if (descriptors.Has(0x56)) return Subtitle("dvb_teletext");

            switch (descriptors.RegistrationFormat)
            {
                case "AC-3": return Audio("ac3");
                case "EAC3": return Audio("eac3");
                case "DTS1":
                case "DTS2":
                case "DTS3": return Audio("dts");
                case "Opus": return Audio("opus");
                case "AV01": return Video("av1");
                case "VC-1": return Video("vc1");
                default: return null;
            }
        }

        private static ProbedStream Video(string codec)
            => new ProbedStream { Kind = ProbedStreamKind.Video, Codec = codec };

        private static ProbedStream Audio(string codec)
            => new ProbedStream { Kind = ProbedStreamKind.Audio, Codec = codec };

        private static ProbedStream Subtitle(string codec)
            => new ProbedStream { Kind = ProbedStreamKind.Subtitle, Codec = codec };

        // Walks the descriptor loop of one PMT entry once, keeping only what classification needs.
        private struct DescriptorSet
        {
            private readonly byte[] _data;
            private readonly int _start;
            private readonly int _length;

            public DescriptorSet(byte[] data, int start, int length)
            {
                _data = data;
                _start = start;
                _length = length;
                Language = null;
                RegistrationFormat = null;

                for (var i = start; i + 2 <= start + length;)
                {
                    var tag = data[i];
                    var len = data[i + 1];
                    var payload = i + 2;
                    if (payload + len > start + length) break;

                    switch (tag)
                    {
                        case 0x0A when len >= 3: // ISO 639 language
                            Language = ReadAscii(data, payload, 3);
                            break;
                        case 0x59 when len >= 3 && Language == null: // DVB subtitling
                            Language = ReadAscii(data, payload, 3);
                            break;
                        case 0x05 when len >= 4: // registration
                            RegistrationFormat = ReadAscii(data, payload, 4);
                            break;
                    }

                    i = payload + len;
                }
            }

            public string Language { get; private set; }
            public string RegistrationFormat { get; private set; }

            public bool Has(int tag)
            {
                for (var i = _start; i + 2 <= _start + _length;)
                {
                    var len = _data[i + 1];
                    if (_data[i] == tag) return true;
                    i += 2 + len;
                }

                return false;
            }

            private static string ReadAscii(byte[] data, int start, int count)
            {
                var text = Encoding.ASCII.GetString(data, start, count).Trim();
                foreach (var c in text)
                    if (c < 0x20 || c > 0x7E)
                        return null;

                return text.Length == 0 ? null : text;
            }
        }
    }
}
