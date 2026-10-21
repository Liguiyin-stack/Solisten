using System.Collections.Immutable;
using Solisten.Core.Sorting;

namespace Solisten.Core.Model;

/// <summary>
/// 曲库的不可变快照。排列结果按需计算并缓存。
/// <see cref="Remote"/> 是在线曲目（网易云歌单中的歌）：可以查找、播放，但不参与本地的文件夹 / 专辑 / 聆听记录排列。
/// </summary>
public sealed class LibraryIndex
{
    public static readonly LibraryIndex Empty = new(ImmutableDictionary<TrackId, Track>.Empty, ImmutableDictionary<TrackId, UserTrackData>.Empty);

    private readonly Lazy<PlaybackContext>[] _arrangements;

    public LibraryIndex(ImmutableDictionary<TrackId, Track> tracks, ImmutableDictionary<TrackId, UserTrackData> userData,
        ImmutableDictionary<TrackId, Track>? remote = null, PlaybackContext? playlists = null)
    {
        Tracks = tracks;
        UserData = userData;
        Remote = remote ?? ImmutableDictionary<TrackId, Track>.Empty;
        Playlists = playlists ?? PlaybackContext.Empty with { Arrangement = Arrangement.Playlists };
        _arrangements = Enum.GetValues<Arrangement>()
            .Select(a => new Lazy<PlaybackContext>(() => a == Arrangement.Playlists ? Playlists : Arrangements.Build(a, Tracks.Values.ToArray(), User)))
            .ToArray();
    }

    public static LibraryIndex From(IEnumerable<Track> tracks, IEnumerable<KeyValuePair<TrackId, UserTrackData>>? userData = null) =>
        new(tracks.ToImmutableDictionary(t => t.Id), (userData ?? []).ToImmutableDictionary());

    /// <summary>本地曲目。</summary>
    public ImmutableDictionary<TrackId, Track> Tracks { get; }
    public ImmutableDictionary<TrackId, UserTrackData> UserData { get; }
    /// <summary>在线曲目（编号为负数）。</summary>
    public ImmutableDictionary<TrackId, Track> Remote { get; }
    /// <summary>外部来源的歌单排列（每个歌单一个分组），由宿主提供；可以包含本地曲目和在线曲目。</summary>
    public PlaybackContext Playlists { get; }

    /// <summary>本地曲目数量。</summary>
    public int Count => Tracks.Count;
    /// <summary>没有本地曲目（在线曲目不算：没有本地曲库时仍显示首次导入引导）。</summary>
    public bool IsEmpty => Tracks.IsEmpty;

    public Track? Find(TrackId id) => id.IsRemote ? Remote.GetValueOrDefault(id) : Tracks.GetValueOrDefault(id);

    public bool IsAvailable(TrackId id) => Find(id) is { Available: true };

    public UserTrackData User(TrackId id) => UserData.GetValueOrDefault(id) ?? UserTrackData.Empty;

    public PlaybackContext Arrange(Arrangement arrangement) => _arrangements[(int)arrangement].Value;

    public LibraryIndex WithUserData(TrackId id, UserTrackData data) => new(Tracks, UserData.SetItem(id, data), Remote, Playlists);

    /// <summary>替换在线曲目和外部歌单。本地的文件夹 / 专辑 / 聆听记录排列只取决于本地曲目。</summary>
    public LibraryIndex WithRemote(ImmutableDictionary<TrackId, Track> remote, PlaybackContext? playlists = null) => new(Tracks, UserData, remote, playlists);
}
