namespace Emby.FastIptv
{
    public class TunerSettingsEntry
    {
        public string TunerId { get; set; }

        // Overrides the global User-Agent for this tuner's playlist and stream requests.
        public string UserAgent { get; set; }

        // Newline-separated "Name: Value" pairs applied to playlist and stream HTTP requests.
        public string CustomHeaders { get; set; }

        // Hours to cache the channel list. 0 = use the global default from PluginConfiguration.
        public int CacheTtlHours { get; set; }

        // Optional XMLTV EPG URL. When set, GetProgramsAsync fetches guide data from this URL.
        public string EpgUrl { get; set; }

        // Stream connection timeout in seconds. 0 = use the global default.
        public int StreamTimeoutSeconds { get; set; }

        // Number of retries on stream open failure. 0 = use the global default.
        public int StreamRetryCount { get; set; }

        // When true, a HEAD request is sent to the stream URL before opening to verify reachability.
        public bool EnableHealthProbe { get; set; }
    }
}
