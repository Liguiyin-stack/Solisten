using Solisten.Core.Model;
using Solisten.Integrations.NetEase.Api;
using Solisten.Integrations.NetEase.Auth;
using Solisten.Library.Remote;

namespace Solisten.Integrations.NetEase.Sync;

/// <summary>远程曲库来源：浏览本机缓存的远程歌单，并按需与远端同步。</summary>
public interface IRemoteLibrarySource
{
    Task<IReadOnlyList<RemotePlaylist>> GetPlaylistsAsync(CancellationToken ct);
    Task<IReadOnlyList<RemoteTrack>> GetTracksAsync(string playlistId, CancellationToken ct);
    Task<SyncSummary> SyncAsync(SyncOptions options, IProgress<SyncProgress>? progress, CancellationToken ct);
}

/// <summary>
/// 歌单同步（设计文档 §8）：验证会话 → 分页读取歌单 → 读取所选歌单的曲目 → 匹配本地曲库。
/// 同步是幂等的增量 Upsert，可取消、可部分成功；每次运行都写入 sync_runs。
/// 同一时间只允许一个同步任务。浏览（GetPlaylists / GetTracks）只读本机缓存，断网时也可用。
/// </summary>
public sealed class NetEaseSyncService(
    IMusicProvider provider,
    ICredentialStore credentials,
    RemoteMusicDatabase database,
    Func<LibraryIndex> localIndex,
    SyncPolicy? policy = null) : IRemoteLibrarySource
{
    private readonly SyncPolicy _policy = policy ?? SyncPolicy.Default;
    private readonly SemaphoreSlim _running = new(1, 1);

    public bool IsRunning => _running.CurrentCount == 0;

    public RemoteMusicDatabase Database => database;

    public Task<IReadOnlyList<RemotePlaylist>> GetPlaylistsAsync(CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<RemotePlaylist>>([.. database.GetPlaylists(provider.Provider).Where(p => !p.RemoteDeleted).Select(p => p.Playlist)]);

    public Task<IReadOnlyList<RemoteTrack>> GetTracksAsync(string playlistId, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<RemoteTrack>>([.. database.GetPlaylistEntries(provider.Provider, playlistId).Select(e => e.Track)]);

    /// <exception cref="InvalidOperationException">已有同步正在运行。</exception>
    public async Task<SyncSummary> SyncAsync(SyncOptions options, IProgress<SyncProgress>? progress, CancellationToken ct)
    {
        if (!await _running.WaitAsync(0, ct).ConfigureAwait(false)) throw new InvalidOperationException("网易云音乐正在同步");
        try
        {
            return await RunAsync(options, progress, ct).ConfigureAwait(false);
        }
        finally
        {
            _running.Release();
        }
    }

    private async Task<SyncSummary> RunAsync(SyncOptions options, IProgress<SyncProgress>? progress, CancellationToken ct)
    {
        var p = provider.Provider;
        var limiter = new SyncRateLimiter(_policy);
        long runId = database.StartSyncRun(p, database.GetAccount(p)?.Id);
        int playlistsSeen = 0, tracksSeen = 0, tracksTotal = 0, matched = 0;
        var failed = new List<string>();

        SyncSummary Finish(SyncRunStatus status, string? message, int unmatched = 0)
        {
            database.FinishSyncRun(runId, new SyncRunResult(status, playlistsSeen, tracksSeen, matched, failed.Count, message));
            return new SyncSummary(status, playlistsSeen, tracksSeen, matched, unmatched, failed.Count, failed, message);
        }

        try
        {
            progress?.Report(new SyncProgress(SyncStage.Validating, 0, 0, 0, 0, 0));
            var credential = credentials.Load(p) ?? throw new NetEaseApiException(NetEaseErrorKind.Unauthorized, "not signed in");
            var account = await limiter.RunAsync(c => provider.GetAccountAsync(credential, c), ct).ConfigureAwait(false);
            var stored = database.UpsertAccount(account);
            database.AttachSyncRunAccount(runId, stored.Id);

            // 歌单列表：分页读取完整列表后一次写入，只有完整列表才能判断"远端已删除"。
            progress?.Report(new SyncProgress(SyncStage.FetchingPlaylists, 0, 0, 0, 0, 0));
            var playlists = await FetchAllAsync(limiter, (offset, c) => provider.GetPlaylistsAsync(credential, account.UserId, offset, _policy.PageSize, c), ct).ConfigureAwait(false);
            database.UpsertPlaylists(stored.Id, playlists, complete: true);

            var selected = playlists.Where(pl => pl.IsOwned || options.SelectedPlaylistIds.Contains(pl.Id)).ToList();
            tracksTotal = selected.Sum(pl => pl.TrackCount);
            foreach (var playlist in selected)
            {
                progress?.Report(new SyncProgress(SyncStage.FetchingTracks, playlistsSeen, selected.Count, tracksSeen, tracksTotal, matched));
                try
                {
                    var tracks = await FetchAllAsync(limiter, (offset, c) => provider.GetPlaylistTracksAsync(credential, playlist.Id, offset, _policy.PageSize, c), ct).ConfigureAwait(false);
                    database.ReplacePlaylistItems(p, playlist.Id, tracks);
                    tracksSeen += tracks.Count;
                }
                catch (NetEaseApiException e) when (!e.StopsSync)
                {
                    // 单个歌单失败：保留已成功的部分，记录后继续。
                    failed.Add(playlist.Name);
                }
                playlistsSeen++;
            }

            progress?.Report(new SyncProgress(SyncStage.Matching, playlistsSeen, selected.Count, tracksSeen, tracksTotal, matched));
            var (m, unmatched) = Rematch();
            matched = m;
            progress?.Report(new SyncProgress(SyncStage.Completed, playlistsSeen, selected.Count, tracksSeen, tracksTotal, matched));

            var status = failed.Count == 0 ? SyncRunStatus.Succeeded
                : failed.Count < selected.Count ? SyncRunStatus.PartiallySucceeded
                : SyncRunStatus.Failed;
            return Finish(status, failed.Count == 0 ? null : $"{failed.Count} 个歌单同步失败", unmatched);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return Finish(SyncRunStatus.Cancelled, "已取消");
        }
        catch (NetEaseApiException e)
        {
            // 登录态失效：删除本机会话，界面显示"请重新登录"；远程缓存保留，可以继续浏览。
            if (e.Kind == NetEaseErrorKind.Unauthorized) credentials.Delete(p);
            return Finish(SyncRunStatus.Failed, e.UserMessage);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            Finish(SyncRunStatus.Failed, "同步失败");
            throw;
        }
    }

    private async Task<List<T>> FetchAllAsync<T>(SyncRateLimiter limiter, Func<int, CancellationToken, Task<RemotePage<T>>> page, CancellationToken ct)
    {
        var all = new List<T>();
        for (int n = 0; n < _policy.MaxPages; n++)
        {
            int offset = all.Count;
            var result = await limiter.RunAsync(c => page(offset, c), ct).ConfigureAwait(false);
            all.AddRange(result.Items);
            if (!result.HasMore || result.Items.Count == 0) return all;
        }
        throw new NetEaseApiException(NetEaseErrorKind.InvalidResponse, "too many pages");
    }

    /// <summary>
    /// 用当前本地曲库重新匹配全部已缓存的远程曲目（本地曲库变化后也会调用）。
    /// 返回（已匹配, 未匹配）数量。
    /// </summary>
    public (int Matched, int Unmatched) Rematch()
    {
        var p = provider.Provider;
        var matcher = new TrackMatcher(localIndex());
        var previous = database.GetMappings(p);
        var mappings = database.GetTracks(p).Select(t => matcher.Match(t, previous.GetValueOrDefault(t.Id))).ToList();
        database.SaveMappings(mappings);
        int matched = mappings.Count(m => m.Status == TrackMatchStatus.Matched);
        return (matched, mappings.Count - matched);
    }
}
