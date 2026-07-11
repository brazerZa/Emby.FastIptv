# Emby.FastIptv

An [Emby Server](https://emby.media/) plugin that adds an M3U/IPTV tuner host,
including EPG (guide data) support via XMLTV.

## Features

- Add IPTV tuners backed by any M3U/M3U8 playlist URL
- Per-tuner custom User-Agent and HTTP headers
- Per-tuner channel list caching with configurable TTL
- Radio channel detection (`radio="true"` / `type="radio"` EXTINF attributes)
- Stable channel IDs (prefers `tvg-id`, falls back to a sanitised channel name)
- HLS stream detection (`.m3u8`/`.m3u` → `container=hls`)
- Channel grouping in the Emby UI via `group-title`
- EPG / guide data import from a per-tuner XMLTV URL
- Stream reliability: configurable connect timeout, retry count, and an
  optional HEAD-request health probe before opening a stream

## Requirements

- An [Emby Server](https://emby.media/) installation (the plugin targets the
  `MediaBrowser.Common` / `MediaBrowser.Controller` / `MediaBrowser.Model` APIs
  shipped with Emby Server)
- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) to build

## Building

The project references Emby Server's assemblies directly from its install
location (`$(ProgramFiles)\Emby-Server\system\*.dll` by default), so Emby
Server must be installed on the build machine.

```bash
dotnet build Emby.FastIptv/Emby.FastIptv.csproj -c Release
```

This produces `Emby.FastIptv/bin/Release/net8.0/Emby.FastIptv.dll`.

If your Emby Server is installed somewhere other than the default
`Program Files` path, edit the `HintPath` entries in
`Emby.FastIptv/Emby.FastIptv.csproj` to point at your install's `system`
folder.

## Installing

1. Build the plugin (see above), or download a release build.
2. Copy `Emby.FastIptv.dll` to `C:\ProgramData\Emby-Server\plugins\Emby.FastIptv\`.
3. Restart the Emby Server service.
4. In Emby's dashboard, go to **Live TV → Tuner Devices → Add** and select
   **FastIptv** to configure a new tuner (M3U URL, User-Agent, headers, EPG
   URL, etc).
5. Hard-refresh your browser (`Ctrl+Shift+F5`) if the setup page doesn't show
   up right away, to clear cached HTML/JS.

## License

No license specified yet.
