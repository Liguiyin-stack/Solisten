# Solisten

一首歌，一个空间。Windows 原生聆听画布（WinUI 3 + WASAPI + FFmpeg + Win2D）。
设计见 [docs/design-windows.md](docs/design-windows.md)，实施计划与进度见 [docs/implementation-plan.md](docs/implementation-plan.md)。

## 构建

需要 .NET SDK 10。不需要 Visual Studio。

```sh
./scripts/fetch-ffmpeg.sh          # 下载 FFmpeg LGPL 共享库到 external/ffmpeg/x64
dotnet test                        # Core / Library / Audio / Rendering 测试
dotnet build src/Solisten.App      # 生成 src/Solisten.App/bin/x64/Debug/.../Solisten.exe
```

运行时可以把音乐文件夹作为参数传入（会加入曲库）。设置环境变量 `SOLISTEN_DATA_DIR` 可以使用独立的数据目录，
默认是 `%LOCALAPPDATA%\Solisten`。

## 结构

| 项目 | 内容 |
|---|---|
| `Solisten.Core` | 纯 .NET：状态模型、Reducer、播放规则、排序规则 |
| `Solisten.Audio` | FFmpeg 解码、WASAPI 输出、无缝播放引擎、音频时钟 |
| `Solisten.Library` | 扫描、元数据、封面、波形/主色分析、SQLite、文件监听 |
| `Solisten.Rendering` | 环境层、整曲时间层（L2）与当前聆听窗口（L3） |
| `Solisten.App` | WinUI 3 窗口、三层界面、输入、SMTC |

## 第三方

FFmpeg（LGPL 2.1+，动态链接）、TagLib#（LGPL）、Win2D、Windows App SDK、CommunityToolkit.Mvvm、
Microsoft.Data.Sqlite、Dapper、NAudio.Wasapi，拼音数据来自 mozillazg/pinyin-data（MIT）。
