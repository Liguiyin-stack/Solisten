#!/usr/bin/env sh
# 下载 FFmpeg 8.1 LGPL 共享库（只保留音频需要的 4 个 DLL）到 external/ffmpeg/x64。
set -eu
cd "$(dirname "$0")/.."
url="https://github.com/BtbN/FFmpeg-Builds/releases/download/latest/ffmpeg-n8.1-latest-win64-lgpl-shared-8.1.zip"
tmp="$(mktemp -d)"
curl -sSL -o "$tmp/ff.zip" "$url"
unzip -q "$tmp/ff.zip" -d "$tmp/x"
mkdir -p external/ffmpeg/x64
for dll in avcodec-62 avformat-62 avutil-60 swresample-6; do
  cp "$tmp"/x/*/bin/$dll.dll external/ffmpeg/x64/
done
cp "$tmp"/x/*/LICENSE.txt external/ffmpeg/
rm -rf "$tmp"
echo "FFmpeg DLLs in external/ffmpeg/x64"
