using Solisten.App.Services;
using Solisten.Core.Model;
using Solisten.Integrations.NetEase.Api;
using Solisten.Integrations.NetEase.Auth;
using Solisten.Integrations.NetEase.Diagnostics;
using Solisten.Integrations.NetEase.Sync;
using Solisten.Library;
using Solisten.Library.Remote;

namespace Solisten.App.Integrations;

/// <summary>
/// 网易云音乐集成的登录与同步状态（设计文档 §11）。不进入 AppState：播放状态不因网络请求而重建。
/// 界面只拿到账号摘要和同步进度，会话凭据只在登录服务和安全存储之间传递。
/// </summary>
public sealed class NetEaseIntegrationViewModel : IDisposable
{
    private readonly LibraryService _library;
    private readonly FileLog _log;
    private readonly Func<AppSettings> _settings;
    private readonly Action<Func<AppSettings, AppSettings>> _updateSettings;
    private readonly Lock _gate = new();
    private CancellationTokenSource? _syncCts;
    private bool _rematchPending;
    private object? _matchedTracks;

    public NetEaseIntegrationViewModel(IMusicProvider provider, ICredentialStore credentials, LibraryService library, FileLog log,
        Func<AppSettings> settings, Action<Func<AppSettings, AppSettings>> updateSettings)
    {
        _library = library;
        _log = log;
        _settings = settings;
        _updateSettings = updateSettings;
        Provider = provider;
        Database = new RemoteMusicDatabase(library.Database);
        Login = new NetEaseQrLoginService(provider, credentials);
        Sync = new NetEaseSyncService(provider, credentials, Database, () => _library.Index);
        Online = new NetEaseOnline(provider, credentials, Database, library.Paths, log) { Quality = settings().NetEaseQuality };
        _library.Changed += OnLibraryChanged;
        // 提前读取一次本机凭据（不联网），之后界面判断登录状态只读内存缓存。
        _ = Task.Run(() => Login.HasSession);
    }

    public IMusicProvider Provider { get; }
    public RemoteMusicDatabase Database { get; }
    public NetEaseQrLoginService Login { get; }
    public NetEaseSyncService Sync { get; }
    public NetEaseOnline Online { get; }

    /// <summary>按当前登录状态刷新在线曲目（同步、登录、退出后调用）。</summary>
    public void ReloadOnline() => Online.Reload(IsConnected);

    public void SetQuality(StreamQuality quality)
    {
        Online.Quality = quality;
        _updateSettings(s => s with { NetEaseQuality = quality });
    }

    /// <summary>非官方适配器需要用户先确认才能登录。</summary>
    public bool RequiresConsent => !Provider.IsOfficial && !_settings().NetEaseUnofficialAccepted;

    public bool IsConnected => Login.HasSession;
    public StoredAccount? Account => Database.GetAccount(Provider.Provider);
    public SyncRun? LastRun => Database.GetLastSyncRun(Provider.Provider);
    public IReadOnlyList<StoredPlaylist> Playlists => Database.GetPlaylists(Provider.Provider);
    public bool IsSyncing => Sync.IsRunning;
    public SyncProgress? Progress { get; private set; }

    /// <summary>登录、同步、匹配状态变化（可能在后台线程触发）。</summary>
    public event Action? Changed;

    public void AcceptUnofficial(bool accepted)
    {
        _updateSettings(s => s with { NetEaseUnofficialAccepted = accepted });
        Changed?.Invoke();
    }

    public bool IsSelected(string playlistId) => _settings().NetEaseSelectedPlaylists.Contains(playlistId, StringComparer.Ordinal);

    public void SetSelected(string playlistId, bool selected)
    {
        _updateSettings(s =>
        {
            var set = s.NetEaseSelectedPlaylists.ToHashSet(StringComparer.Ordinal);
            if (selected) set.Add(playlistId); else set.Remove(playlistId);
            return s with { NetEaseSelectedPlaylists = [.. set.Order(StringComparer.Ordinal)] };
        });
    }

    /// <summary>登录成功后由登录对话框调用：记录账号摘要（凭据已经由登录服务保存）。</summary>
    public void OnSignedIn(RemoteAccount account)
    {
        Database.UpsertAccount(account);
        _log.Write($"netease signed in: user {account.UserId}");
        ReloadOnline();
        Changed?.Invoke();
    }

    private int _accountChecked;

    /// <summary>
    /// 本机有会话但没有账号摘要（例如数据库被删除或换了数据目录）：在后台确认一次账号并补上记录。
    /// 会话已失效时登录服务会删除它，界面回到"未连接"。只在已登录时才会联网。
    /// </summary>
    public void EnsureAccount()
    {
        if (!IsConnected || Account is not null || Interlocked.Exchange(ref _accountChecked, 1) == 1) return; // 每次运行只确认一次，失败也不反复重试
        _ = Task.Run(async () =>
        {
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                Database.UpsertAccount(await Login.ValidateAsync(cts.Token).ConfigureAwait(false));
                ReloadOnline();
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                _log.Write($"netease account check failed: {Redactor.Redact(e.Message)}");
            }
            finally
            {
                Changed?.Invoke();
            }
        });
    }

    /// <summary>立即同步。已经在同步时返回 null。</summary>
    public async Task<SyncSummary?> SyncNowAsync()
    {
        CancellationTokenSource cts;
        lock (_gate)
        {
            if (Sync.IsRunning) return null;
            _syncCts?.Dispose();
            _syncCts = cts = new CancellationTokenSource();
        }
        Progress = new SyncProgress(SyncStage.Validating, 0, 0, 0, 0, 0);
        Changed?.Invoke();
        try
        {
            var options = new SyncOptions(_settings().NetEaseSelectedPlaylists.ToHashSet(StringComparer.Ordinal));
            var progress = new Progress<SyncProgress>(p => { Progress = p; Changed?.Invoke(); });
            var summary = await Task.Run(() => Sync.SyncAsync(options, progress, cts.Token)).ConfigureAwait(false);
            _log.Write($"netease sync {summary.Status}: {summary.Playlists} playlists, {summary.Tracks} tracks, {summary.Matched} matched, {summary.Errors} errors {Redactor.Redact(summary.Message)}");
            ReloadOnline();
            return summary;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            _log.Write($"netease sync failed: {Redactor.Redact(e.ToString())}");
            return new SyncSummary(SyncRunStatus.Failed, 0, 0, 0, 0, 1, [], "同步失败");
        }
        finally
        {
            Progress = null;
            Changed?.Invoke();
        }
    }

    public void CancelSync()
    {
        lock (_gate) _syncCts?.Cancel();
    }

    /// <summary>退出登录：删除本机会话并尽力通知远端。<paramref name="deleteCache"/> 为 true 时同时删除远程歌单缓存。</summary>
    public async Task LogoutAsync(bool deleteCache)
    {
        CancelSync();
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await Login.LogoutAsync(cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { } // 远端注销超时：本机凭据已在 finally 中删除
        if (deleteCache) Database.DeleteProviderData(Provider.Provider);
        ReloadOnline();
        _updateSettings(s => s with { NetEaseSelectedPlaylists = deleteCache ? [] : s.NetEaseSelectedPlaylists });
        _log.Write($"netease signed out (cache {(deleteCache ? "deleted" : "kept")})");
        Changed?.Invoke();
    }

    /// <summary>
    /// 本地曲库变化后重新匹配（文件加入、移动或删除）。只有曲目本身变化才需要（播放次数、收藏变化不需要）；
    /// 同步期间跳过，同步结束时本来就会匹配。
    /// </summary>
    private void OnLibraryChanged(LibraryIndex index)
    {
        lock (_gate)
        {
            if (ReferenceEquals(index.Tracks, _matchedTracks)) return;
            _matchedTracks = index.Tracks;
            if (_rematchPending || Sync.IsRunning) return;
            _rematchPending = true;
        }
        _ = Task.Run(() =>
        {
            try
            {
                lock (_gate) _rematchPending = false;
                if (Database.GetAccount(Provider.Provider) is null) return;
                Sync.Rematch();
                ReloadOnline(); // 匹配结果变了：重建"网易云歌单"排列
                Changed?.Invoke();
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                _log.Write($"netease rematch failed: {Redactor.Redact(e.Message)}");
            }
        });
    }

    public void Dispose()
    {
        _library.Changed -= OnLibraryChanged;
        CancelSync();
        lock (_gate) _syncCts?.Dispose();
        (Provider as IDisposable)?.Dispose();
    }
}
