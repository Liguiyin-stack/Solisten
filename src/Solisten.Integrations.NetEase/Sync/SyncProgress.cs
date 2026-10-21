using Solisten.Library.Remote;

namespace Solisten.Integrations.NetEase.Sync;

public enum SyncStage { Validating, FetchingPlaylists, FetchingTracks, Matching, Completed }

/// <summary>同步进度（"歌单 3 / 8 · 歌曲 126 / 420 · 已匹配 87 首"）。</summary>
public sealed record SyncProgress(
    SyncStage Stage,
    int PlaylistsDone,
    int PlaylistsTotal,
    int TracksSeen,
    int TracksTotal,
    int Matched);

/// <summary>
/// 同步哪些歌单（设计文档 §8.1）：默认只同步用户创建的歌单，加上用户明确勾选的收藏歌单。
/// 其他收藏歌单只记录名称，供"选择歌单"使用，不拉取曲目。
/// </summary>
public sealed record SyncOptions(IReadOnlySet<string> SelectedPlaylistIds)
{
    public static readonly SyncOptions OwnedOnly = new(new HashSet<string>());
}

public sealed record SyncSummary(
    SyncRunStatus Status,
    int Playlists,
    int Tracks,
    int Matched,
    int Unmatched,
    int Errors,
    IReadOnlyList<string> FailedPlaylists,
    string? Message);
