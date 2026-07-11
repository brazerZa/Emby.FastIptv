namespace Emby.FastIptv.LiveTv
{
    public class M3uChannel
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string TvgId { get; set; }
        public string TvgLogo { get; set; }
        public string Group { get; set; }
        public string Url { get; set; }
        public int Number { get; set; }
        public bool IsRadio { get; set; }
    }
}
