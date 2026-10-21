using System.Text;
using Solisten.Core.Model;
using Solisten.Library.LyricSources;

namespace Solisten.Library.Tests;

public class LrcParserTests
{
    [Fact]
    public void ParsesTimeTagFormats_AndSortsByTime()
    {
        var l = LrcParser.Parse("[00:12.50]第二行\n[00:05]第一行\n[01:02.345]第三行\n[00:20:10]冒号格式", LyricsSource.LrcFile)!;
        Assert.True(l.IsSynced);
        Assert.Equal(["第一行", "第二行", "冒号格式", "第三行"], l.Lines.Select(x => x.Text));
        Assert.Equal(TimeSpan.FromSeconds(5), l.Lines[0].Time);
        Assert.Equal(TimeSpan.FromSeconds(12.5), l.Lines[1].Time);
        Assert.Equal(TimeSpan.FromSeconds(20.10), l.Lines[2].Time);
        Assert.Equal(TimeSpan.FromSeconds(62.345), l.Lines[3].Time);
    }

    [Fact]
    public void RepeatedTags_ProduceOneLinePerTimestamp()
    {
        var l = LrcParser.Parse("[00:10.00][00:40.00]副歌\n[00:20.00]主歌", LyricsSource.LrcFile)!;
        Assert.Equal([10.0, 20.0, 40.0], l.Lines.Select(x => x.Time.TotalSeconds));
        Assert.Equal("副歌", l.Lines[2].Text);
    }

    [Fact]
    public void IgnoresMetadata_JsonLines_AndInstrumentalGaps_AppliesOffset()
    {
        var text = "[ti:晴天]\n[ar:周杰伦]\n[offset:500]\n{\"t\":0,\"c\":[{\"tx\":\"作词\"}]}\n[00:10.00]故事的小黄花\n[00:15.00]\n[00:20.00]从出生那年就飘着";
        var l = LrcParser.Parse(text, LyricsSource.LrcFile)!;
        Assert.Equal(["故事的小黄花", "从出生那年就飘着"], l.Lines.Select(x => x.Text));
        Assert.Equal(TimeSpan.FromSeconds(9.5), l.Lines[0].Time); // 正 offset：歌词提前
    }

    [Fact]
    public void NoTimeTags_IsUnsynced()
    {
        var l = LrcParser.Parse("第一行\r\n\r\n第二行", LyricsSource.Embedded)!;
        Assert.False(l.IsSynced);
        Assert.Equal(["第一行", "第二行"], l.Lines.Select(x => x.Text));
        Assert.Equal(-1, l.IndexAt(TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public void EmptyOrBlank_IsNull()
    {
        Assert.Null(LrcParser.Parse(null, LyricsSource.LrcFile));
        Assert.Null(LrcParser.Parse("  \n[ti:x]\n[00:01.00]", LyricsSource.LrcFile));
    }

    [Fact]
    public void Translation_AttachesToNearestLineWithinTolerance()
    {
        var lrc = "[00:10.000]I love you\n[00:14.000]Every day\n[00:20.000]Goodbye";
        var tr = "[00:09.950]我爱你\n[00:14.200]每一天\n[00:30.000]远处的翻译\n[00:19.990]Goodbye";
        var l = LrcParser.Parse(lrc, LyricsSource.NetEase, tr)!;
        Assert.Equal("我爱你", l.Lines[0].Translation);
        Assert.Equal("每一天", l.Lines[1].Translation);
        Assert.Null(l.Lines[2].Translation); // 和原文相同的"翻译"忽略，太远的不挂
    }

    [Fact]
    public void IndexAt_FindsCurrentLine()
    {
        var l = LrcParser.Parse("[00:10.00]a\n[00:20.00]b\n[00:30.00]c", LyricsSource.LrcFile)!;
        Assert.Equal(-1, l.IndexAt(TimeSpan.FromSeconds(5)));
        Assert.Equal(0, l.IndexAt(TimeSpan.FromSeconds(10)));
        Assert.Equal(1, l.IndexAt(TimeSpan.FromSeconds(29.9)));
        Assert.Equal(2, l.IndexAt(TimeSpan.FromMinutes(5)));
    }
}

public sealed class LocalLyricsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "solisten-lyrics-tests", Guid.NewGuid().ToString("N"));

    public LocalLyricsTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    [Theory]
    [InlineData("utf8bom")]
    [InlineData("utf8")]
    [InlineData("utf16")]
    [InlineData("gb18030")]
    public void Sidecar_DetectsEncoding(string encoding)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var text = "[00:01.00]故事的小黄花\n[00:05.00]从出生那年就飘着";
        byte[] bytes = encoding switch
        {
            "utf8bom" => [.. Encoding.UTF8.GetPreamble(), .. Encoding.UTF8.GetBytes(text)],
            "utf8" => Encoding.UTF8.GetBytes(text),
            "utf16" => [.. Encoding.Unicode.GetPreamble(), .. Encoding.Unicode.GetBytes(text)],
            _ => Encoding.GetEncoding("GB18030").GetBytes(text),
        };
        var audio = Path.Combine(_dir, "晴天.flac");
        File.WriteAllBytes(Path.Combine(_dir, "晴天.lrc"), bytes);

        var l = LocalLyrics.FromSidecar(audio)!;
        Assert.Equal(LyricsSource.LrcFile, l.Source);
        Assert.Equal("故事的小黄花", l.Lines[0].Text);
    }

    [Fact]
    public void Sidecar_Missing_IsNull() => Assert.Null(LocalLyrics.FromSidecar(Path.Combine(_dir, "none.mp3")));

    [Fact]
    public void Tags_UnreadableFile_IsNull()
    {
        var bad = Path.Combine(_dir, "bad.mp3");
        File.WriteAllBytes(bad, [1, 2, 3]);
        Assert.Null(LocalLyrics.FromTags(bad));
    }
}
