namespace Solisten.Core.Model;

public enum LyricsSource { LrcFile, Embedded, NetEase }

/// <summary>一行歌词。<see cref="Translation"/> 是同一时刻的翻译（例如外文歌的中文翻译）。</summary>
public sealed record LyricLine(TimeSpan Time, string Text, string? Translation = null);

/// <summary>
/// 一首歌的歌词。<see cref="IsSynced"/> 为 false 时没有时间轴（只能整段显示，不随播放滚动）。
/// 同步歌词的行按时间升序。
/// </summary>
public sealed record Lyrics(IReadOnlyList<LyricLine> Lines, bool IsSynced, LyricsSource Source)
{
    public bool IsEmpty => Lines.Count == 0;

    /// <summary>某一时刻正在唱的那一行（不晚于 <paramref name="t"/> 的最后一行）；在第一行之前或没有时间轴时为 −1。</summary>
    public int IndexAt(TimeSpan t)
    {
        if (!IsSynced) return -1;
        int lo = 0, hi = Lines.Count - 1, found = -1;
        while (lo <= hi)
        {
            int mid = (lo + hi) >>> 1;
            if (Lines[mid].Time <= t) { found = mid; lo = mid + 1; }
            else hi = mid - 1;
        }
        return found;
    }
}
