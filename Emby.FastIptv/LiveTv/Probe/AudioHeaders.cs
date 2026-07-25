namespace Emby.FastIptv.LiveTv.Probe
{
    // Reads sample rate and channel count from the first frame header of an audio elementary
    // stream. Best-effort: when nothing parses, the stream keeps its codec and Emby just sees
    // an unknown channel count, which is what it also sees for any container without that
    // information up front.
    internal static class AudioHeaders
    {
        private static readonly int[] AacSampleRates =
        {
            96000, 88200, 64000, 48000, 44100, 32000,
            24000, 22050, 16000, 12000, 11025, 8000, 7350
        };

        // acmod -> channel count, before the LFE channel is added.
        private static readonly int[] Ac3Channels = { 2, 1, 2, 3, 3, 4, 4, 5 };

        private static readonly int[] Ac3SampleRates = { 48000, 44100, 32000 };

        private static readonly int[,] MpegAudioSampleRates =
        {
            { 44100, 48000, 32000 }, // MPEG-1
            { 22050, 24000, 16000 }, // MPEG-2
            { 11025, 12000, 8000 }   // MPEG-2.5
        };

        public static void TryParse(string codec, byte[] es, int length, ProbedStream target)
        {
            switch (codec)
            {
                case "aac": ParseAdts(es, length, target); break;
                case "ac3": ParseAc3(es, length, target); break;
                case "mp1":
                case "mp2":
                case "mp3": ParseMpegAudio(es, length, target); break;
            }
        }

        // MPEG audio frames carry the layer in the header, so the exact codec name is only known
        // once a frame is seen. Returns null when no plausible frame is found.
        public static string DetectMpegAudioCodec(byte[] es, int length)
        {
            var offset = FindMpegAudioFrame(es, length);
            if (offset < 0) return null;

            var layerBits = (es[offset + 1] >> 1) & 3;
            switch (layerBits)
            {
                case 3: return "mp1";
                case 2: return "mp2";
                case 1: return "mp3";
                default: return null;
            }
        }

        private static void ParseAdts(byte[] es, int length, ProbedStream target)
        {
            for (var i = 0; i + 6 < length; i++)
            {
                if (es[i] != 0xFF || (es[i + 1] & 0xF6) != 0xF0) continue;

                var sampleRateIndex = (es[i + 2] >> 2) & 0x0F;
                var channelConfig = ((es[i + 2] & 0x01) << 2) | ((es[i + 3] >> 6) & 0x03);
                if (sampleRateIndex >= AacSampleRates.Length) continue;

                target.SampleRate = AacSampleRates[sampleRateIndex];
                // channel_configuration 0 means "described in the AudioSpecificConfig", and 7
                // means 7.1 (8 channels); everything else maps to its own value.
                if (channelConfig > 0)
                    target.Channels = channelConfig == 7 ? 8 : channelConfig;

                var objectType = ((es[i + 2] >> 6) & 0x03) + 1;
                if (objectType == 2) target.Profile = "LC";
                else if (objectType == 5) target.Profile = "HE-AAC";
                return;
            }
        }

        private static void ParseAc3(byte[] es, int length, ProbedStream target)
        {
            for (var i = 0; i + 8 < length; i++)
            {
                if (es[i] != 0x0B || es[i + 1] != 0x77) continue;

                var fscod = (es[i + 4] >> 6) & 0x03;
                if (fscod > 2) continue;

                // syncinfo is 5 bytes; bsi starts at byte 5 with bsid(5) bsmod(3), then acmod.
                var r = new BitReader(es, length, i + 6);
                var acmod = (int)r.ReadBits(3);
                if ((acmod & 0x01) != 0 && acmod != 0x01) r.ReadBits(2); // cmixlev
                if ((acmod & 0x04) != 0) r.ReadBits(2);                  // surmixlev
                if (acmod == 0x02) r.ReadBits(2);                        // dsurmod
                var lfeOn = r.ReadBit() == 1;
                if (r.Overrun) return;

                target.SampleRate = Ac3SampleRates[fscod];
                target.Channels = Ac3Channels[acmod] + (lfeOn ? 1 : 0);
                return;
            }
        }

        private static void ParseMpegAudio(byte[] es, int length, ProbedStream target)
        {
            var offset = FindMpegAudioFrame(es, length);
            if (offset < 0) return;

            var versionBits = (es[offset + 1] >> 3) & 3; // 3 = MPEG-1, 2 = MPEG-2, 0 = MPEG-2.5
            var rateIndex = (es[offset + 2] >> 2) & 3;
            var channelMode = (es[offset + 3] >> 6) & 3; // 3 = single channel

            var versionRow = versionBits == 3 ? 0 : versionBits == 2 ? 1 : 2;
            if (rateIndex < 3)
                target.SampleRate = MpegAudioSampleRates[versionRow, rateIndex];

            target.Channels = channelMode == 3 ? 1 : 2;
        }

        private static int FindMpegAudioFrame(byte[] es, int length)
        {
            for (var i = 0; i + 3 < length; i++)
            {
                if (es[i] != 0xFF || (es[i + 1] & 0xE0) != 0xE0) continue;

                var versionBits = (es[i + 1] >> 3) & 3;
                var layerBits = (es[i + 1] >> 1) & 3;
                var bitrateIndex = (es[i + 2] >> 4) & 0x0F;
                var rateIndex = (es[i + 2] >> 2) & 3;

                if (versionBits == 1 || layerBits == 0) continue;       // reserved
                if (bitrateIndex == 0 || bitrateIndex == 0x0F) continue; // free/invalid
                if (rateIndex == 3) continue;                            // reserved

                return i;
            }

            return -1;
        }
    }
}
