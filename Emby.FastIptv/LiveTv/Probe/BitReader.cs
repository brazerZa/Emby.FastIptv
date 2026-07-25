namespace Emby.FastIptv.LiveTv.Probe
{
    // Big-endian bit reader with the Exp-Golomb primitives H.264/HEVC parameter sets need.
    // Reads past the end return 0 and set Overrun instead of throwing, so a truncated or
    // corrupt parameter set ends up rejected by the caller's sanity checks rather than
    // taking the probe down.
    internal sealed class BitReader
    {
        private readonly byte[] _data;
        private readonly int _length;
        private int _bitPos;

        public BitReader(byte[] data, int length, int startByte = 0)
        {
            _data = data;
            _length = length;
            _bitPos = startByte * 8;
        }

        public bool Overrun { get; private set; }

        public int ReadBit()
        {
            var byteIndex = _bitPos >> 3;
            if (byteIndex >= _length)
            {
                Overrun = true;
                return 0;
            }

            var bit = (_data[byteIndex] >> (7 - (_bitPos & 7))) & 1;
            _bitPos++;
            return bit;
        }

        public uint ReadBits(int count)
        {
            uint value = 0;
            for (var i = 0; i < count; i++)
                value = (value << 1) | (uint)ReadBit();
            return value;
        }

        public void SkipBits(int count)
        {
            for (var i = 0; i < count && !Overrun; i++)
                ReadBit();
        }

        // Unsigned Exp-Golomb. Bounded at 32 leading zeros so a run of zero bytes can't spin.
        public uint ReadUe()
        {
            var leadingZeros = 0;
            while (ReadBit() == 0)
            {
                if (Overrun) return 0;
                if (++leadingZeros >= 32)
                {
                    Overrun = true;
                    return 0;
                }
            }

            if (leadingZeros == 0) return 0;
            return (uint)((1 << leadingZeros) - 1) + ReadBits(leadingZeros);
        }

        public int ReadSe()
        {
            var value = ReadUe();
            if (value == 0) return 0;
            var magnitude = (int)((value + 1) / 2);
            return (value & 1) == 1 ? magnitude : -magnitude;
        }
    }
}
