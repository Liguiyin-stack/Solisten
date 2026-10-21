using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Dapper;
using Microsoft.Data.Sqlite;
using Solisten.Core.Model;
using Solisten.Library.Storage;

namespace Solisten.Library.Remote;

/// <summary>
/// 远程歌单索引与匹配结果的 SQLite 读写（schema 3）。与 <see cref="LibraryDatabase"/> 共享连接和锁。
/// 所有写入都是增量 Upsert：同步失败或中途取消时，已经写入的部分保持有效，旧缓存不会被清空。
/// Cookie、登录 key、二维码内容等会话材料从不写入这里。
/// </summary>
public sealed class RemoteMusicDatabase(LibraryDatabase db)
{
    // ───────────────────────── 账号 ─────────────────────────

    public StoredAccount UpsertAccount(RemoteAccount account) => db.Locked(c =>
    {
        var now = Now();
        var id = c.QuerySingle<long>("""
            INSERT INTO remote_accounts(provider, remote_user_id, nickname, avatar_url, created_at, updated_at)
            VALUES(@provider, @UserId, @Nickname, @AvatarUrl, @now, @now)
            ON CONFLICT(provider, remote_user_id) DO UPDATE SET nickname=excluded.nickname, avatar_url=excluded.avatar_url, updated_at=excluded.updated_at
            RETURNING id
            """, new { provider = Name(account.Provider), account.UserId, account.Nickname, account.AvatarUrl, now });
        return ReadAccount(c, "id=@id", new { id })!;
    });

    /// <summary>该来源最近登录过的账号。</summary>
    public StoredAccount? GetAccount(RemoteProvider provider) =>
        db.Locked(c => ReadAccount(c, "provider=@provider ORDER BY updated_at DESC LIMIT 1", new { provider = Name(provider) }));

    private static StoredAccount? ReadAccount(SqliteConnection c, string where, object args)
    {
        var r = c.QuerySingleOrDefault<AccountRow>($"SELECT id, provider, remote_user_id, nickname, avatar_url, last_sync_at FROM remote_accounts WHERE {where}", args);
        return r is null ? null : new StoredAccount(r.id, new RemoteAccount(Parse(r.provider), r.remote_user_id, r.nickname ?? "", r.avatar_url), Date(r.last_sync_at));
    }

    // ───────────────────────── 歌单 ─────────────────────────

    /// <summary>
    /// 写入账号的歌单列表（按远端顺序）。<paramref name="complete"/> 为 true 表示这是完整列表：
    /// 列表之外的旧歌单只标记为远端已删除，不删除本地缓存，避免误删用户数据。
    /// </summary>
    public void UpsertPlaylists(long accountId, IReadOnlyList<RemotePlaylist> playlists, bool complete) => db.Locked(c =>
    {
        using var tx = c.BeginTransaction();
        var now = Now();
        for (int i = 0; i < playlists.Count; i++)
        {
            var p = playlists[i];
            c.Execute("""
                INSERT INTO remote_playlists(provider, playlist_id, account_id, name, description, cover_url, is_owned, track_count, remote_updated_at, synced_at, position, remote_deleted)
                VALUES(@provider, @Id, @accountId, @Name, @Description, @CoverUrl, @owned, @TrackCount, @updated, @now, @i, 0)
                ON CONFLICT(provider, playlist_id) DO UPDATE SET
                    account_id=excluded.account_id, name=excluded.name, description=excluded.description, cover_url=excluded.cover_url,
                    is_owned=excluded.is_owned, track_count=excluded.track_count, remote_updated_at=excluded.remote_updated_at,
                    synced_at=excluded.synced_at, position=excluded.position, remote_deleted=0
                """, new
            {
                provider = Name(p.Provider), p.Id, accountId, p.Name, p.Description, p.CoverUrl, owned = p.IsOwned ? 1 : 0,
                p.TrackCount, updated = p.UpdatedAt.ToString("O"), now, i,
            }, tx);
        }
        if (complete)
        {
            var seen = playlists.Select(p => p.Id).ToHashSet(StringComparer.Ordinal);
            var existing = c.Query<string>("SELECT playlist_id FROM remote_playlists WHERE account_id=@accountId AND remote_deleted=0", new { accountId }, tx).ToList();
            foreach (var id in existing.Where(id => !seen.Contains(id)))
                c.Execute("UPDATE remote_playlists SET remote_deleted=1 WHERE account_id=@accountId AND playlist_id=@id", new { accountId, id }, tx);
        }
        tx.Commit();
    });

    public IReadOnlyList<StoredPlaylist> GetPlaylists(RemoteProvider provider) => db.Locked(c =>
        c.Query<PlaylistRow>("""
            SELECT p.*,
                   (SELECT COUNT(*) FROM remote_playlist_items i WHERE i.provider=p.provider AND i.playlist_id=p.playlist_id) AS cached,
                   (SELECT COUNT(*) FROM remote_playlist_items i
                      JOIN remote_track_mappings m ON m.provider=i.provider AND m.track_id=i.track_id
                     WHERE i.provider=p.provider AND i.playlist_id=p.playlist_id AND m.status='Matched' AND m.local_track_id IS NOT NULL) AS matched
            FROM remote_playlists p WHERE p.provider=@provider ORDER BY p.remote_deleted, p.is_owned DESC, p.position
            """, new { provider = Name(provider) })
        .Select(r => r.ToStored()).ToList());

    // ───────────────────────── 歌单条目 ─────────────────────────

    /// <summary>
    /// 用远端的完整曲目列表更新一个歌单：曲目元数据 Upsert，条目顺序重写，
    /// 不在新列表中的条目只删除关系（远程曲目本身和它的匹配结果保留）。
    /// </summary>
    public void ReplacePlaylistItems(RemoteProvider provider, string playlistId, IReadOnlyList<RemoteTrack> tracks) => db.Locked(c =>
    {
        using var tx = c.BeginTransaction();
        var now = Now();
        var p = Name(provider);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        int position = 0;
        foreach (var t in tracks)
        {
            if (t.Id.Provider != provider || !seen.Add(t.Id.Value)) continue; // 同一歌单中的重复曲目只保留第一次出现
            UpsertTrack(c, tx, t, now);
            c.Execute("""
                INSERT INTO remote_playlist_items(provider, playlist_id, track_id, position) VALUES(@p, @playlistId, @id, @position)
                ON CONFLICT(provider, playlist_id, track_id) DO UPDATE SET position=excluded.position
                """, new { p, playlistId, id = t.Id.Value, position }, tx);
            position++;
        }
        var existing = c.Query<string>("SELECT track_id FROM remote_playlist_items WHERE provider=@p AND playlist_id=@playlistId", new { p, playlistId }, tx).ToList();
        foreach (var id in existing.Where(id => !seen.Contains(id)))
            c.Execute("DELETE FROM remote_playlist_items WHERE provider=@p AND playlist_id=@playlistId AND track_id=@id", new { p, playlistId, id }, tx);
        tx.Commit();
    });

    private static void UpsertTrack(SqliteConnection c, SqliteTransaction tx, RemoteTrack t, string now) =>
        c.Execute("""
            INSERT INTO remote_tracks(provider, track_id, title, artist, album, duration_ms, isrc, cover_url, playable_online, remote_updated_at, raw_hash, synced_at)
            VALUES(@provider, @id, @Title, @Artist, @Album, @ms, @Isrc, @CoverUrl, @playable, @updated, @hash, @now)
            ON CONFLICT(provider, track_id) DO UPDATE SET
                title=excluded.title, artist=excluded.artist, album=excluded.album, duration_ms=excluded.duration_ms, isrc=excluded.isrc,
                cover_url=excluded.cover_url, playable_online=excluded.playable_online, remote_updated_at=excluded.remote_updated_at,
                raw_hash=excluded.raw_hash, synced_at=excluded.synced_at
            """, new
        {
            provider = Name(t.Id.Provider), id = t.Id.Value, t.Title, t.Artist, t.Album, ms = (long)t.Duration.TotalMilliseconds,
            t.Isrc, t.CoverUrl, playable = t.IsPlayableOnline ? 1 : 0, updated = t.UpdatedAt.ToString("O"), hash = Hash(t), now,
        }, tx);

    public IReadOnlyList<RemotePlaylistEntry> GetPlaylistEntries(RemoteProvider provider, string playlistId) => db.Locked(c =>
        c.Query<EntryRow>("""
            SELECT i.position, t.*, m.local_track_id, m.status, m.confidence, m.reason, m.is_manual
            FROM remote_playlist_items i
            JOIN remote_tracks t ON t.provider=i.provider AND t.track_id=i.track_id
            LEFT JOIN remote_track_mappings m ON m.provider=i.provider AND m.track_id=i.track_id
            WHERE i.provider=@provider AND i.playlist_id=@playlistId ORDER BY i.position
            """, new { provider = Name(provider), playlistId })
        .Select(r => new RemotePlaylistEntry((int)r.position, r.ToTrack(), r.ToMapping())).ToList());

    /// <summary>已缓存的全部远程曲目（同步后重新匹配用）。</summary>
    public IReadOnlyList<RemoteTrack> GetTracks(RemoteProvider provider) => db.Locked(c =>
        c.Query<TrackRow>("SELECT * FROM remote_tracks WHERE provider=@provider", new { provider = Name(provider) }).Select(r => r.ToTrack()).ToList());

    // ───────────────────────── 匹配 ─────────────────────────

    public IReadOnlyDictionary<RemoteTrackId, RemoteTrackMapping> GetMappings(RemoteProvider provider) => db.Locked(c =>
        c.Query<EntryRow>("SELECT provider, track_id, local_track_id, status, confidence, reason, is_manual FROM remote_track_mappings WHERE provider=@provider",
                new { provider = Name(provider) })
            .Select(r => r.ToMapping())
            .ToDictionary(m => m.Remote));

    public void SaveMappings(IEnumerable<RemoteTrackMapping> mappings) => db.Locked(c =>
    {
        using var tx = c.BeginTransaction();
        var now = Now();
        foreach (var m in mappings)
        {
            c.Execute("""
                INSERT INTO remote_track_mappings(provider, track_id, local_track_id, status, confidence, reason, is_manual, updated_at)
                VALUES(@provider, @id, @local, @status, @Confidence, @Reason, @manual, @now)
                ON CONFLICT(provider, track_id) DO UPDATE SET
                    local_track_id=excluded.local_track_id, status=excluded.status, confidence=excluded.confidence,
                    reason=excluded.reason, is_manual=excluded.is_manual, updated_at=excluded.updated_at
                """, new
            {
                provider = Name(m.Remote.Provider), id = m.Remote.Value, local = m.LocalTrack?.Value, status = m.Status.ToString(),
                m.Confidence, m.Reason, manual = m.IsManual ? 1 : 0, now,
            }, tx);
        }
        tx.Commit();
    });

    /// <summary>匹配到这首本地曲目的远程曲目（用于给本地歌曲找网易云歌词）。没有匹配时为 null。</summary>
    public RemoteTrackId? FindRemoteFor(RemoteProvider provider, TrackId local) => db.Locked(c =>
        c.ExecuteScalar<string?>("SELECT track_id FROM remote_track_mappings WHERE provider=@provider AND local_track_id=@local AND status='Matched' LIMIT 1",
            new { provider = Name(provider), local = local.Value }) is { } id
            ? new RemoteTrackId(provider, id)
            : (RemoteTrackId?)null);

    /// <summary>
    /// 远程歌单在本地曲库中的投影：只包含已匹配且文件可用的本地曲目，按歌单顺序去重。
    /// 这是远程歌单进入播放上下文的唯一入口；未匹配的远程曲目永远不会出现在这里。
    /// </summary>
    public ImmutableArray<TrackId> PlayableTracks(RemoteProvider provider, string playlistId, LibraryIndex index)
    {
        var seen = new HashSet<TrackId>();
        var builder = ImmutableArray.CreateBuilder<TrackId>();
        foreach (var e in GetPlaylistEntries(provider, playlistId))
        {
            if (e.Mapping.IsPlayable(index) && seen.Add(e.Mapping.LocalTrack!.Value)) builder.Add(e.Mapping.LocalTrack.Value);
        }
        return builder.ToImmutable();
    }

    // ───────────────────────── 同步记录 ─────────────────────────

    /// <summary>开始一次同步。上次异常退出时遗留的"运行中"记录先标记为失败。</summary>
    public long StartSyncRun(RemoteProvider provider, long? accountId) => db.Locked(c =>
    {
        var p = Name(provider);
        var now = Now();
        c.Execute("UPDATE sync_runs SET status='Failed', finished_at=@now, error_message='同步被中断' WHERE provider=@p AND status='Running'", new { p, now });
        return c.QuerySingle<long>("INSERT INTO sync_runs(provider, account_id, started_at, status) VALUES(@p, @accountId, @now, 'Running') RETURNING id",
            new { p, accountId, now });
    });

    /// <summary>会话验证通过后记录这次同步属于哪个账号。</summary>
    public void AttachSyncRunAccount(long runId, long accountId) =>
        db.Locked(c => c.Execute("UPDATE sync_runs SET account_id=@accountId WHERE id=@runId", new { runId, accountId }));

    public void FinishSyncRun(long runId, SyncRunResult result) => db.Locked(c =>
    {
        using var tx = c.BeginTransaction();
        var now = Now();
        c.Execute("""
            UPDATE sync_runs SET finished_at=@now, status=@status, playlists_seen=@PlaylistsSeen, tracks_seen=@TracksSeen,
                   matched=@Matched, errors=@Errors, error_message=@ErrorMessage
            WHERE id=@runId
            """, new { runId, now, status = result.Status.ToString(), result.PlaylistsSeen, result.TracksSeen, result.Matched, result.Errors, result.ErrorMessage }, tx);
        if (result.Status is SyncRunStatus.Succeeded or SyncRunStatus.PartiallySucceeded)
            c.Execute("UPDATE remote_accounts SET last_sync_at=@now WHERE id=(SELECT account_id FROM sync_runs WHERE id=@runId)", new { runId, now }, tx);
        tx.Commit();
    });

    public SyncRun? GetLastSyncRun(RemoteProvider provider) => db.Locked(c =>
        c.QuerySingleOrDefault<SyncRunRow>("SELECT * FROM sync_runs WHERE provider=@provider ORDER BY id DESC LIMIT 1", new { provider = Name(provider) })?.ToRun());

    // ───────────────────────── 清理 ─────────────────────────

    /// <summary>退出登录并选择"删除缓存"时：删除该来源的账号、歌单、曲目、匹配结果和同步记录。本地曲目不受影响。</summary>
    public void DeleteProviderData(RemoteProvider provider) => db.Locked(c =>
    {
        using var tx = c.BeginTransaction();
        var p = Name(provider);
        c.Execute("DELETE FROM remote_accounts WHERE provider=@p", new { p }, tx);   // 级联删除歌单和条目
        c.Execute("DELETE FROM remote_tracks WHERE provider=@p", new { p }, tx);     // 级联删除匹配结果
        c.Execute("DELETE FROM sync_runs WHERE provider=@p", new { p }, tx);
        tx.Commit();
    });

    // ───────────────────────── 工具 ─────────────────────────

    private static string Now() => DateTimeOffset.UtcNow.ToString("O");
    private static string Name(RemoteProvider p) => p.ToString();
    private static RemoteProvider Parse(string p) => Enum.Parse<RemoteProvider>(p);
    private static DateTimeOffset? Date(string? s) => s is null ? null : DateTimeOffset.Parse(s, CultureInfo.InvariantCulture);

    /// <summary>远程元数据的摘要，用于判断远端曲目信息是否变化。</summary>
    internal static string Hash(RemoteTrack t)
    {
        var raw = $"{t.Title}\u001f{t.Artist}\u001f{t.Album}\u001f{(long)t.Duration.TotalMilliseconds}\u001f{t.Isrc}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw)), 0, 12);
    }

#pragma warning disable IDE1006 // 列名映射
    private sealed class AccountRow
    {
        public long id { get; init; }
        public string provider { get; init; } = "";
        public string remote_user_id { get; init; } = "";
        public string? nickname { get; init; }
        public string? avatar_url { get; init; }
        public string? last_sync_at { get; init; }
    }

    private sealed class PlaylistRow
    {
        public string provider { get; init; } = "";
        public string playlist_id { get; init; } = "";
        public string name { get; init; } = "";
        public string? description { get; init; }
        public string? cover_url { get; init; }
        public long is_owned { get; init; }
        public long track_count { get; init; }
        public string? remote_updated_at { get; init; }
        public string synced_at { get; init; } = "";
        public long remote_deleted { get; init; }
        public long cached { get; init; }
        public long matched { get; init; }

        public StoredPlaylist ToStored() => new(
            new RemotePlaylist(Parse(provider), playlist_id, name, description, cover_url, is_owned != 0, (int)track_count,
                Date(remote_updated_at) ?? DateTimeOffset.MinValue),
            remote_deleted != 0, Date(synced_at)!.Value, (int)cached, (int)matched);
    }

    private class TrackRow
    {
        public string provider { get; init; } = "";
        public string track_id { get; init; } = "";
        public string title { get; init; } = "";
        public string artist { get; init; } = "";
        public string album { get; init; } = "";
        public long duration_ms { get; init; }
        public string? isrc { get; init; }
        public string? cover_url { get; init; }
        public long playable_online { get; init; }
        public string? remote_updated_at { get; init; }

        public RemoteTrack ToTrack() => new(
            new RemoteTrackId(Parse(provider), track_id), title, artist, album, TimeSpan.FromMilliseconds(duration_ms), isrc, cover_url,
            playable_online != 0, Date(remote_updated_at) ?? DateTimeOffset.MinValue);
    }

    private sealed class EntryRow : TrackRow
    {
        public long position { get; init; }
        public long? local_track_id { get; init; }
        public string? status { get; init; }
        public double? confidence { get; init; }
        public string? reason { get; init; }
        public long? is_manual { get; init; }

        /// <summary>没有映射记录（还没匹配过）时视为未匹配。</summary>
        public RemoteTrackMapping ToMapping()
        {
            var id = new RemoteTrackId(Parse(provider), track_id);
            if (status is null) return new RemoteTrackMapping(id, null, TrackMatchStatus.Unmatched, 0, null);
            var s = Enum.Parse<TrackMatchStatus>(status);
            var local = local_track_id is { } l ? new TrackId(l) : (TrackId?)null;
            // 本地曲目被删除后外键把 local_track_id 置空：原本匹配成功的映射显示为本地文件缺失。
            if (s == TrackMatchStatus.Matched && local is null) s = TrackMatchStatus.LocalFileMissing;
            return new RemoteTrackMapping(id, local, s, confidence ?? 0, reason, is_manual is > 0);
        }
    }

    private sealed class SyncRunRow
    {
        public long id { get; init; }
        public string provider { get; init; } = "";
        public string started_at { get; init; } = "";
        public string? finished_at { get; init; }
        public string status { get; init; } = "";
        public long playlists_seen { get; init; }
        public long tracks_seen { get; init; }
        public long matched { get; init; }
        public long errors { get; init; }
        public string? error_message { get; init; }

        public SyncRun ToRun() => new(id, Parse(provider), Date(started_at)!.Value, Date(finished_at), Enum.Parse<SyncRunStatus>(status),
            (int)playlists_seen, (int)tracks_seen, (int)matched, (int)errors, error_message);
    }
#pragma warning restore IDE1006
}
