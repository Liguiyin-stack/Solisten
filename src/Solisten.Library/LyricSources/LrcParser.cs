using System.Globalization;
using System.Text.RegularExpressions;
using Solisten.Core.Model;

namespace Solisten.Library.LyricSources;

/// <summary>
/// LRC 歌词解析：
/// <list type="bullet">
/// <item>时间标签 [mm:ss]、[mm:ss.x]、[mm:ss.xx]、[mm:ss.xxx]、[mm:ss:xx]，一行可以有多个标签（重复的副歌）；</item>
/// <item>[offset:±毫秒] 整体偏移（正数表示歌词提前）；[ti:]、[ar:] 等元数据标签忽略；</item>
/// <item>网易云在歌词里夹带的 JSON 行（{"t":…}）忽略；空白歌词行（间奏）丢弃；</item>
/// <item>完全没有时间标签时作为无时间轴歌词（逐行显示，不滚动）。</item>
/// </list>
/// </summary>
public static partial class LrcParser
{
    [GeneratedRegex(@"\[(\d{1,3}):(\d{1,2})(?:[.:](\d{1,3}))?\]")]
    private static partial Regex TimeTag();

    [GeneratedRegex(@"^\[offset:\s*([+-]?\d+)\s*\]", RegexOptions.IgnoreCase)]
    private static partial Regex OffsetTag();

    [GeneratedRegex(@"^\[[a-zA-Z#]+:[^\]]*\]\s*$")]
    private static partial Regex MetaTag();

    /// <summary>翻译与原文时间相差在这个范围内视为同一行（网易云的翻译常常比原文早或晚几十毫秒）。</summary>
    public static readonly TimeSpan TranslationTolerance = TimeSpan.FromSeconds(0.6);

    public static Lyrics? Parse(string? text, LyricsSource source, string? translation = null)
    {
        var (lines, synced) = ParseLines(text);
        if (lines.Count == 0) return null;
        if (synced && !string.IsNullOrWhiteSpace(translation))
        {
            var (trans, transSynced) = ParseLines(translation);
            if (transSynced) lines = MergeTranslation(lines, trans);
        }
        return new Lyrics(lines, synced, source);
    }

    private static (List<LyricLine> Lines, bool Synced) ParseLines(string? text)
    {
        var timed = new List<LyricLine>();
        var plain = new List<LyricLine>();
        if (string.IsNullOrWhiteSpace(text)) return (timed, false);

        long offsetMs = 0;
        foreach (var raw in text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] == '{') continue;
            if (OffsetTag().Match(line) is { Success: true } off)
            {
                offsetMs = long.Parse(off.Groups[1].Value, CultureInfo.InvariantCulture);
                continue;
            }
            if (MetaTag().IsMatch(line)) continue;

            var tags = new List<TimeSpan>();
            int pos = 0;
            while (TimeTag().Match(line, pos) is { Success: true } m && m.Index == pos)
            {
                tags.Add(ToTime(m));
                pos = m.Index + m.Length;
                while (pos < line.Length && line[pos] == ' ') pos++;
            }
            var content = line[pos..].Trim();
            if (tags.Count == 0)
            {
                if (content.Length > 0) plain.Add(new LyricLine(TimeSpan.Zero, content));
                continue;
            }
            if (content.Length == 0) continue; // 间奏
            foreach (var t in tags) timed.Add(new LyricLine(t, content));
        }

        if (timed.Count == 0) return (plain, false);
        var shift = TimeSpan.FromMilliseconds(offsetMs);
        var result = timed
            .Select(l => l with { Time = l.Time - shift < TimeSpan.Zero ? TimeSpan.Zero : l.Time - shift })
            .OrderBy(l => l.Time) // 稳定排序：同一时刻保持原来的先后
            .ToList();
        return (result, true);
    }

    private static TimeSpan ToTime(Match m)
    {
        int min = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
        int sec = int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
        double frac = 0;
        if (m.Groups[3].Success)
        {
            var f = m.Groups[3].Value;
            frac = int.Parse(f, CultureInfo.InvariantCulture) / Math.Pow(10, f.Length);
        }
        return TimeSpan.FromSeconds(min * 60 + sec + frac);
    }

    /// <summary>每行翻译挂到时间最接近（容差内）且还没有翻译的原文行上；和原文完全相同的"翻译"忽略。</summary>
    private static List<LyricLine> MergeTranslation(List<LyricLine> lines, List<LyricLine> translations)
    {
        var result = lines.ToArray();
        foreach (var t in translations)
        {
            int best = -1;
            var bestDiff = TranslationTolerance;
            for (int i = 0; i < result.Length; i++)
            {
                if (result[i].Translation is not null) continue;
                var diff = (result[i].Time - t.Time).Duration();
                if (diff <= bestDiff) { best = i; bestDiff = diff; }
                if (result[i].Time > t.Time + TranslationTolerance) break;
            }
            if (best >= 0 && !string.Equals(result[best].Text, t.Text, StringComparison.Ordinal))
                result[best] = result[best] with { Translation = t.Text };
        }
        return [.. result];
    }
}
