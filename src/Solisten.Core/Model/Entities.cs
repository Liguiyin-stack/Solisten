using System.Collections.Immutable;

namespace Solisten.Core.Model;

public readonly record struct TrackId(long Value)
{
    public override string ToString() => Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
}

public sealed record TrackMeta(
    string Title,
    string Artist,
    string AlbumArtist,
    string Album,
    int Disc,
    int TrackNo,
    int? Year,
    TimeSpan Duration);

public sealed record AudioFormat(string Codec, int SampleRate, int BitDepth, int Channels);

/// <summary>分组键。Folder 是相对曲库根目录的文件夹路径；Album 是（专辑艺术家 ‖ 艺术家, 专辑名）。</summary>
public sealed record GroupKeys(string Folder, string FolderDisplay, string Album, string AlbumDisplay);

public sealed record Track(
    TrackId Id,
    string Path,
    TrackMeta Meta,
    AudioFormat Format,
    GroupKeys Groups,
    bool Available = true)
{
    public string FileName => System.IO.Path.GetFileName(Path);
}

/// <summary>Solisten 自己记录的用户数据，从不写回音频文件。</summary>
public sealed record UserTrackData(int PlayCount, DateTimeOffset? LastPlayedAt, bool Favorite)
{
    public static readonly UserTrackData Empty = new(0, null, false);
}

public enum Arrangement { Folder, Album, Listening }

public enum ScopeKind { All, Group, Search }

public readonly record struct ScopeKey(ScopeKind Kind, string Key)
{
    public static readonly ScopeKey All = new(ScopeKind.All, "");
    public static ScopeKey Group(string key) => new(ScopeKind.Group, key);
    public static ScopeKey Search(string query) => new(ScopeKind.Search, query);
}

/// <summary>上下文内的一段连续分组（专辑/文件夹/聆听记录分段）。</summary>
public sealed record ContextGroup(string Key, string DisplayName, int Start, int Count)
{
    public int End => Start + Count;
}

/// <summary>一个有序的歌曲序列；只在确认播放时复制为快照。Order 可能包含重复项（聆听记录的分段可重叠）。</summary>
public sealed record PlaybackContext(
    Arrangement Arrangement,
    ScopeKey Scope,
    ImmutableArray<TrackId> Order,
    ImmutableArray<ContextGroup> Groups)
{
    public static readonly PlaybackContext Empty =
        new(Arrangement.Folder, ScopeKey.All, ImmutableArray<TrackId>.Empty, ImmutableArray<ContextGroup>.Empty);

    public int Count => Order.Length;
    public bool IsEmpty => Order.IsDefaultOrEmpty;

    public int GroupIndexAt(int index)
    {
        for (int g = 0; g < Groups.Length; g++)
        {
            if (index >= Groups[g].Start && index < Groups[g].End) return g;
        }
        return -1;
    }

    public ContextGroup? GroupAt(int index)
    {
        int g = GroupIndexAt(index);
        return g < 0 ? null : Groups[g];
    }

    /// <summary>把某个分组切出来，作为独立上下文（确认播放时的快照范围）。</summary>
    public PlaybackContext SliceGroup(int groupIndex)
    {
        var g = Groups[groupIndex];
        return new PlaybackContext(
            Arrangement,
            ScopeKey.Group(g.Key),
            Order.Slice(g.Start, g.Count),
            [g with { Start = 0 }]);
    }

    public int IndexOf(TrackId id, int preferredNear = 0)
    {
        if (IsEmpty) return -1;
        if (preferredNear >= 0 && preferredNear < Order.Length && Order[preferredNear] == id) return preferredNear;
        return Order.IndexOf(id);
    }
}
