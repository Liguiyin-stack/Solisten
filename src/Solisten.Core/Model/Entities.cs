using System.Collections.Immutable;

namespace Solisten.Core.Model;

/// <summary>本地曲目的编号是数据库主键（正数）；在线曲目（网易云）用负数，取反即远端歌曲编号。</summary>
public readonly record struct TrackId(long Value)
{
    public override string ToString() => Value.ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>在线曲目：没有本地文件，播放时才向来源请求临时播放地址；不写入曲库数据库。</summary>
    public bool IsRemote => Value < 0;
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

/// <summary>Playlists：外部来源（网易云）的歌单，每个歌单一个分组；内容由宿主提供，不由本地曲目计算。</summary>
public enum Arrangement { Folder, Album, Listening, Playlists }

/// <summary>Remote：外部来源（例如网易云歌单）在本地曲库中的投影，只包含已匹配的本地曲目。</summary>
public enum ScopeKind { All, Group, Search, Remote }

public readonly record struct ScopeKey(ScopeKind Kind, string Key)
{
    public static readonly ScopeKey All = new(ScopeKind.All, "");
    public static ScopeKey Group(string key) => new(ScopeKind.Group, key);
    public static ScopeKey Search(string query) => new(ScopeKind.Search, query);
    public static ScopeKey Remote(string key) => new(ScopeKind.Remote, key);
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
