using System.Collections.Immutable;
using Solisten.Core.Sorting;

namespace Solisten.Core.Model;

/// <summary>曲库的不可变快照。排列结果按需计算并缓存。</summary>
public sealed class LibraryIndex
{
    public static readonly LibraryIndex Empty = new(ImmutableDictionary<TrackId, Track>.Empty, ImmutableDictionary<TrackId, UserTrackData>.Empty);

    private readonly Lazy<PlaybackContext>[] _arrangements;

    public LibraryIndex(ImmutableDictionary<TrackId, Track> tracks, ImmutableDictionary<TrackId, UserTrackData> userData)
    {
        Tracks = tracks;
        UserData = userData;
        _arrangements = Enum.GetValues<Arrangement>()
            .Select(a => new Lazy<PlaybackContext>(() => Arrangements.Build(a, Tracks.Values.ToArray(), User)))
            .ToArray();
    }

    public static LibraryIndex From(IEnumerable<Track> tracks, IEnumerable<KeyValuePair<TrackId, UserTrackData>>? userData = null) =>
        new(tracks.ToImmutableDictionary(t => t.Id), (userData ?? []).ToImmutableDictionary());

    public ImmutableDictionary<TrackId, Track> Tracks { get; }
    public ImmutableDictionary<TrackId, UserTrackData> UserData { get; }

    public int Count => Tracks.Count;
    public bool IsEmpty => Tracks.IsEmpty;

    public Track? Find(TrackId id) => Tracks.GetValueOrDefault(id);

    public bool IsAvailable(TrackId id) => Tracks.TryGetValue(id, out var t) && t.Available;

    public UserTrackData User(TrackId id) => UserData.GetValueOrDefault(id) ?? UserTrackData.Empty;

    public PlaybackContext Arrange(Arrangement arrangement) => _arrangements[(int)arrangement].Value;

    public LibraryIndex WithUserData(TrackId id, UserTrackData data) => new(Tracks, UserData.SetItem(id, data));
}
