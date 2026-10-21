using System.Text;
using Solisten.Core.Model;

namespace Solisten.Library.Remote;

/// <summary>某首远程歌曲的一个本地候选。</summary>
public sealed record MatchCandidate(TrackId Track, double Confidence, string Reason);

/// <summary>
/// 远程曲目 ↔ 本地曲目匹配（设计文档 §9）。纯函数，不访问网络和数据库。
/// 规则偏保守：宁可标记为"需要确认"，也不把版本不同的歌（现场版、混音版）自动当成同一首。
/// 本地曲库目前不保存 ISRC，所以匹配从"清洗后的标题 + 主艺术家 + 时长差"开始。
/// </summary>
public sealed class TrackMatcher
{
    /// <summary>时长差在这个范围内为高置信。</summary>
    public static readonly TimeSpan CloseDuration = TimeSpan.FromSeconds(2);
    /// <summary>时长差在这个范围内为中置信，需要用户确认。</summary>
    public static readonly TimeSpan NearDuration = TimeSpan.FromSeconds(5);
    /// <summary>达到这个置信度才会自动标记为已匹配。</summary>
    public const double AutoMatchThreshold = 0.85;

    private readonly LibraryIndex _index;
    private readonly Dictionary<string, List<Track>> _byTitle = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<Track>> _byFileName = new(StringComparer.Ordinal);

    public TrackMatcher(LibraryIndex index)
    {
        _index = index;
        foreach (var t in index.Tracks.Values)
        {
            Add(_byTitle, Normalize(t.Meta.Title), t);
            foreach (var key in FileNameTitles(t.Path)) Add(_byFileName, key, t);
        }
    }

    /// <summary>
    /// 计算一首远程歌曲的映射。<paramref name="previous"/> 是上次的结果：
    /// 用户手动选择且本地文件仍然可用的映射原样保留；原来匹配成功但本地文件已经不在的显示为本地文件缺失。
    /// </summary>
    public RemoteTrackMapping Match(RemoteTrack remote, RemoteTrackMapping? previous = null)
    {
        if (previous is { IsManual: true, LocalTrack: { } manual })
        {
            return _index.IsAvailable(manual)
                ? previous with { Status = TrackMatchStatus.Matched }
                : previous with { Status = TrackMatchStatus.LocalFileMissing, Reason = "本地文件不可用" };
        }

        var candidates = Candidates(remote);
        if (candidates.Count > 0)
        {
            var best = candidates[0];
            bool unique = candidates.Count == 1 || best.Confidence - candidates[1].Confidence >= 0.03;
            if (best.Confidence >= AutoMatchThreshold && unique)
                return new RemoteTrackMapping(remote.Id, best.Track, TrackMatchStatus.Matched, best.Confidence, best.Reason);
            if (best.Confidence >= AutoMatchThreshold)
                return new RemoteTrackMapping(remote.Id, null, TrackMatchStatus.Ambiguous, best.Confidence, $"有 {candidates.Count} 个本地候选");
            // 只有一个候选但时长或艺术家对不上：元数据冲突，由用户决定。
            var status = candidates.Count == 1 ? TrackMatchStatus.MetadataConflict : TrackMatchStatus.Ambiguous;
            return new RemoteTrackMapping(remote.Id, null, status, best.Confidence, best.Reason);
        }

        if (previous is { Status: TrackMatchStatus.Matched or TrackMatchStatus.LocalFileMissing, LocalTrack: { } old } && !_index.IsAvailable(old))
            return new RemoteTrackMapping(remote.Id, old, TrackMatchStatus.LocalFileMissing, 0, "本地文件不可用");
        if (previous is { Status: TrackMatchStatus.LocalFileMissing })
            return previous with { Reason = "本地文件不可用" };
        return new RemoteTrackMapping(remote.Id, null, TrackMatchStatus.Unmatched, 0, null);
    }

    /// <summary>按置信度从高到低排列的候选（用于"有多个候选：点击选择"）。不可用的本地文件不参与匹配。</summary>
    public IReadOnlyList<MatchCandidate> Candidates(RemoteTrack remote)
    {
        var title = Normalize(remote.Title);
        if (title.Length == 0) return [];
        var remoteArtists = SplitArtists(remote.Artist);
        var album = Normalize(remote.Album);

        var result = new Dictionary<TrackId, MatchCandidate>();
        void Consider(Track t, bool byFileName)
        {
            if (!t.Available) return;
            bool artist = ArtistsOverlap(remoteArtists, t);
            var diff = DurationDiff(remote.Duration, t.Meta.Duration);
            bool sameAlbum = album.Length > 0 && Normalize(t.Meta.Album) == album;

            double confidence;
            string reason;
            if (artist && diff <= CloseDuration)
            {
                confidence = byFileName ? 0.86 : 0.95;
                if (sameAlbum) confidence += 0.03;
                reason = byFileName ? "文件名、艺术家和时长一致" : sameAlbum ? "标题、艺术家、专辑和时长一致" : "标题、艺术家和时长一致";
            }
            else if (artist && diff <= NearDuration)
            {
                confidence = 0.7;
                reason = $"时长相差 {diff.TotalSeconds:0} 秒，需要确认";
            }
            else if (artist)
            {
                confidence = 0.45;
                reason = diff == TimeSpan.MaxValue ? "缺少时长信息，需要确认" : "时长差异较大，可能是不同版本";
            }
            else
            {
                confidence = 0.3;
                reason = "只有标题一致";
            }

            if (!result.TryGetValue(t.Id, out var existing) || existing.Confidence < confidence)
                result[t.Id] = new MatchCandidate(t.Id, confidence, reason);
        }

        if (_byTitle.TryGetValue(title, out var byTitle)) foreach (var t in byTitle) Consider(t, byFileName: false);
        if (_byFileName.TryGetValue(title, out var byFile)) foreach (var t in byFile) Consider(t, byFileName: true);

        return [.. result.Values.OrderByDescending(c => c.Confidence).ThenBy(c => c.Track.Value)];
    }

    // ───────────────────────── 文本清洗（§9.2） ─────────────────────────

    /// <summary>
    /// 标题 / 专辑清洗：Unicode NFKC（全角转半角）、小写、统一中英文括号和空白。
    /// 括号里的版本信息（Live、Remix、DJ 版…）保留，它们参与比较，避免不同版本误匹配。
    /// </summary>
    public static string Normalize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        var s = text.Normalize(NormalizationForm.FormKC).ToLowerInvariant();
        var sb = new StringBuilder(s.Length);
        bool space = false;
        foreach (var ch in s)
        {
            char c = ch switch
            {
                '【' or '「' or '『' or '〔' or '［' or '{' => '[',
                '】' or '」' or '』' or '〕' or '］' or '}' => ']',
                '（' => '(',
                '）' => ')',
                '‘' or '’' or '`' or '´' => '\'',
                '“' or '”' => '"',
                '–' or '—' or '－' => '-',
                _ => ch,
            };
            if (char.IsWhiteSpace(c))
            {
                space = true;
                continue;
            }
            // 空白折叠为一个空格；括号两侧不留空格（"晴天 (Live)" 与 "晴天(Live)" 相同）。
            if (space && sb.Length > 0 && !IsBracket(c) && !IsBracket(sb[^1])) sb.Append(' ');
            space = false;
            sb.Append(c);
        }
        return sb.ToString();

        static bool IsBracket(char c) => c is '(' or ')' or '[' or ']';
    }

    private static readonly string[] ArtistSeparators = ["/", "、", "&", ",", "，", ";", "；", " feat. ", " feat ", " ft. ", " featuring ", " x ", " × "];

    /// <summary>艺术家拆分成集合比较（"A / B"、"A、B"、"A feat. B" 都包含 A 和 B），不简单删除分隔符。</summary>
    public static IReadOnlySet<string> SplitArtists(string? artists)
    {
        var s = $" {Normalize(artists)} ";
        foreach (var sep in ArtistSeparators) s = s.Replace(sep, "\u001f", StringComparison.Ordinal);
        return s.Split('\u001f', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(a => a.Length > 0)
            .ToHashSet(StringComparer.Ordinal);
    }

    private static bool ArtistsOverlap(IReadOnlySet<string> remote, Track local)
    {
        if (remote.Count == 0) return false;
        var artists = SplitArtists(local.Meta.Artist);
        if (artists.Overlaps(remote)) return true;
        return SplitArtists(local.Meta.AlbumArtist).Overlaps(remote);
    }

    private static TimeSpan DurationDiff(TimeSpan a, TimeSpan b) =>
        a <= TimeSpan.Zero || b <= TimeSpan.Zero ? TimeSpan.MaxValue : (a - b).Duration();

    /// <summary>文件名里可能的标题："晴天.flac" → "晴天"；"周杰伦 - 晴天.flac" / "01. 晴天.flac" → 也取 " - " 或序号之后的部分。</summary>
    internal static IEnumerable<string> FileNameTitles(string path)
    {
        var stem = Normalize(Path.GetFileNameWithoutExtension(path));
        if (stem.Length == 0) yield break;
        yield return stem;
        int dash = stem.LastIndexOf(" - ", StringComparison.Ordinal);
        if (dash > 0 && dash + 3 < stem.Length) yield return stem[(dash + 3)..];
        int i = 0;
        while (i < stem.Length && char.IsAsciiDigit(stem[i])) i++;
        if (i > 0 && i < stem.Length)
        {
            var rest = stem[i..].TrimStart('.', '-', '_', ' ');
            if (rest.Length > 0 && rest != stem) yield return rest;
        }
    }

    private static void Add(Dictionary<string, List<Track>> map, string key, Track t)
    {
        if (key.Length == 0) return;
        if (!map.TryGetValue(key, out var list)) map[key] = list = [];
        if (!list.Contains(t)) list.Add(t);
    }
}
