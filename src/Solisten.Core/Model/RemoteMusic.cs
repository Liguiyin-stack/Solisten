namespace Solisten.Core.Model;

/// <summary>
/// 远程音乐来源（网易云等）的数据模型。只描述元数据，不包含网络、Cookie 或 JSON 依赖。
/// 远程曲目与本地 <see cref="Track"/> 分开建模：只有匹配到本地文件的远程曲目才能进入播放上下文。
/// </summary>
public enum RemoteProvider
{
    NetEase,
}

public readonly record struct RemoteTrackId(RemoteProvider Provider, string Value)
{
    public override string ToString() => $"{Provider}:{Value}";
}

public sealed record RemoteAccount(
    RemoteProvider Provider,
    string UserId,
    string Nickname,
    string? AvatarUrl);

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
    MetadataConflict,
}

/// <summary>远程曲目 ↔ 本地曲目的映射。<see cref="IsManual"/> 表示由用户亲自选择，重新匹配时保留。</summary>
public sealed record RemoteTrackMapping(
    RemoteTrackId Remote,
    TrackId? LocalTrack,
    TrackMatchStatus Status,
    double Confidence,
    string? Reason,
    bool IsManual = false)
{
    /// <summary>只有匹配成功且本地文件可用的曲目才能播放。</summary>
    public bool IsPlayable(LibraryIndex index) =>
        Status == TrackMatchStatus.Matched && LocalTrack is { } id && index.IsAvailable(id);
}
