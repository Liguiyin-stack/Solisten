<p align="center">
  <img src="docs/images/icon.png" width="128" alt="Solisten 图标" />
</p>

<h1 align="center">Solisten</h1>

<p align="center">一首歌，一个空间。Windows 原生的音乐播放器。</p>

Solisten 把正在播放的那首歌放在画面中央：封面随音乐呼吸、起伏，色彩和光粒子从封面里流出来；
右侧是整屏滚动的歌词。本地音乐完全离线播放，也可以登录网易云音乐，同步歌单、在线播放。

## 下载安装

到 [Releases](https://github.com/Liguiyin-stack/Solisten/releases) 下载最新的 `Solisten-Setup-<版本>-x64.exe`，双击安装。

- 系统要求：Windows 10 1903（19041）或更高，x64。
- 只为当前用户安装，不需要管理员权限，默认装到 `%LOCALAPPDATA%\Programs\Solisten`。
- 安装包自带 .NET 运行时、Windows App SDK 和 FFmpeg，不需要另外安装任何东西。
- 安装程序没有代码签名，首次运行时 Windows SmartScreen 可能提示"已保护你的电脑"，点"更多信息 → 仍要运行"即可。
- 卸载：在"设置 → 应用"里卸载，可以选择是否同时删除曲库数据（音乐文件本身不会被删除）。

## 功能

- **聆听空间**：封面随低频呼吸、随重音扩散，背景色场取自封面主色；安静 / 标准 / 沉浸三档（按 `I` 切换）。
- **本地曲库**：按文件夹、专辑或聆听记录浏览；支持 FLAC、ALAC、MP3、AAC/M4A、OGG、Opus、WAV、AIFF；采样级无缝播放；按标题、艺术家、专辑或拼音搜索。
- **歌词**：读取同名 `.lrc` 文件、文件内嵌歌词或网易云歌词（含翻译）；类似 Apple Music 的整屏歌词，当前行清晰、其余行按距离淡出和模糊，点击一句跳到那里。
- **网易云音乐**（可选）：扫码登录，同步自建和收藏的歌单，自动匹配本地文件；本地没有的歌在线播放。
- **个人痕迹**：收藏、标签、备注、时间标记，只保存在 Solisten 自己的数据库里，从不写回音频文件。

### 网易云音乐说明

- 这是**非官方集成**，使用网易云音乐客户端的接口，接口可能随时变化或触发风控。
- 能否播放、播放哪个版本由网易云按你的账号决定：没有完整权限的歌只能播放网易云提供的试听片段。不使用任何第三方音源，不绕过会员或版权限制。
- 在线音频只流式播放，不保存到本机；只缓存封面、歌词和音频特征分析结果。
- 登录信息保存在本机的 Windows 凭据管理器中；**未登录时 Solisten 不会发出任何网络请求**。

## 操作

| 操作 | 键盘 | 鼠标 |
|---|---|---|
| 播放 / 暂停 | `Space` | 控制栏播放按钮 |
| 上一首 / 下一首 | `Ctrl+←` / `Ctrl+→` | 控制栏按钮 |
| 快退 / 快进 5 秒 | `Shift+←` / `Shift+→` | 点击或拖动进度线 |
| 打开目录 | `↑` 或 `←` `→` | 控制栏目录按钮 |
| 目录里选歌 / 播放 | `←` `→` 选择，`↑` `↓` 切换分组，`Enter` 播放 | 单击选中，再单击或双击播放，右键更多 |
| 回到正在播放 | `.` | 目录右上角按钮 |
| 搜索 | `/` 或 `Ctrl+F` | 目录里的搜索框 |
| 资料（标签、备注、标记） | `↓` | 控制栏资料按钮 |
| 显示 / 隐藏歌词 | `L` | 控制栏歌词按钮 |
| 打标记 | `M` | — |
| 单曲循环 / 范围内随机 | `R` / `S` | 控制栏按钮 |
| 切换聆听空间模式 | `I` | 设置 |
| 设置 | `Ctrl+,` | 控制栏设置按钮 |
| 关闭当前面板 | `Esc` | 点画面空白处，或鼠标侧键"后退" |
| 全屏 | `F11` | 双击画面空白处 |

控制栏、进度线和时间在没有操作 2.5 秒后自动隐藏，移动鼠标即可唤出。

## 从源码构建

需要 .NET SDK 10。不需要 Visual Studio。

```sh
./scripts/fetch-ffmpeg.sh          # 下载 FFmpeg LGPL 共享库到 external/ffmpeg/x64
dotnet test                        # 运行全部测试
dotnet build src/Solisten.App      # 生成 src/Solisten.App/bin/x64/Debug/.../Solisten.exe
```

打包安装程序（需要 [NSIS 3](https://nsis.sourceforge.io/)，`makensis` 在 PATH 中，或设置 `MAKENSIS=<路径>`）：

```sh
./scripts/build-installer.sh 1.0.0 # 生成 artifacts/Solisten-Setup-1.0.0-x64.exe
```

重新生成图标：`dotnet run --project scripts/icon -- src/Solisten.App/Assets`。

运行时可以把音乐文件夹作为参数传入（会加入曲库）。环境变量：

- `SOLISTEN_DATA_DIR`：使用独立的数据目录（开发和测试用），默认 `%LOCALAPPDATA%\Solisten`；
- `SOLISTEN_REMOTE_PROVIDER`：`off` 关闭网易云集成，`fake` 使用本机演示数据。

## 结构

| 项目 | 内容 |
|---|---|
| `Solisten.Core` | 纯 .NET：状态模型、Reducer、播放规则、排序规则、远程曲目与歌词模型 |
| `Solisten.Audio` | FFmpeg 解码（本地文件与在线流）、WASAPI 输出、无缝播放引擎、音频时钟 |
| `Solisten.Library` | 扫描、元数据、封面、波形 / 主色 / 音频特征分析（含边播边分析）、SQLite、文件监听、歌词解析、远程歌单匹配 |
| `Solisten.Rendering` | 聆听空间（光粒子封面、色彩空间、柔光玻璃）、进度线 |
| `Solisten.Integrations.NetEase` | 网易云音乐：扫码登录、歌单同步、在线播放地址、歌词 |
| `Solisten.App` | WinUI 3 窗口、三层界面、整屏歌词、设置、输入、SMTC |

设计文档：[docs/design-windows.md](docs/design-windows.md)、[docs/design-netease-sync.md](docs/design-netease-sync.md)；
实施记录：[docs/implementation-plan.md](docs/implementation-plan.md)。

## 第三方

FFmpeg（LGPL 2.1+，动态链接）、TagLib#（LGPL 2.1）、Win2D、Windows App SDK、CommunityToolkit.Mvvm、
Microsoft.Data.Sqlite / SQLite、Dapper、NAudio.Wasapi、QRCoder，拼音数据来自 mozillazg/pinyin-data（MIT）。
网易云音乐接口与加密方式参考社区项目 NeteaseCloudMusicApi。Solisten 与网易云音乐没有任何关联。
