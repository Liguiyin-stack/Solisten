# Solisten 网易云音乐扫码登录与歌曲同步设计文档

> 版本：v0.1（方案设计）  
> 日期：2026-10-21  
> 状态：待确认，不包含实现代码  
> 目标：在不破坏 Solisten“本地优先、一首歌一个空间”核心体验的前提下，增加网易云音乐账号登录、歌单同步和本地歌曲匹配能力。

---

## 1. 结论先行

**可以做，但必须区分两种能力：**

### 1.1 推荐首期做：同步“歌单与歌曲元数据”

扫码登录网易云后，将用户的歌单、歌曲标题、歌手、专辑、时长、网易云歌曲 ID 同步到 Solisten；然后把这些远程歌曲匹配到用户电脑上的本地音频文件。

- 能在 Solisten 中看到网易云歌单；
- 能知道哪些歌曲已经存在本地；
- 本地匹配成功后，继续使用 Solisten 现有 FFmpeg/WASAPI 播放；
- 不下载网易云音频，不绕过会员、试听或 DRM 限制；
- 不把远程歌曲伪装成已经存在的本地文件。

这是与当前项目架构最兼容、风险最低的方案。

### 1.2 不建议首期做：直接播放网易云云端歌曲

“同步用户歌曲”如果指的是把云端音乐直接下载或播放，技术上会涉及：音频 URL 授权、会员权限、试听片段、版权限制、接口风控、临时 URL 过期和服务条款。当前 Solisten 的音频引擎假设输入是本地文件路径，因此这不是简单加一个 API 调用就能完成的功能。

云端播放应作为独立项目阶段，只有在拥有合法、稳定、明确授权的官方接口时才考虑。

---

## 2. 重要的接口与合规判断

目前网上常见的二维码登录流程主要来自社区维护的非官方网易云接口封装，而不是 Solisten 可以直接假设长期稳定的官方 OAuth SDK。社区实现通常包含：生成二维码 key、生成二维码、轮询扫码状态、登录成功后保存 Cookie；常见状态码为 800（过期）、801（等待扫码）、802（已扫码待确认）、803（登录成功）。这些接口和状态码可以用于原型验证，但不能视为网易云官方稳定承诺。

因此必须采用两层策略：

1. **产品发布版**：优先接入网易云官方授权/开放平台能力；如果没有可申请、可公开使用的官方授权能力，则不把该功能作为正式发布能力。
2. **个人原型版**：可以通过可替换的 Provider Adapter 对接社区接口，但需要明确标注“非官方集成”，并把它限制在用户本机，不能把 Cookie 上传到 Solisten 的服务器。

社区项目本身也存在维护状态、登录失效、接口风控和二维码状态异常等历史问题，因此不能把它直接硬编码到 UI 或 Core 层。相关社区资料显示，二维码接口会遇到长期等待、扫码后 502、登录刷新不兼容等问题。见：

- Binaryify/NeteaseCloudMusicApi 的二维码登录问题记录；
- 社区 SDK 对二维码状态码和用户歌单接口的整理。

这些资料只能作为原型实现参考，不构成官方接口保证。

---

## 3. 与当前 Solisten 项目的关系

当前项目实际技术栈：

| 层 | 当前实现 | 网易云同步中的职责 |
|---|---|---|
| UI | WinUI 3 + Windows App SDK + XAML + C# | 登录页、二维码、同步进度、歌单来源 |
| 应用层 | `Solisten.App` | 编排登录、同步、通知和取消操作 |
| Core | `Solisten.Core`，纯 .NET 状态模型与 Reducer | 保持播放状态纯本地；增加远程来源模型时不污染播放规则 |
| 音频 | FFmpeg.AutoGen + WASAPI/NAudio | 只播放本地匹配成功的文件 |
| 曲库 | `Solisten.Library` + TagLib# + SQLite/Dapper | 保存本地曲库和远程歌单索引、匹配结果 |
| 渲染 | Win2D / Composition | 不需要为同步功能增加特殊渲染逻辑 |
| 安全存储 | 建议 Windows PasswordVault 或 DPAPI | 保存登录会话，不保存明文 Cookie |
| 网络 | 建议 `HttpClientFactory` 或受控 `HttpClient` | 请求、超时、重试、取消、限速、错误分类 |

项目当前 README 明确将 Solisten 定位为纯离线本地播放器，设计文档也明确“不联网”。因此这次功能不是普通的小功能，而是一次产品边界变化：建议做成**可选集成模块**，默认关闭，未登录时完全不产生网络请求。

---

## 4. 总体架构

```text
┌───────────────────────────────────────────┐
│ Solisten.App / WinUI 3                    │
│  Settings > Integrations > 网易云音乐      │
│  QR Login Dialog / Sync Progress / Library │
└──────────────────────┬────────────────────┘
                       │ commands + events
┌──────────────────────▼────────────────────┐
│ Solisten.Integrations.NetEase              │
│  NetEaseAuthService                        │
│  NetEaseLibraryClient                      │
│  NetEaseSyncService                        │
│  TrackMatcher                              │
│  SyncRateLimiter                            │
└──────────────┬───────────────┬────────────┘
               │               │
        ┌──────▼──────┐  ┌────▼─────────────┐
        │ Provider API │  │ Secure Credential │
        │ Adapter      │  │ Store             │
        │ official or  │  │ PasswordVault/    │
        │ prototype    │  │ DPAPI             │
        └──────┬──────┘  └──────────────────┘
               │
        HTTPS / local adapter only
               │
        网易云授权/接口

┌───────────────────────────────────────────┐
│ Solisten.Library / SQLite                 │
│ local tracks + remote playlists + mapping  │
└───────────────────────────────────────────┘
```

### 4.1 关键原则

- `Solisten.Core` 不直接依赖网易云、HTTP、Cookie 或 JSON。
- `PlaybackEngine` 不知道网易云存在。
- 远程曲目与本地 `Track` 分开建模。
- 同步是幂等任务，可取消，可重试，不阻塞 UI 线程。
- 远程数据只进入“来源/歌单/匹配”层，只有本地匹配成功的 Track 才能进入现有播放上下文。
- Provider Adapter 必须可替换，以便将来从非官方接口迁移到官方接口。

---

## 5. 建议的代码目录

```text
src/
├─ Solisten.Core/
│  └─ Model/
│     └─ RemoteMusic.cs                 # 只放无网络依赖的数据模型
├─ Solisten.Library/
│  ├─ Remote/
│  │  ├─ RemoteMusicDatabase.cs         # 远程歌单与映射的 SQLite 读写
│  │  ├─ TrackMatcher.cs                 # 远程曲目 ↔ 本地 Track 匹配
│  │  └─ SyncModels.cs
│  └─ Storage/
│     └─ LibraryDatabase.cs              # schema migration
├─ Solisten.Integrations.NetEase/
│  ├─ Solisten.Integrations.NetEase.csproj
│  ├─ Api/
│  │  ├─ IMusicProvider.cs
│  │  ├─ NetEaseProvider.cs
│  │  ├─ NetEaseDtos.cs
│  │  └─ NetEaseApiException.cs
│  ├─ Auth/
│  │  ├─ NetEaseQrLoginService.cs
│  │  ├─ LoginState.cs
│  │  └─ ICredentialStore.cs
│  └─ Sync/
│     ├─ NetEaseSyncService.cs
│     ├─ SyncProgress.cs
│     └─ SyncPolicy.cs
└─ Solisten.App/
   ├─ Integrations/
   │  ├─ NetEaseLoginDialog.xaml
   │  ├─ NetEaseLoginDialog.xaml.cs
   │  ├─ NetEaseIntegrationViewModel.cs
   │  └─ NetEaseSyncView.xaml
   └─ Services/
      └─ AppHost.cs                    # 编排服务，不实现 API 细节
```

如果不希望新增项目，第一版可以把 `Solisten.Integrations.NetEase` 放进 `Solisten.Library/Remote`，但长期建议独立项目，避免本地曲库层直接依赖第三方服务。

---

## 6. 数据模型设计

### 6.1 Core 模型

不要把远程歌曲直接塞进当前的 `Track`，因为 `Track.Path` 是播放引擎的本地文件入口。建议新增：

```csharp
public enum RemoteProvider
{
    NetEase
}

public readonly record struct RemoteTrackId(
    RemoteProvider Provider,
    string Value);

public sealed record RemoteTrack(
    RemoteTrackId Id,
    string Title,
    string Artist,
    string Album,
    TimeSpan Duration,
    string? Isrc,
    string? CoverUrl,
    bool IsPlayableOnline,
    DateTimeOffset UpdatedAt);

public sealed record RemotePlaylist(
    RemoteProvider Provider,
    string Id,
    string Name,
    string? Description,
    string? CoverUrl,
    bool IsOwned,
    int TrackCount,
    DateTimeOffset UpdatedAt);

public enum TrackMatchStatus
{
    Unmatched,
    Matched,
    Ambiguous,
    LocalFileMissing,
    MetadataConflict
}

public sealed record RemoteTrackMapping(
    RemoteTrackId Remote,
    TrackId? LocalTrack,
    TrackMatchStatus Status,
    double Confidence,
    string? Reason);
```

`CoverUrl` 只作为远程元数据，默认不要下载。只有用户明确打开“下载远程封面”并且接口允许时才执行；更推荐使用本地文件封面。

### 6.2 SQLite 表

建议新增 schema version 3：

```sql
CREATE TABLE remote_accounts (
    id INTEGER PRIMARY KEY,
    provider TEXT NOT NULL,
    remote_user_id TEXT NOT NULL,
    nickname TEXT,
    avatar_url TEXT,
    last_sync_at TEXT,
    created_at TEXT NOT NULL,
    updated_at TEXT NOT NULL,
    UNIQUE(provider, remote_user_id)
);

CREATE TABLE remote_playlists (
    provider TEXT NOT NULL,
    playlist_id TEXT NOT NULL,
    account_id INTEGER NOT NULL REFERENCES remote_accounts(id) ON DELETE CASCADE,
    name TEXT NOT NULL,
    description TEXT,
    cover_url TEXT,
    is_owned INTEGER NOT NULL,
    track_count INTEGER NOT NULL,
    remote_updated_at TEXT,
    synced_at TEXT NOT NULL,
    PRIMARY KEY(provider, playlist_id)
);

CREATE TABLE remote_tracks (
    provider TEXT NOT NULL,
    track_id TEXT NOT NULL,
    title TEXT NOT NULL,
    artist TEXT NOT NULL,
    album TEXT NOT NULL,
    duration_ms INTEGER NOT NULL,
    isrc TEXT,
    cover_url TEXT,
    raw_hash TEXT NOT NULL,
    synced_at TEXT NOT NULL,
    PRIMARY KEY(provider, track_id)
);

CREATE TABLE remote_playlist_items (
    provider TEXT NOT NULL,
    playlist_id TEXT NOT NULL,
    track_id TEXT NOT NULL,
    position INTEGER NOT NULL,
    added_at TEXT,
    PRIMARY KEY(provider, playlist_id, track_id),
    FOREIGN KEY(provider, playlist_id) REFERENCES remote_playlists(provider, playlist_id) ON DELETE CASCADE,
    FOREIGN KEY(provider, track_id) REFERENCES remote_tracks(provider, track_id) ON DELETE CASCADE
);

CREATE TABLE remote_track_mappings (
    provider TEXT NOT NULL,
    track_id TEXT NOT NULL,
    local_track_id INTEGER,
    status TEXT NOT NULL,
    confidence REAL NOT NULL,
    reason TEXT,
    updated_at TEXT NOT NULL,
    PRIMARY KEY(provider, track_id),
    FOREIGN KEY(local_track_id) REFERENCES tracks(id) ON DELETE SET NULL
);

CREATE TABLE sync_runs (
    id INTEGER PRIMARY KEY,
    provider TEXT NOT NULL,
    account_id INTEGER,
    started_at TEXT NOT NULL,
    finished_at TEXT,
    status TEXT NOT NULL,
    playlists_seen INTEGER NOT NULL DEFAULT 0,
    tracks_seen INTEGER NOT NULL DEFAULT 0,
    matched INTEGER NOT NULL DEFAULT 0,
    errors INTEGER NOT NULL DEFAULT 0,
    error_message TEXT
);
```

注意：Cookie、登录 key、二维码内容、认证响应原文、可复用会话票据都不进入 SQLite。

---

## 7. 二维码登录流程

### 7.1 用户流程

1. 用户打开“设置 → 集成 → 网易云音乐”。
2. 点击“扫码登录”。
3. Solisten 请求一次性 QR key。
4. Solisten 请求二维码内容或二维码图片。
5. 弹出二维码对话框，显示：
   - 二维码；
   - “请使用网易云音乐 App 扫码”；
   - 当前状态；
   - 重新生成；
   - 取消。
6. 后台每 1.5–2.5 秒轮询一次状态。
7. 800：二维码过期，显示“二维码已过期，点击刷新”。
8. 801：等待扫码。
9. 802：已扫码，等待手机确认。
10. 803：登录成功，立即调用登录状态接口验证账号。
11. 将认证材料交给安全存储；关闭二维码；显示昵称和账号摘要。
12. 首次登录询问“是否立即同步歌单”。

### 7.2 服务接口

```csharp
public interface INeteaseQrLoginService
{
    Task<QrLoginSession> CreateAsync(CancellationToken ct);
    Task<QrLoginStatus> PollAsync(QrLoginSession session, CancellationToken ct);
    Task<RemoteAccount> ValidateAsync(CancellationToken ct);
    Task LogoutAsync(CancellationToken ct);
}

public sealed record QrLoginSession(
    string SessionId,
    string ProviderKey,
    DateTimeOffset ExpiresAt,
    Uri? QrUrl,
    byte[]? QrPng);

public enum QrLoginState
{
    WaitingForScan,
    WaitingForConfirmation,
    Succeeded,
    Expired,
    Failed,
    Cancelled
}
```

### 7.3 轮询约束

- 每个会话只能有一个轮询循环；
- 页面关闭、取消、退出登录时必须取消轮询；
- 不能指数重试二维码状态检查到几分钟不间断；
- 网络错误采用有限重试，例如 3 次，随后转为可重试失败；
- 只在登录状态发生变化时通知 UI，避免每次 801 都刷新整棵界面；
- 不记录响应 Cookie 到日志。

### 7.4 安全存储

Windows 版建议使用：

- `Windows.Security.Credentials.PasswordVault`：存储账号对应的会话字符串；或
- Windows DPAPI / Credential Manager：存储加密后的会话数据。

不得使用：

- `settings.json` 明文保存 Cookie；
- SQLite 明文保存 Cookie；
- URL query string 保存 Cookie；
- 日志、异常消息、遥测中写入 Cookie；
- 把 Cookie 复制到剪贴板；
- 让 UI 层直接持有长期会话票据。

退出登录的语义必须是：删除本地会话 + 尝试通知远端注销 + 清空内存中的认证对象 + 让远程内容保持但标记为“未连接”，或由用户选择删除。

---

## 8. 歌单同步流程

```text
StartSync
  ├─ LoadCredential
  ├─ ValidateSession
  ├─ FetchUserPlaylists (paged)
  ├─ UpsertPlaylists
  ├─ For each selected playlist
  │    ├─ FetchPlaylistTracks (paged)
  │    ├─ UpsertRemoteTracks
  │    └─ UpsertPlaylistItems
  ├─ MatchRemoteTracksToLocalLibrary
  ├─ Recalculate local playlist contexts
  └─ Publish SyncCompleted / SyncFailed
```

### 8.1 同步策略

默认只同步：

- 用户创建的歌单；
- 用户明确勾选的收藏歌单；
- 用户明确点击“同步”的歌单。

不要默认同步所有订阅歌单、播放记录、评论、动态和私人信息。

### 8.2 分页、限速、取消

- 所有列表 API 必须分页；
- 单次请求设置连接和总超时；
- 限制并发，建议默认 1–2 个请求；
- 遇到 429、网关错误、服务端风控时采用有限退避；
- `CancellationToken` 从 UI 一直传到 HttpClient；
- 同步结果必须支持“部分成功”；
- 同步不能清空旧数据后再重新写入，采用增量 Upsert；
- 每次同步写入 `sync_runs`，便于显示历史和诊断。

### 8.3 数据变化处理

- 远程歌单仍存在：更新名称、描述、曲目顺序；
- 远程歌单被删除：标记远程删除，不立即删除本地历史，避免误删用户数据；
- 远程歌曲从歌单移除：只删除 `remote_playlist_items` 关系；
- 远程歌曲信息变化：更新元数据，但保留本地映射，重新计算置信度；
- 本地文件被移动或删除：映射状态变为 `LocalFileMissing`，不删除远程歌曲。

---

## 9. 远程歌曲与本地文件匹配

### 9.1 匹配优先级

1. ISRC 精确匹配（如果双方都有且合法暴露）；
2. 清洗后的标题 + 主艺术家 + 时长差；
3. 标题 + 专辑 + 艺术家 + 时长差；
4. 文件名 + 艺术家 + 时长差；
5. 只满足标题/艺术家的候选只能标记为 `Ambiguous`，不能自动播放。

### 9.2 文本清洗

统一：

- 大小写；
- 全角半角；
- Unicode 规范化；
- 中文/英文括号；
- `DJ Version`、`Live`、`Remix` 等版本标识保留为影响匹配的字段；
- 艺术家分隔符；
- `feat.`、`&`、`、` 不应简单全部删除；
- 不要为了“提高命中率”删除歌曲版本信息。

### 9.3 时长阈值

建议：

- 差值 ≤ 2 秒：高置信；
- 差值 ≤ 5 秒：中置信，需要确认；
- 差值 > 5 秒：通常不自动匹配；
- 现场版、混音版、剪辑版使用人工选择。

### 9.4 UI 状态

每首远程歌曲显示：

- 已匹配：可在 Solisten 播放；
- 未匹配：只有云端信息，本地没有文件；
- 有多个候选：点击选择；
- 本地文件失效：重新定位文件；
- 元数据冲突：显示候选和差异。

### 9.5 播放边界

只有 `TrackMatchStatus.Matched` 的本地 Track 才能进入现有 `PlaybackContext`。未匹配远程曲目不能调用 `PlayTrack`，而是显示轻提示：

> “这首歌已同步，但本机没有对应音频文件。请将文件加入曲库后重新匹配。”

这样可以保持 `PlaybackEngine` 的本地文件假设不变。

---

## 10. UI 设计

### 10.1 设置入口

在现有设置页增加“服务集成”区块：

```text
服务集成

网易云音乐                                  未连接
[扫码登录]

登录后可同步歌单和歌曲信息，匹配本地文件后播放。
Solisten 不下载云端音频，不上传本地音乐。
```

登录后：

```text
网易云音乐                                  已连接
头像  用户昵称
最后同步：今天 20:31
[立即同步] [选择歌单] [退出登录]
```

### 10.2 二维码对话框

二维码页应保持极简，不放歌单、播放器或复杂操作：

- 中央二维码；
- 状态文案；
- 过期倒计时；
- 重新生成；
- 取消。

二维码图片不能进入动态封面主视觉，不要把登录流程做成装饰性 3D 场景。

### 10.3 歌单来源

在导航层增加“来源”概念：

```text
本地曲库
  文件夹
  专辑
  聆听记录

网易云音乐
  我喜欢的音乐
  通勤
  夜晚
```

远程来源只显示为导航上下文，不改变当前播放锚点。用户浏览网易云歌单时，当前正在播放的本地歌曲仍保持不变；只有明确确认一个已匹配的本地歌曲才切歌。

### 10.4 同步反馈

同步期间显示轻量状态，而不是阻塞全屏：

```text
正在同步网易云音乐
歌单 3 / 8 · 歌曲 126 / 420
已匹配 87 首 · 待匹配 39 首
[后台运行] [取消]
```

完成后显示摘要：

```text
同步完成
8 个歌单 · 420 首歌曲 · 87 首已匹配本地文件
[查看未匹配歌曲]
```

---

## 11. Core 与 AppState 处理

不建议把登录 Cookie、同步进度、当前远程歌曲直接放到 `AppState`。原因是：

- Cookie 属于安全凭据；
- 同步进度是后台任务状态；
- 当前歌词一样属于高频派生状态；
- 播放状态必须保持稳定，不应因网络请求重建播放上下文。

建议：

- `AppState` 继续只描述播放、浏览、画布和本地曲库；
- `NetEaseIntegrationViewModel` 管理登录 UI 和同步 UI；
- `RemoteMusicDatabase` 管理持久化；
- `NetEaseSyncService` 通过事件或 `IProgress<SyncProgress>` 通知界面；
- 远程歌单导航在生成播放上下文时转换为“已匹配 TrackId 的投影”。

示意：

```csharp
public interface IRemoteLibrarySource
{
    Task<IReadOnlyList<RemotePlaylist>> GetPlaylistsAsync(CancellationToken ct);
    Task<IReadOnlyList<RemoteTrack>> GetTracksAsync(string playlistId, CancellationToken ct);
    Task<SyncSummary> SyncAsync(SyncOptions options, IProgress<SyncProgress>? progress, CancellationToken ct);
}
```

---

## 12. 失败与恢复

| 场景 | 用户看到的结果 | 内部处理 |
|---|---|---|
| 二维码过期 | “二维码已过期” | 丢弃 QR session，允许重建 |
| 用户取消 | 关闭对话框 | 取消轮询，不写入凭据 |
| 扫码成功但状态验证失败 | “已扫码，但无法确认登录状态” | 不保存未验证会话，记录脱敏错误 |
| 登录态失效 | “请重新登录” | 保留远程缓存，暂停同步 |
| 请求超时 | “网络请求超时” | 有限重试，允许再次同步 |
| 被限速/风控 | “服务暂时限制请求” | 停止快速重试，显示稍后再试 |
| 部分歌单失败 | “同步部分完成” | 保留已成功的 Upsert，记录失败歌单 |
| 本地文件不存在 | “未匹配” | 远程条目保留，不进入播放上下文 |
| 多个本地候选 | “需要确认” | 禁止自动播放，提供选择 |
| 服务接口变更 | “网易云集成暂不可用” | Provider 返回结构化错误，不让 UI 崩溃 |

---

## 13. 安全、隐私与合规要求

必须在设置页明确说明：

- 登录信息只用于访问用户主动选择的网易云数据；
- Solisten 不上传本地音频文件；
- Solisten 不向第三方服务器发送本地曲库扫描结果；
- 默认只同步歌单和歌曲元数据；
- 会话凭据保存在本机安全存储；
- 用户可以随时退出登录并删除缓存；
- 远程歌曲是否可播放取决于本地是否存在合法音频文件。

工程要求：

- HTTPS 证书错误不能被忽略；
- 认证 header 和 Cookie 必须进入日志脱敏器；
- 统一日志中禁止记录二维码原文、登录 key、Cookie、用户隐私字段；
- 网络请求设置明确 User-Agent，并允许 Provider 替换；
- 不抓取浏览器 Cookie；
- 不绕过验证码、风控、会员限制或 DRM；
- 不将非官方接口当成官方授权；
- 发布前核对网易云服务条款、接口授权和所使用第三方库许可证。

---

## 14. 测试设计

### 14.1 单元测试

- 二维码状态码 800/801/802/803 映射；
- 过期二维码不会触发同步；
- 登录成功但账号验证失败时不保存凭据；
- Cookie 脱敏；
- 标题/艺术家 Unicode 规范化；
- 时长阈值；
- 版本歌曲不误匹配；
- 同一远程歌曲重复同步保持幂等；
- 歌单删除不会删除本地 Track；
- 未匹配歌曲不会生成 `PlayTrack`；
- 取消同步不会留下半个运行状态；
- 断网恢复后可以从旧缓存继续浏览。

### 14.2 集成测试

使用本地假 HttpServer，不连接真实网易云：

- 分页歌单同步；
- 乱序响应；
- 429 和 5xx 有限重试；
- 部分歌单失败；
- 登录状态失效；
- API 返回未知字段；
- API 缺失字段时不崩溃；
- SQLite 迁移从 schema 2 到 schema 3；
- 并发点击“立即同步”只允许运行一个任务。

### 14.3 手动测试

- 未登录启动时完全离线；
- 登录二维码能刷新和取消；
- 登录窗口关闭后轮询停止；
- 同步时音乐继续播放；
- 同步期间切换当前歌曲不受影响；
- 远程歌单中的未匹配歌曲不会误播放；
- 退出登录后重新进入应用不会显示已连接；
- 删除本地文件后显示“本地文件缺失”；
- DPI 125%/150%/200%；
- 高对比度与减少动画；
- 断网、睡眠唤醒、系统时间变化。

---

## 15. 分阶段实施计划

### P0：Provider 抽象与本地假数据

1. 新增 Core 远程模型；
2. 新增 SQLite 表和迁移；
3. 实现 `IMusicProvider`；
4. 用假 HttpServer 产生歌单和歌曲；
5. 实现本地匹配；
6. 在导航层显示远程歌单投影；
7. 不加入真实登录。

验收：远程歌曲能同步到数据库，匹配到本地歌曲后可以进入现有播放流程。

### P1：二维码登录原型

1. 实现 `NetEaseQrLoginService`；
2. 实现二维码显示、轮询、取消、过期；
3. 接入本机安全存储；
4. 加入“非官方适配器”显式开关；
5. 日志脱敏和网络失败测试。

验收：扫码成功后只保存经登录状态验证的会话，不影响播放。

### P2：真实歌单同步

1. 获取账号歌单；
2. 获取所选歌单详情；
3. 分页、限速、缓存、失败恢复；
4. 匹配本地文件；
5. 增加未匹配和歧义处理界面。

验收：至少能可靠同步一组歌单，并在断网后浏览上次缓存。

### P3：产品化

1. 官方授权接口评估；
2. 服务条款和隐私说明；
3. 账号切换；
4. 多 Provider 预留；
5. 完整安装包和卸载清理；
6. 性能与安全审计。

---

## 16. 首期明确不做

- 不直接下载网易云音频；
- 不绕过会员、试听或 DRM；
- 不抓取浏览器登录 Cookie；
- 不同步评论、私信、动态、好友、播放历史等非必要隐私数据；
- 不上传本地音频和本地曲库路径；
- 不把远程歌曲强行写入本地 Track 表；
- 不让网络同步阻塞播放线程；
- 不让不稳定的社区 API 直接成为 Core 依赖；
- 不默认开启后台定时同步。

---

## 17. 推荐决策

**推荐现在先做 P0 + P1 的接口和 UI 骨架，但不要立即把真实非官方网易云接口写死。**

最稳妥的落地顺序是：

1. 先实现“远程歌单模型 + 本地匹配 + 假 Provider”；
2. 再实现二维码 UI 和安全凭据存储；
3. 最后在确认授权和接口稳定性后接入真实 Provider；
4. 首期同步歌单元数据，不做云端音频播放。

这样即使未来网易云接口变化，Solisten 的播放核心、本地曲库、视觉设计和歌词系统都不会被绑定或拖垮。
