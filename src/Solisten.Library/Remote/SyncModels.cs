using Solisten.Core.Model;

namespace Solisten.Library.Remote;

public enum SyncRunStatus { Running, Succeeded, PartiallySucceeded, Failed, Cancelled }

/// <summary>一次同步的记录（sync_runs），用于显示上次同步结果和诊断。</summary>
public sealed record SyncRun(
    long Id,
    RemoteProvider Provider,
    DateTimeOffset StartedAt,
    DateTimeOffset? FinishedAt,
    SyncRunStatus Status,
    int PlaylistsSeen,
    int TracksSeen,
    int Matched,
    int Errors,
    string? ErrorMessage);

public sealed record SyncRunResult(SyncRunStatus Status, int PlaylistsSeen, int TracksSeen, int Matched, int Errors, string? ErrorMessage);

/// <summary>本机保存的远程账号摘要（不含任何会话凭据）。</summary>
public sealed record StoredAccount(long Id, RemoteAccount Account, DateTimeOffset? LastSyncAt);

/// <summary>本机缓存的一个远程歌单。<see cref="RemoteDeleted"/> 表示上次同步时远端已经没有这个歌单。</summary>
public sealed record StoredPlaylist(
    RemotePlaylist Playlist,
    bool RemoteDeleted,
    DateTimeOffset SyncedAt,
    int CachedTracks,
    int Matched);

/// <summary>歌单中的一首远程歌曲及它与本地曲目的匹配结果。</summary>
public sealed record RemotePlaylistEntry(int Position, RemoteTrack Track, RemoteTrackMapping Mapping);
