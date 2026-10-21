using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Runtime.InteropServices;
using Solisten.App.Integrations;
using Solisten.Audio.Decoding;
using Solisten.Audio.Engine;
using Solisten.Audio.Output;
using Solisten.Core;
using Solisten.Core.Model;
using Solisten.Integrations.NetEase.Api;
using Solisten.Integrations.NetEase.Auth;
using Solisten.Library;
using Solisten.Library.Analysis;

namespace Solisten.App.Services;

/// <summary>
/// 宿主：组装 Store / 引擎 / 曲库，并执行 Reducer 返回的副作用。
/// 引擎与曲库事件转换为 Action 派发回 Store；Store 从不被引擎反向修改浏览状态。
/// </summary>
public sealed partial class AppHost : IEffectHandler, IDisposable
{
    private readonly Timer _deviceDebounce;
    private readonly AudioDeviceWatcher? _deviceWatcher;

    public AppHost()
    {
        // SOLISTEN_DATA_DIR 用于开发和测试，避免触碰真实的曲库数据。
        Paths = new AppPaths(Environment.GetEnvironmentVariable("SOLISTEN_DATA_DIR") is { Length: > 0 } dataDir ? dataDir : null);
        Settings = AppSettings.Load(Paths.Settings);
        Log = new FileLog(Paths.LogsDir);
        Library = new LibraryService(Paths, PcmReaderFactory.Default);
        Engine = new PlaybackEngine(src => new WasapiOutput(src, Settings.OutputDeviceId));
        Engine.Volume = VolumeCurve.FromSlider(Settings.Volume);
        // 边播边分析：在线歌曲第一次播放就有动画，播完一遍后保存特征（不保存音频）。
        LiveFeatures = new LiveFeatureRecorder(NeedsLiveFeatures);
        LiveFeatures.LiveUpdated += (id, f) =>
        {
            if (_liveLogged.Add(id)) Log.Write($"live features started {id}");
            LiveFeaturesUpdated?.Invoke(id, f);
        };
        LiveFeatures.Completed += OnLiveFeaturesCompleted;
        LiveFeatures.Failed += (id, e) => Log.Write($"live features failed {id}: {e.Message}");
        Engine.Tap = LiveFeatures;
        Store = new PlayerStore(AppState.Initial(Settings.ToCore()));
        Store.AddEffectHandler(this);

        Engine.TrackBoundaryReached += id => Store.Dispatch(new TrackBoundaryReached(id));
        Engine.TrackEnded += id => Store.Dispatch(new TrackEnded(id));
        Engine.PlaybackFailed += (id, reason) =>
        {
            Log.Write($"playback failed {id}: {Solisten.Integrations.NetEase.Diagnostics.Redactor.Redact(reason)}");
            // 在线播放地址可能已经过期（暂停很久之后）：换一个新地址，从当前位置重试一次。
            if (id.IsRemote && _remoteRetried != id && Store.State.Playing is { } p && p.Track == id)
            {
                _remoteRetried = id;
                LoadRemote(id, PositionOf(id).Position, paused: false, fresh: true);
                return;
            }
            Store.Dispatch(new PlaybackFailed(id, reason));
        };
        Engine.DeviceLost += reason =>
        {
            Log.Write($"device lost: {reason}");
            Store.Dispatch(new OutputDeviceLost());
        };

        Library.Changed += index => Store.Dispatch(new LibraryUpdated(WithOnline(index)));
        Library.Analysis.Analyzed += id => AnalysisReady?.Invoke(id);
        Library.Analysis.Failed += (id, e) => Log.Write($"analysis failed {id}: {e.Message}");
        NetEase = CreateNetEase();
        Lyrics = new LyricsService(NetEase);
        if (NetEase is { } netease)
        {
            netease.Online.TracksChanged += () => { if (_started) Store.Dispatch(new LibraryUpdated(WithOnline(Library.Index))); };
            // 导航层焦点在一首在线歌曲上停留 1.5 秒：预先请求播放地址，确认播放时不用再等网络。
            // 不要更短：快速翻歌时会连续发出很多请求，容易触发网易云的风控。
            _prefetch = new Timer(_ => { if (_prefetchTarget is { } t) netease.Online.Prefetch(t); });
            Store.StateChanged += (_, e) =>
            {
                var focus = e.Current.Layer == Layer.Navigate ? e.Current.Focus?.Track : null;
                if (focus == e.Previous.Focus?.Track && e.Previous.Layer == e.Current.Layer) return;
                _prefetchTarget = focus is { IsRemote: true } ? focus : null;
                if (_prefetchTarget is not null) _prefetch.Change(1500, Timeout.Infinite);
            };
        }

        // 默认设备变化：防抖后在新设备上重建输出，从当前位置继续（§5.6）。
        _deviceDebounce = new Timer(_ => Engine.ReopenOutput());
        try
        {
            _deviceWatcher = new AudioDeviceWatcher();
            // 只有"跟随系统默认设备"时才跟随默认设备变化；指定了设备时保持不动。
            _deviceWatcher.DefaultDeviceChanged += _ => { if (Settings.OutputDeviceId is null) _deviceDebounce.Change(300, Timeout.Infinite); };
        }
        catch (COMException e) { Log.Write($"device watcher unavailable: {e.Message}"); }
    }

    // ───────────────────────── 设置（§8.7） ─────────────────────────

    public void SetEndBehavior(EndBehavior behavior)
    {
        UpdateSettings(s => s with { EndBehavior = behavior });
        Store.Dispatch(new SetEndBehavior(behavior));
    }


    /// <summary>切换输出设备（null 表示跟随系统默认设备），在新设备上从当前位置继续。</summary>
    public void SetOutputDevice(string? deviceId)
    {
        if (Settings.OutputDeviceId == deviceId) return;
        UpdateSettings(s => s with { OutputDeviceId = deviceId });
        Engine.ReopenOutput();
    }

    public static IReadOnlyList<(string Id, string Name)> ListOutputDevices()
    {
        try { return AudioDeviceWatcher.ListDevices(); }
        catch (COMException) { return []; }
    }

    public AppPaths Paths { get; }
    public AppSettings Settings { get; private set; }
    public LibraryService Library { get; }
    public PlaybackEngine Engine { get; }
    public LiveFeatureRecorder LiveFeatures { get; }
    public LyricsService Lyrics { get; }
    public PlayerStore Store { get; }
    public FileLog Log { get; }

    /// <summary>网易云音乐集成；没有可用的 Provider 时为 null（界面显示"暂不可用"）。</summary>
    public NetEaseIntegrationViewModel? NetEase { get; }

    /// <summary>
    /// 网易云集成是可选模块：创建它不会发出网络请求，只有用户扫码登录或点击同步时才联网。
    /// 默认使用客户端接口适配器（非官方，需要用户在设置中确认），会话保存在 Windows 凭据管理器中。
    /// 开发时设置 SOLISTEN_REMOTE_PROVIDER=fake 使用本机演示数据，凭据只保存在内存中；设置为 off 关闭集成。
    /// </summary>
    private NetEaseIntegrationViewModel? CreateNetEase()
    {
        (IMusicProvider, ICredentialStore)? provider = Environment.GetEnvironmentVariable("SOLISTEN_REMOTE_PROVIDER") switch
        {
            "fake" => (FakeMusicProvider.Demo(), new InMemoryCredentialStore()),
            "off" => null,
            _ => (new NetEaseProvider(), new CachedCredentialStore(new PasswordVaultCredentialStore())),
        };
        return provider is var (p, store)
            ? new NetEaseIntegrationViewModel(p, store, Library, Log, () => Settings, UpdateSettings)
            : null;
    }

    /// <summary>
    /// 在导航层打开"网易云歌单"排列并定位到一个歌单，不打断当前播放。返回这个歌单能浏览的歌曲数量。
    /// </summary>
    public int ShowRemotePlaylist(string playlistId)
    {
        if (NetEase is not { } netease) return 0;
        var ctx = Store.State.Library.Arrange(Arrangement.Playlists);
        var key = PlaylistKey(netease, playlistId);
        var group = ctx.Groups.FirstOrDefault(g => g.Key == key);
        if (group is null) return 0;
        Store.Dispatch(new SetArrangement(Arrangement.Playlists));
        Store.Dispatch(new ShowContext(ctx, group.Start));
        return group.Count;
    }

    private static string PlaylistKey(NetEaseIntegrationViewModel netease, string playlistId) => $"{netease.Provider.Provider}:{playlistId}";

    /// <summary>
    /// "网易云歌单"排列：每个歌单一个分组，按设置页中的顺序（自建在前）。已匹配到本地文件的歌用本地文件，
    /// 其余在已登录时作为在线曲目；两者都没有的歌不出现。远端已删除的歌单不显示。
    /// </summary>
    private PlaybackContext BuildPlaylists(NetEaseIntegrationViewModel netease, LibraryIndex local, ImmutableDictionary<TrackId, Track> online)
    {
        var order = ImmutableArray.CreateBuilder<TrackId>();
        var groups = ImmutableArray.CreateBuilder<ContextGroup>();
        foreach (var pl in netease.Playlists.Where(p => !p.RemoteDeleted))
        {
            int start = order.Count;
            var seen = new HashSet<TrackId>();
            foreach (var e in netease.Database.GetPlaylistEntries(netease.Provider.Provider, pl.Playlist.Id))
            {
                TrackId? id = e.Mapping.IsPlayable(local) ? e.Mapping.LocalTrack
                    : NetEaseOnline.IdOf(e.Track.Id) is var remote && online.ContainsKey(remote) ? remote
                    : null;
                if (id is { } t && seen.Add(t)) order.Add(t);
            }
            if (order.Count > start) groups.Add(new ContextGroup(PlaylistKey(netease, pl.Playlist.Id), pl.Playlist.Name, start, order.Count - start));
        }
        return new PlaybackContext(Arrangement.Playlists, ScopeKey.All, order.ToImmutable(), groups.ToImmutable());
    }

    // ───────────────────────── 在线播放 ─────────────────────────

    private volatile bool _started;
    private Timer? _prefetch;
    private TrackId? _prefetchTarget;
    private TrackId? _remoteRetried;
    private long _loadGeneration;
    private long _successorGeneration;

    /// <summary>本地曲库加上当前可播放的在线曲目。</summary>
    private LibraryIndex WithOnline(LibraryIndex index)
    {
        if (NetEase is not { } netease) return index;
        var online = netease.Online.Tracks;
        try { return index.WithRemote(online, BuildPlaylists(netease, index, online)); }
        catch (Exception e) when (e is Microsoft.Data.Sqlite.SqliteException or InvalidOperationException)
        {
            Log.Write($"netease playlists failed: {e.Message}");
            return index.WithRemote(online);
        }
    }

    /// <summary>
    /// 请求在线播放地址后再交给引擎。地址请求期间又切了歌时丢弃结果；
    /// 远端不给地址（无版权、需要购买）时按播放失败处理，Reducer 会跳到下一首。
    /// </summary>
    private void LoadRemote(TrackId id, TimeSpan startAt, bool paused, bool fresh = false)
    {
        long generation = Interlocked.Increment(ref _loadGeneration);
        if (_remoteRetried is { } retried && retried != id) _remoteRetried = null; // 换了歌：新歌重新拥有一次重试机会
        Engine.Stop(); // 先停掉上一首，避免请求地址期间仍在播放旧歌
        if (NetEase is not { } netease)
        {
            Store.Dispatch(new PlaybackFailed(id, "网易云集成不可用"));
            return;
        }
        _ = Task.Run(async () =>
        {
            string? failure = null;
            try
            {
                var stream = await netease.Online.ResolveAsync(id, CancellationToken.None, fresh);
                if (Interlocked.Read(ref _loadGeneration) != generation) return;
                if (stream is null)
                {
                    failure = "网易云没有提供这首歌的播放地址（可能没有版权或需要单独购买）";
                }
                else
                {
                    if (stream.IsTrial) Toast?.Invoke($"试听片段 {(int)stream.TrialLength!.Value.TotalSeconds} 秒：完整播放需要网易云会员或购买");
                    Engine.Load(id, stream.Url.AbsoluteUri, startAt, paused);
                    PrepareRemoteVisuals(id);
                }
            }
            catch (NetEaseApiException e)
            {
                failure = e.UserMessage;
            }
            catch (OperationCanceledException)
            {
                failure = "网络请求超时";
            }
            if (failure is not null && Interlocked.Read(ref _loadGeneration) == generation)
            {
                Log.Write($"netease stream failed {id}: {failure}");
                Store.Dispatch(new PlaybackFailed(id, failure));
            }
        });
    }

    private readonly HashSet<TrackId> _preparingRemote = [];
    private readonly HashSet<TrackId> _liveLogged = []; // 只在分析线程上访问
    private readonly ConcurrentDictionary<TrackId, bool> _featuresCached = new();

    /// <summary>
    /// 在线曲目开始播放：从封面提取主色（只下载封面，不再额外下载音频）。
    /// 音频特征由边播边分析提供：播放时实时驱动空间，完整播完一遍后保存为缓存。
    /// </summary>
    private void PrepareRemoteVisuals(TrackId id)
    {
        if (NetEase is not { } netease || AnalysisScheduler.LoadStreamPalette(Paths, id) is not null) return;
        lock (_preparingRemote) if (!_preparingRemote.Add(id)) return;
        _ = Task.Run(async () =>
        {
            try
            {
                var cover = await netease.Online.CoverAsync(id);
                if (Library.Analysis.EnsureStreamPalette(id, cover) is not null) AnalysisReady?.Invoke(id);
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                Log.Write($"netease palette failed {id}: {e.Message}");
            }
            finally
            {
                lock (_preparingRemote) _preparingRemote.Remove(id);
            }
        });
    }

    /// <summary>需要边播边分析的曲目：还没有缓存特征的在线曲目（在引擎线程上调用，每首只查一次磁盘）。</summary>
    private bool NeedsLiveFeatures(TrackId id) =>
        id.IsRemote && !_featuresCached.GetOrAdd(id, i => AnalysisScheduler.HasStreamFeatures(Paths, i));

    private void OnLiveFeaturesCompleted(TrackId id, SoundFeatures features)
    {
        try
        {
            Library.Analysis.SaveStreamFeatures(id, features);
            _featuresCached[id] = true;
            Log.Write($"live features saved {id}");
            AnalysisReady?.Invoke(id); // 画面换成完整的离线特征
        }
        catch (IOException e) { Log.Write($"save live features failed {id}: {e.Message}"); }
    }

    /// <summary>某首歌的实时音频特征有更新（在分析线程上触发）：聆听空间在没有离线特征时用它驱动。</summary>
    public event Action<TrackId, SoundFeatures>? LiveFeaturesUpdated;

    /// <summary>提前请求下一首在线歌曲的地址，保持无缝衔接；期间后继又变化时丢弃结果。</summary>
    private void SetRemoteSuccessor(TrackId id, long generation)
    {
        if (NetEase is not { } netease) return;
        _ = Task.Run(async () =>
        {
            try
            {
                var stream = await netease.Online.ResolveAsync(id, CancellationToken.None);
                if (stream is not null && Interlocked.Read(ref _successorGeneration) == generation)
                    Engine.SetSuccessor(id, stream.Url.AbsoluteUri);
            }
            catch (Exception e) when (e is NetEaseApiException or OperationCanceledException)
            {
                Log.Write($"netease successor failed {id}: {Solisten.Integrations.NetEase.Diagnostics.Redactor.Redact(e.Message)}");
            }
        });
    }

    /// <summary>某首歌分析完成（可能在后台线程触发）。</summary>
    public event Action<TrackId>? AnalysisReady;

    /// <summary>需要显示的轻提示（可能在后台线程触发）。</summary>
    public event Action<string>? Toast;

    private Task _verified = Task.CompletedTask;

    /// <summary>冷启动：先从数据库恢复聆听画布，再在后台做校验扫描，然后处理命令行传入的文件或文件夹。</summary>
    public void Start(IReadOnlyList<string>? paths = null)
    {
        NetEase?.ReloadOnline();
        var index = WithOnline(Library.Load());
        var session = Library.Database.LoadSession();
        Store.Dispatch(new LibraryLoaded(index, session));
        _started = true;
        _verified = Task.Run(async () =>
        {
            try { await Library.VerifyAllAsync(); }
            catch (Exception e) { Log.Write($"verify scan failed: {e}"); }
        });
        if (paths is { Count: > 0 }) _ = OpenPathsAsync(paths);
    }

    /// <summary>
    /// §8.6 打开外部传入的路径（命令行、"打开方式"、另一个实例转发）。
    /// 文件夹加入曲库；音频文件以它所在的文件夹作为临时 BrowseContext 显示出来，是否播放由用户确认，不打断当前歌曲。
    /// 不在曲库中的文件会先导入它所在的文件夹。
    /// </summary>
    public async Task OpenPathsAsync(IReadOnlyList<string> paths)
    {
        try
        {
            await _verified;
            TrackId? reveal = null;
            foreach (var raw in paths)
            {
                var path = Path.GetFullPath(raw);
                if (Directory.Exists(path))
                {
                    await AddFolderAsync(path);
                }
                else if (File.Exists(path) && global::Solisten.Library.Metadata.TrackMetadataReader.IsSupported(path))
                {
                    reveal ??= await EnsureTrackAsync(path);
                }
            }
            if (reveal is { } id) Store.Dispatch(new RevealTrack(id));
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            Log.Write($"open paths failed: {e}");
            Toast?.Invoke($"无法打开：{e.Message}");
        }
    }

    private async Task<TrackId?> EnsureTrackAsync(string file)
    {
        if (FindTrack(file) is { } existing) return existing;
        var folder = Path.GetDirectoryName(file)!;
        Toast?.Invoke($"正在导入 {folder}");
        await AddFolderAsync(folder);
        if (FindTrack(file) is { } added) return added;
        Toast?.Invoke("无法导入这个文件");
        return null;
    }

    private TrackId? FindTrack(string file)
    {
        foreach (var t in Library.Index.Tracks.Values)
        {
            if (string.Equals(Path.GetFullPath(t.Path), file, StringComparison.OrdinalIgnoreCase)) return t.Id;
        }
        return null;
    }

    /// <summary>加入文件夹。已经在某个曲库文件夹之内时只重新扫描那个文件夹，避免同一首歌出现两次。</summary>
    public async Task AddFolderAsync(string path)
    {
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        var containing = Library.Roots.FirstOrDefault(r =>
            string.Equals(r.Path, full, StringComparison.OrdinalIgnoreCase)
            || full.StartsWith(r.Path + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
        if (containing is not null)
        {
            try { await Library.RescanAsync(containing.Id); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Toast?.Invoke($"扫描失败：{e.Message}"); }
            return;
        }

        try { await Library.AddRootAsync(full); }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            Log.Write($"import failed {path}: {e}");
            Toast?.Invoke($"导入失败：{e.Message}");
        }
    }

    public void UpdateSettings(Func<AppSettings, AppSettings> update)
    {
        Settings = update(Settings);
        try { Settings.Save(Paths.Settings); }
        catch (IOException e) { Log.Write($"settings save failed: {e.Message}"); }
    }

    /// <summary>当前播放位置：引擎时钟指向播放锚点时以它为准，否则用 Store 中的位置。</summary>
    public (TimeSpan Position, bool Running) PositionOf(TrackId? anchor)
    {
        var clock = Engine.Clock;
        if (anchor is { } a && clock.Track == a) return (clock.Position, clock.IsRunning);
        return (Store.State.Playing?.Position ?? TimeSpan.Zero, false);
    }

    private string? PathOf(TrackId id) => Store.State.Library.Find(id)?.Path;

    public void Handle(Effect effect, PlayerStore store)
    {
        switch (effect)
        {
            case LoadTrack l when l.Track.IsRemote:
                LoadRemote(l.Track, l.StartAt, l.Paused);
                break;
            case LoadTrack l when PathOf(l.Track) is { } path:
                Interlocked.Increment(ref _loadGeneration); // 让仍在请求地址的在线曲目作废
                Engine.Load(l.Track, path, l.StartAt, l.Paused);
                Library.Analysis.Enqueue(l.Track, AnalysisPriority.Playing);
                break;
            case LoadTrack l:
                store.Dispatch(new PlaybackFailed(l.Track, "曲库中找不到这首歌"));
                break;
            case SetSuccessor s:
                long successor = Interlocked.Increment(ref _successorGeneration);
                if (s.Track is { IsRemote: true } remoteNext)
                {
                    Engine.SetSuccessor(null, null);
                    SetRemoteSuccessor(remoteNext, successor);
                    break;
                }
                Engine.SetSuccessor(s.Track, s.Track is { } succ ? PathOf(succ) : null);
                if (s.Track is { } next) Library.Analysis.Enqueue(next, AnalysisPriority.Snapshot);
                break;
            case PauseAudio: Engine.Pause(); break;
            case ResumeAudio: Engine.Resume(); break;
            case SeekAudio s: Engine.Seek(s.Position); break;
            case StopAudio: Engine.Stop(); break;

            case PersistSession p:
                Background(() => Library.Database.SaveSession(p.Session));
                break;
            case RecordPlay r:
                Background(() => Library.RecordPlay(r.Track));
                break;
            case RunSearch s:
                Background(() => store.Dispatch(new SearchResultsArrived(s.Query, Library.Database.Search(s.Query))));
                break;
            case PrioritizeAnalysis p when p.Track.IsRemote: break; // 在线曲目不做本地分析（需要完整解码音频）
            case PrioritizeAnalysis p:
                Library.Analysis.Enqueue(p.Track, AnalysisPriority.Visible);
                break;
            case SaveMarker m:
                Background(() => { Library.Database.AddMarker(m.Track, m.Position, m.Label); MarkersChanged?.Invoke(m.Track); });
                Toast?.Invoke($"已在 {Rendering.Progress.ProgressLayout.FormatTime(m.Position)} 打标记");
                break;
            case SaveMarkerLabel m:
                Background(() => { Library.Database.SetMarkerLabel(m.MarkerId, m.Label); MarkersChanged?.Invoke(m.Track); });
                break;
            case RemoveMarker m:
                Background(() => { Library.Database.DeleteMarker(m.MarkerId); MarkersChanged?.Invoke(m.Track); });
                break;
            case SaveTags t:
                Background(() => Library.Database.SetTags(t.Track, t.Tags));
                break;
            case SaveNote n:
                Background(() => Library.Database.SetNote(n.Track, n.Note));
                break;
            case SaveFavorite f:
                Background(() => Library.SetFavorite(f.Track, f.Favorite));
                break;
            case ShowToast t:
                Toast?.Invoke(t.Message);
                break;
        }
    }

    /// <summary>某首歌的标记有变化。</summary>
    public event Action<TrackId>? MarkersChanged;

    private void Background(Action a) => Task.Run(() =>
    {
        try { a(); }
        catch (Exception e) { Log.Write($"background effect failed: {e}"); }
    });

    /// <summary>退出前保存当前位置。</summary>
    public void SaveSessionNow()
    {
        var s = Store.State;
        if (s.Playing is null) return;
        var (pos, _) = PositionOf(s.Playing.Track);
        var snapshot = Reducer.ToSession(s) with { Position = pos };
        try { Library.Database.SaveSession(snapshot); }
        catch (Exception e) { Log.Write($"session save failed: {e.Message}"); }
    }

    public void Dispose()
    {
        _deviceDebounce.Dispose();
        _prefetch?.Dispose();
        _deviceWatcher?.Dispose();
        Engine.Dispose();
        LiveFeatures.Dispose();
        NetEase?.Dispose();
        Library.Dispose();
    }

    // ───────────────────────── 防止睡眠 ─────────────────────────

    private const uint EsContinuous = 0x80000000;
    private const uint EsSystemRequired = 0x00000001;

    /// <summary>§8.6：播放时只阻止系统空闲睡眠，不阻止显示器关闭。</summary>
    public static void PreventSleep(bool playing) => SetThreadExecutionState(playing ? EsContinuous | EsSystemRequired : EsContinuous);

    [LibraryImport("kernel32.dll")]
    private static partial uint SetThreadExecutionState(uint flags);
}

public sealed class FileLog(string dir)
{
    private readonly Lock _gate = new();

    public void Write(string message)
    {
        try
        {
            lock (_gate)
                File.AppendAllText(Path.Combine(dir, $"{DateTime.Now:yyyy-MM-dd}.log"), $"{DateTime.Now:HH:mm:ss.fff} {message}{Environment.NewLine}");
        }
        catch (IOException) { }
    }
}
