namespace Emby.FastIptv.LiveTv.Probe
{
    // CRC-32/MPEG-2 as used by MPEG-TS PSI sections. Running it over the section including its
    // trailing CRC yields 0, which is how a section is validated. Cheap insurance against
    // describing a channel from a corrupted PAT/PMT.
    internal static class Crc32Mpeg
    {
        private static readonly uint[] Table = BuildTable();

        public static bool IsSectionValid(byte[] section, int length)
        {
            if (length < 12 || length > section.Length) return false;

            var crc = 0xFFFFFFFFu;
            for (var i = 0; i < length; i++)
                crc = (crc << 8) ^ Table[((crc >> 24) ^ section[i]) & 0xFF];

            return crc == 0;
        }

        private static uint[] BuildTable()
        {
            var table = new uint[256];
            for (var i = 0u; i < 256u; i++)
            {
                var crc = i << 24;
                for (var bit = 0; bit < 8; bit++)
                    crc = (crc & 0x80000000u) != 0 ? (crc << 1) ^ 0x04C11DB7u : crc << 1;
                table[i] = crc;
            }

            return table;
        }
    }
}
