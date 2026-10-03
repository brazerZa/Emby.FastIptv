#!/usr/bin/env bash
# Downloads a released Emby Server build from github.com/MediaBrowser/Emby.Releases and extracts
# its system/ assemblies, so the plugin is compiled and load-checked against the exact DLLs a
# real server runs (nuget.org only carries beta reference packages for 4.10).
#
# usage: tools/fetch-emby.sh <version> <dest dir>    e.g. tools/fetch-emby.sh 4.10.1.0 /tmp/emby
set -euo pipefail

version="$1"
dest="$2"
zip="embyserver-netframework_${version}.zip"

mkdir -p "$dest"
tmp="$(mktemp -d)"
trap 'rm -rf "$tmp"' EXIT

curl -fsSL --retry 3 -o "$tmp/$zip" \
  "https://github.com/MediaBrowser/Emby.Releases/releases/download/${version}/${zip}"
unzip -q -o -j "$tmp/$zip" 'system/*.dll' -x 'system/*/*' -d "$dest"

test -f "$dest/MediaBrowser.Controller.dll"
echo "Emby ${version} assemblies in ${dest}"
