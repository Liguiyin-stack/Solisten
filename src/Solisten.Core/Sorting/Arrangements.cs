using System.Collections.Immutable;
using Solisten.Core.Model;

namespace Solisten.Core.Sorting;

/// <summary>§6.2 分组规则：把曲库排列为单轴连续、分段的上下文。</summary>
public static class Arrangements
{
    public const string RecentKey = "listening:recent";
    public const string FavoritesKey = "listening:favorites";
    public const string UnplayedKey = "listening:unplayed";
    public const int RecentLimit = 200;

    public static PlaybackContext Build(
        Arrangement arrangement,
        IReadOnlyCollection<Track> tracks,
        Func<TrackId, UserTrackData> userData)
    {
        return arrangement switch
        {
            Arrangement.Folder => Grouped(arrangement,
                tracks.GroupBy(t => t.Groups.Folder)
                      .OrderBy(g => g.Key, NaturalComparer.Instance)
                      .Select(g => (g.Key, g.First().Groups.FolderDisplay, (IEnumerable<Track>)g.OrderBy(t => t.FileName, NaturalComparer.Instance)))),
            Arrangement.Album => Grouped(arrangement,
                tracks.GroupBy(t => t.Groups.Album)
                      .OrderBy(g => g.Key, NaturalComparer.Instance)
                      .Select(g => (g.Key, g.First().Groups.AlbumDisplay, (IEnumerable<Track>)g.OrderBy(t => t, AlbumTrackComparer.Instance)))),
            Arrangement.Listening => Listening(tracks, userData),
            _ => throw new ArgumentOutOfRangeException(nameof(arrangement)),
        };
    }

    private static PlaybackContext Listening(IReadOnlyCollection<Track> tracks, Func<TrackId, UserTrackData> userData)
    {
        var folderOrder = Build(Arrangement.Folder, tracks, userData).Order;
        var byId = tracks.ToDictionary(t => t.Id);
        var ordered = folderOrder.Select(id => byId[id]).ToList();

        var recent = ordered
            .Where(t => userData(t.Id).LastPlayedAt is not null)
            .OrderByDescending(t => userData(t.Id).LastPlayedAt)
            .Take(RecentLimit);
        var favorites = ordered.Where(t => userData(t.Id).Favorite);
        var unplayed = ordered.Where(t => userData(t.Id).PlayCount == 0);

        return Grouped(Arrangement.Listening,
        [
            (RecentKey, "最近播放", recent),
            (FavoritesKey, "收藏", favorites),
            (UnplayedKey, "未听过", unplayed),
        ]);
    }

    private static PlaybackContext Grouped(
        Arrangement arrangement,
        IEnumerable<(string Key, string Display, IEnumerable<Track> Tracks)> groups)
    {
        var order = ImmutableArray.CreateBuilder<TrackId>();
        var ctxGroups = ImmutableArray.CreateBuilder<ContextGroup>();
        foreach (var (key, display, items) in groups)
        {
            int start = order.Count;
            foreach (var t in items) order.Add(t.Id);
            int count = order.Count - start;
            if (count > 0) ctxGroups.Add(new ContextGroup(key, display, start, count));
        }
        return new PlaybackContext(arrangement, ScopeKey.All, order.ToImmutable(), ctxGroups.ToImmutable());
    }

    /// <summary>专辑组内：（碟号, 曲号, 文件名）。</summary>
    public sealed class AlbumTrackComparer : IComparer<Track>
    {
        public static readonly AlbumTrackComparer Instance = new();

        public int Compare(Track? x, Track? y)
        {
            if (ReferenceEquals(x, y)) return 0;
            if (x is null) return -1;
            if (y is null) return 1;
            int c = x.Meta.Disc.CompareTo(y.Meta.Disc);
            if (c != 0) return c;
            c = x.Meta.TrackNo.CompareTo(y.Meta.TrackNo);
            if (c != 0) return c;
            return NaturalComparer.CompareStrings(x.FileName, y.FileName);
        }
    }

    /// <summary>专辑分组键：（专辑艺术家 ‖ 艺术家, 专辑名）。</summary>
    public static (string Key, string Display) AlbumKey(string albumArtist, string artist, string album)
    {
        var who = string.IsNullOrWhiteSpace(albumArtist) ? artist : albumArtist;
        return ($"{who.Trim().ToLowerInvariant()}\u001f{album.Trim().ToLowerInvariant()}", $"{album} · {who}");
    }
}
