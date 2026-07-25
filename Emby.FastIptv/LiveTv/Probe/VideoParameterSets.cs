using System;

namespace Emby.FastIptv.LiveTv.Probe
{
    // Pulls resolution/profile/level/bit-depth out of the video elementary stream itself.
    //
    // Everything here is best-effort by design: a parser that returns false simply leaves the
    // stream without those details, and a probe that cannot determine the essentials hands the
    // channel back to Emby's own (slower, always-correct) probe. Every value is range-checked
    // before it is accepted, because a misparsed SPS producing a plausible-looking 4096x2160 is
    // worse than no value at all.
    internal static class VideoParameterSets
    {
        // Upper bound on how much elementary stream is scanned for parameter sets. Broadcast
        // encoders repeat the SPS at every key frame, but a 2-second GOP on a 20 Mbit/s feed puts
        // the first one several megabytes in, so this has to be generous. What actually limits
        // the work is the probe's read budget and deadline, not this cap.
        public const int MaxScanBytes = 16 * 1024 * 1024;

        private const int MinDimension = 16;
        private const int MaxDimension = 8192;

        public static bool TryParse(string codec, byte[] es, int length, ProbedStream target)
        {
            switch (codec)
            {
                case "h264": return TryParseH264(es, length, target);
                case "hevc": return TryParseHevc(es, length, target);
                case "mpeg2video":
                case "mpeg1video": return TryParseMpeg2(es, length, target);
                default: return false;
            }
        }

        // ── H.264 ──────────────────────────────────────────────────────────────

        private static bool TryParseH264(byte[] es, int length, ProbedStream target)
        {
            foreach (var nal in new AnnexBEnumerator(es, length))
            {
                if (nal.Length < 4) continue;
                if ((es[nal.Start] & 0x1F) != 7) continue; // not an SPS

                var rbsp = Unescape(es, nal.Start + 1, nal.Length - 1, out var rbspLength);
                if (ParseH264Sps(rbsp, rbspLength, target)) return true;
            }

            return false;
        }

        private static bool ParseH264Sps(byte[] rbsp, int length, ProbedStream target)
        {
            var r = new BitReader(rbsp, length);

            var profileIdc = (int)r.ReadBits(8);
            var constraintFlags = (int)r.ReadBits(8);
            var levelIdc = (int)r.ReadBits(8);
            r.ReadUe(); // seq_parameter_set_id

            var chromaFormatIdc = 1;
            var separateColourPlane = false;
            var bitDepth = 8;

            if (IsH264HighProfile(profileIdc))
            {
                chromaFormatIdc = (int)r.ReadUe();
                if (chromaFormatIdc == 3)
                    separateColourPlane = r.ReadBit() == 1;

                bitDepth = (int)r.ReadUe() + 8;
                r.ReadUe();  // bit_depth_chroma_minus8
                r.ReadBit(); // qpprime_y_zero_transform_bypass_flag

                if (r.ReadBit() == 1) // seq_scaling_matrix_present_flag
                {
                    var listCount = chromaFormatIdc != 3 ? 8 : 12;
                    for (var i = 0; i < listCount; i++)
                        if (r.ReadBit() == 1)
                            SkipScalingList(r, i < 6 ? 16 : 64);
                }
            }

            r.ReadUe(); // log2_max_frame_num_minus4

            var pocType = r.ReadUe();
            if (pocType == 0)
            {
                r.ReadUe(); // log2_max_pic_order_cnt_lsb_minus4
            }
            else if (pocType == 1)
            {
                r.ReadBit(); // delta_pic_order_always_zero_flag
                r.ReadSe();  // offset_for_non_ref_pic
                r.ReadSe();  // offset_for_top_to_bottom_field
                var cycleLength = r.ReadUe();
                if (cycleLength > 255) return false;
                for (var i = 0u; i < cycleLength; i++)
                    r.ReadSe();
            }

            r.ReadUe();  // max_num_ref_frames
            r.ReadBit(); // gaps_in_frame_num_value_allowed_flag

            var widthInMbs = r.ReadUe() + 1;
            var heightInMapUnits = r.ReadUe() + 1;
            var frameMbsOnly = r.ReadBit();
            if (frameMbsOnly == 0)
                r.ReadBit(); // mb_adaptive_frame_field_flag
            r.ReadBit();     // direct_8x8_inference_flag

            uint cropLeft = 0, cropRight = 0, cropTop = 0, cropBottom = 0;
            if (r.ReadBit() == 1) // frame_cropping_flag
            {
                cropLeft = r.ReadUe();
                cropRight = r.ReadUe();
                cropTop = r.ReadUe();
                cropBottom = r.ReadUe();
            }

            if (r.Overrun) return false;

            int subWidthC = 1, subHeightC = 1;
            if (!separateColourPlane)
            {
                switch (chromaFormatIdc)
                {
                    case 1: subWidthC = 2; subHeightC = 2; break;
                    case 2: subWidthC = 2; subHeightC = 1; break;
                    case 3: subWidthC = 1; subHeightC = 1; break;
                    default: subWidthC = 1; subHeightC = 1; break; // monochrome
                }
            }

            var cropUnitX = subWidthC;
            var cropUnitY = subHeightC * (2 - frameMbsOnly);

            var width = (long)widthInMbs * 16 - cropUnitX * ((long)cropLeft + cropRight);
            var height = (2L - frameMbsOnly) * heightInMapUnits * 16 - cropUnitY * ((long)cropTop + cropBottom);

            if (!IsSaneDimension(width) || !IsSaneDimension(height)) return false;

            target.Width = (int)width;
            target.Height = (int)height;
            target.BitDepth = bitDepth;
            target.IsInterlaced = frameMbsOnly == 0;
            target.Level = levelIdc > 0 ? levelIdc : (double?)null;
            target.Profile = H264ProfileName(profileIdc, constraintFlags);
            return true;
        }

        private static bool IsH264HighProfile(int profileIdc)
        {
            switch (profileIdc)
            {
                case 100: case 110: case 122: case 244: case 44:
                case 83: case 86: case 118: case 128: case 138: case 139: case 134: case 135:
                    return true;
                default:
                    return false;
            }
        }

        private static void SkipScalingList(BitReader r, int size)
        {
            var lastScale = 8;
            var nextScale = 8;
            for (var i = 0; i < size && !r.Overrun; i++)
            {
                if (nextScale != 0)
                {
                    var delta = r.ReadSe();
                    nextScale = (lastScale + delta + 256) % 256;
                }
                lastScale = nextScale == 0 ? lastScale : nextScale;
            }
        }

        // Names match ffprobe's so anything comparing profile strings sees what it expects.
        private static string H264ProfileName(int profileIdc, int constraintFlags)
        {
            switch (profileIdc)
            {
                case 66: return (constraintFlags & 0x40) != 0 ? "Constrained Baseline" : "Baseline";
                case 77: return "Main";
                case 88: return "Extended";
                case 100: return "High";
                case 110: return "High 10";
                case 122: return "High 4:2:2";
                case 244: return "High 4:4:4 Predictive";
                case 44: return "CAVLC 4:4:4";
                default: return null;
            }
        }

        // ── HEVC ───────────────────────────────────────────────────────────────

        private static bool TryParseHevc(byte[] es, int length, ProbedStream target)
        {
            foreach (var nal in new AnnexBEnumerator(es, length))
            {
                if (nal.Length < 5) continue;
                if (((es[nal.Start] >> 1) & 0x3F) != 33) continue; // not an SPS

                var rbsp = Unescape(es, nal.Start + 2, nal.Length - 2, out var rbspLength);
                if (ParseHevcSps(rbsp, rbspLength, target)) return true;
            }

            return false;
        }

        private static bool ParseHevcSps(byte[] rbsp, int length, ProbedStream target)
        {
            var r = new BitReader(rbsp, length);

            r.ReadBits(4); // sps_video_parameter_set_id
            var maxSubLayersMinus1 = (int)r.ReadBits(3);
            r.ReadBit();   // sps_temporal_id_nesting_flag

            // profile_tier_level
            r.ReadBits(2); // general_profile_space
            r.ReadBit();   // general_tier_flag
            var profileIdc = (int)r.ReadBits(5);
            r.SkipBits(32); // general_profile_compatibility_flags
            r.ReadBit();    // general_progressive_source_flag
            var interlacedSource = r.ReadBit() == 1;
            r.ReadBit();    // general_non_packed_constraint_flag
            r.ReadBit();    // general_frame_only_constraint_flag
            r.SkipBits(43); // general_reserved_zero_43bits
            r.ReadBit();    // general_inbld_flag / reserved
            var levelIdc = (int)r.ReadBits(8);

            var profilePresent = new bool[8];
            var levelPresent = new bool[8];
            for (var i = 0; i < maxSubLayersMinus1; i++)
            {
                profilePresent[i] = r.ReadBit() == 1;
                levelPresent[i] = r.ReadBit() == 1;
            }

            if (maxSubLayersMinus1 > 0)
                for (var i = maxSubLayersMinus1; i < 8; i++)
                    r.ReadBits(2); // reserved_zero_2bits

            for (var i = 0; i < maxSubLayersMinus1; i++)
            {
                if (profilePresent[i]) r.SkipBits(88);
                if (levelPresent[i]) r.SkipBits(8);
            }

            r.ReadUe(); // sps_seq_parameter_set_id

            var chromaFormatIdc = (int)r.ReadUe();
            if (chromaFormatIdc == 3)
                r.ReadBit(); // separate_colour_plane_flag

            var widthInSamples = r.ReadUe();
            var heightInSamples = r.ReadUe();

            uint winLeft = 0, winRight = 0, winTop = 0, winBottom = 0;
            if (r.ReadBit() == 1) // conformance_window_flag
            {
                winLeft = r.ReadUe();
                winRight = r.ReadUe();
                winTop = r.ReadUe();
                winBottom = r.ReadUe();
            }

            var bitDepth = (int)r.ReadUe() + 8;

            if (r.Overrun) return false;

            int subWidthC = 1, subHeightC = 1;
            switch (chromaFormatIdc)
            {
                case 1: subWidthC = 2; subHeightC = 2; break;
                case 2: subWidthC = 2; subHeightC = 1; break;
            }

            var width = widthInSamples - (long)subWidthC * ((long)winLeft + winRight);
            var height = heightInSamples - (long)subHeightC * ((long)winTop + winBottom);

            if (!IsSaneDimension(width) || !IsSaneDimension(height)) return false;
            if (bitDepth < 8 || bitDepth > 16) return false;

            target.Width = (int)width;
            target.Height = (int)height;
            target.BitDepth = bitDepth;
            target.IsInterlaced = interlacedSource;
            target.Level = levelIdc > 0 ? levelIdc : (double?)null;
            target.Profile = HevcProfileName(profileIdc);
            return true;
        }

        private static string HevcProfileName(int profileIdc)
        {
            switch (profileIdc)
            {
                case 1: return "Main";
                case 2: return "Main 10";
                case 3: return "Main Still Picture";
                case 4: return "Rext";
                default: return null;
            }
        }

        // ── MPEG-1/2 ───────────────────────────────────────────────────────────

        private static bool TryParseMpeg2(byte[] es, int length, ProbedStream target)
        {
            for (var i = 0; i + 7 < length; i++)
            {
                if (es[i] != 0x00 || es[i + 1] != 0x00 || es[i + 2] != 0x01 || es[i + 3] != 0xB3)
                    continue;

                var p = i + 4;
                var width = (es[p] << 4) | (es[p + 1] >> 4);
                var height = ((es[p + 1] & 0x0F) << 8) | es[p + 2];

                if (!IsSaneDimension(width) || !IsSaneDimension(height)) return false;

                target.Width = width;
                target.Height = height;
                target.BitDepth = 8;
                // MPEG-2 only states progressive_sequence in the sequence extension, and SD
                // broadcast feeds are interlaced far more often than not, so absence of the
                // extension is treated as interlaced.
                target.IsInterlaced = !HasProgressiveSequence(es, p, length);
                return true;
            }

            return false;
        }

        private static bool HasProgressiveSequence(byte[] es, int from, int length)
        {
            var limit = Math.Min(length - 6, from + 512);
            for (var i = from; i < limit; i++)
            {
                if (es[i] != 0x00 || es[i + 1] != 0x00 || es[i + 2] != 0x01 || es[i + 3] != 0xB5)
                    continue;
                if ((es[i + 4] >> 4) != 1) continue; // not the sequence extension
                return ((es[i + 5] >> 3) & 1) == 1;
            }

            return false;
        }

        // ── shared helpers ─────────────────────────────────────────────────────

        private static bool IsSaneDimension(long value)
            => value >= MinDimension && value <= MaxDimension;

        // Strips 0x03 emulation-prevention bytes so the bit reader sees real RBSP.
        private static byte[] Unescape(byte[] source, int start, int count, out int length)
        {
            var limit = Math.Min(count, 512); // parameter sets are tiny; this is plenty
            var output = new byte[limit];
            var written = 0;
            var zeros = 0;

            for (var i = 0; i < limit; i++)
            {
                var b = source[start + i];
                if (zeros >= 2 && b == 0x03)
                {
                    zeros = 0;
                    continue;
                }

                zeros = b == 0x00 ? zeros + 1 : 0;
                output[written++] = b;
            }

            length = written;
            return output;
        }

        // Walks Annex-B start codes, yielding each NAL unit's payload range.
        private struct AnnexBEnumerator
        {
            private readonly byte[] _data;
            private readonly int _length;
            private int _position;

            public AnnexBEnumerator(byte[] data, int length)
            {
                _data = data;
                _length = Math.Min(length, MaxScanBytes);
                _position = 0;
                Current = default;
            }

            public (int Start, int Length) Current { get; private set; }

            public AnnexBEnumerator GetEnumerator() => this;

            public bool MoveNext()
            {
                while (_position + 3 < _length)
                {
                    if (_data[_position] != 0x00 || _data[_position + 1] != 0x00)
                    {
                        _position++;
                        continue;
                    }

                    int payloadStart;
                    if (_data[_position + 2] == 0x01)
                        payloadStart = _position + 3;
                    else if (_data[_position + 2] == 0x00 && _data[_position + 3] == 0x01)
                        payloadStart = _position + 4;
                    else
                    {
                        _position++;
                        continue;
                    }

                    var next = FindNextStartCode(payloadStart);
                    Current = (payloadStart, next - payloadStart);
                    _position = next;
                    return true;
                }

                return false;
            }

            private int FindNextStartCode(int from)
            {
                for (var i = from; i + 2 < _length; i++)
                    if (_data[i] == 0x00 && _data[i + 1] == 0x00 &&
                        (_data[i + 2] == 0x01 || (_data[i + 2] == 0x00 && i + 3 < _length && _data[i + 3] == 0x01)))
                        return i;

                return _length;
            }
        }
    }
}
