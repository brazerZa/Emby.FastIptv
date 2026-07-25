namespace Emby.FastIptv.LiveTv.Probe
{
    internal enum ProbedStreamKind
    {
        Video,
        Audio,
        Subtitle
    }

    // Container-agnostic description of one elementary stream, deliberately free of any
    // MediaBrowser types so the parsers can be compiled and tested on their own.
    internal sealed class ProbedStream
    {
        // Must equal the index ffmpeg will give this stream, because Emby turns it into
        // "-map 0:<Index>". For MPEG-TS that is the position in the PMT's ES loop.
        public int Index { get; set; }
        public int Pid { get; set; }
        public ProbedStreamKind Kind { get; set; }
        public string Codec { get; set; }
        public string Profile { get; set; }
        public double? Level { get; set; }
        public int? Width { get; set; }
        public int? Height { get; set; }
        public int? BitDepth { get; set; }
        public bool IsInterlaced { get; set; }
        public int? Channels { get; set; }
        public int? SampleRate { get; set; }
        public string Language { get; set; }
    }
}
