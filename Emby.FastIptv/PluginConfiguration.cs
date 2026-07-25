using MediaBrowser.Model.Plugins;

namespace Emby.FastIptv
{
    public class PluginConfiguration : BasePluginConfiguration
    {
        // Container is always advertised — Emby needs it to pick the right ffmpeg demuxer.
        public string DefaultContainer { get; set; } = "ts";

        // When false (default) the plugin advertises no video/audio stream details and lets
        // Emby probe the opened stream for the real codecs and channel count. Guessing here
        // is what breaks HEVC/4K channels: Emby hands them to clients as h264, the client
        // cannot decode what it actually receives, and you get audio with black video.
        // Resolution is never advertised — the stream's native resolution always applies.
        // Only enable this if a client needs codec metadata up front.
        public bool AdvertiseStreamMetadata { get; set; } = false;

        // Used only when AdvertiseStreamMetadata is true.
        public string DefaultVideoCodec { get; set; } = "h264";
        public string DefaultAudioCodec { get; set; } = "aac";

        // The fast probe is not optional: it reads the real codecs straight from the MPEG-TS
        // PAT/PMT and the video parameter set, and per channel it already falls back to Emby's own
        // probe whenever it cannot be certain. A global "let Emby probe everything" switch would
        // only be a slower way to get the same answer, so there isn't one.

        // Hard deadline for one probe. Exceeding it costs nothing but a fallback to Emby's probe.
        public int ProbeTimeoutSeconds { get; set; } = 4;

        // Read budget per probe. Codecs are known within the first packets; only the resolution
        // needs a key frame, which on a long-GOP feed can be a few megabytes in. The probe stops
        // early once it has what it needs, so this is a ceiling rather than a cost.
        public int ProbeMaxKilobytes { get; set; } = 4096;

        // How long a probe result stays valid. Results survive restarts on disk; re-saving a
        // tuner clears them, which is the way out if a provider changes a channel's codec.
        public int ProbeCacheHours { get; set; } = 24;

        // Global fallback User-Agent for all tuners that don't specify their own.
        public string UserAgent { get; set; } = "";

        // Default channel-list cache duration in hours. Per-tuner override takes precedence.
        public int CacheTtlHours { get; set; } = 6;

        // Default stream connection timeout in seconds. Per-tuner override takes precedence.
        public int StreamTimeoutSeconds { get; set; } = 15;

        // Default number of retries on stream open failure. Per-tuner override takes precedence.
        public int StreamRetryCount { get; set; } = 2;

        // Per-tuner overrides (User-Agent, custom headers, cache TTL, stream quality).
        public TunerSettingsEntry[] TunerSettings { get; set; } = new TunerSettingsEntry[0];
    }
}
