#!/usr/bin/env sh
# 打包 Solisten 安装程序：生成自包含的 Release 版本，再用 NSIS 生成安装 exe。
# 用法：scripts/build-installer.sh [版本号]     需要 makensis（NSIS 3）在 PATH 中，或设置 MAKENSIS=<路径>
#
# 注意：不用 dotnet publish。WinUI 3 非打包应用发布时会漏掉编译后的 XAML（*.xbf），
# 安装后启动即报 "XAML parsing failed"。build 的输出本身已经是自包含的（含 .NET 运行时与 Windows App SDK），
# 直接拿它打包。
set -e
cd "$(dirname "$0")/.."
VERSION="${1:-1.0.0}"
BUILD="src/Solisten.App/bin/x64/Release/net10.0-windows10.0.22621.0/win-x64"
STAGE="artifacts/stage/win-x64"
OUT="artifacts/Solisten-Setup-$VERSION-x64.exe"
MAKENSIS="${MAKENSIS:-makensis}"

dotnet build src/Solisten.App -c Release -r win-x64 --self-contained true -p:Platform=x64 -p:Version="$VERSION"

# 刚生成的文件可能还在被杀毒软件扫描：删不掉就稍等重试
for i in 1 2 3 4 5; do rm -rf "$STAGE" 2>/dev/null && break; sleep 3; done
[ ! -e "$STAGE" ] || { echo "无法清理 $STAGE"; exit 1; }
mkdir -p "$STAGE"
cp -R "$BUILD"/. "$STAGE"/
find "$STAGE" -name "*.pdb" -delete

# 发布前检查：缺了这些，安装后不能运行
for f in Solisten.exe Solisten.pri App.xbf MainWindow.xbf SettingsPanel.xbf LyricsView.xbf coreclr.dll Assets/Solisten.ico ffmpeg/avcodec-62.dll; do
  [ -f "$STAGE/$f" ] || { echo "缺少 $f"; exit 1; }
done

SIZEKB=$(du -sk "$STAGE" | cut -f1)
win() { cygpath -w "$1" 2>/dev/null || echo "$1"; }
"$MAKENSIS" -V2 -INPUTCHARSET UTF8 -DVERSION="$VERSION" -DSOURCE="$(win "$PWD/$STAGE")" -DOUTFILE="$(win "$PWD/$OUT")" -DSIZEKB="$SIZEKB" "$(win "$PWD/installer/Solisten.nsi")"
echo "安装程序：$OUT"
