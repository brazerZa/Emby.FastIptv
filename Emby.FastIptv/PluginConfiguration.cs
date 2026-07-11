using MediaBrowser.Model.Plugins;

namespace Emby.FastIptv
{
    public class PluginConfiguration : BasePluginConfiguration
    {
        public string DefaultVideoCodec { get; set; } = "h264";
        public string DefaultAudioCodec { get; set; } = "aac";
        public string DefaultContainer { get; set; } = "ts";
        public int DefaultWidth { get; set; } = 1920;
        public int DefaultHeight { get; set; } = 1080;

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
