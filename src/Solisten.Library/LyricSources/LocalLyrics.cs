using System.Text;
using Solisten.Core.Model;

namespace Solisten.Library.LyricSources;

/// <summary>
/// 本地歌词（不联网）：音频文件旁边的同名 .lrc 文件，或音频文件标签里内嵌的歌词。
/// .lrc 文件的编码按 BOM 判断；没有 BOM 时先按 UTF-8 严格解码，失败再按 GB18030（常见的中文 .lrc 编码）。
/// </summary>
public static class LocalLyrics
{
    static LocalLyrics() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    /// <summary>同名 .lrc 文件（"晴天.flac" → "晴天.lrc"）。</summary>
    public static Lyrics? FromSidecar(string audioPath)
    {
        var lrc = Path.ChangeExtension(audioPath, ".lrc");
        if (!File.Exists(lrc)) return null;
        try
        {
            var bytes = File.ReadAllBytes(lrc);
            if (bytes.Length > 2 * 1024 * 1024) return null; // 不是歌词
            return LrcParser.Parse(Decode(bytes), LyricsSource.LrcFile);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>标签里内嵌的歌词（ID3 USLT、Vorbis LYRICS、MP4 ©lyr…）。内嵌的也可能是带时间标签的 LRC。</summary>
    public static Lyrics? FromTags(string audioPath)
    {
        try
        {
            using var file = TagLib.File.Create(audioPath);
            return LrcParser.Parse(file.Tag.Lyrics, LyricsSource.Embedded);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or TagLib.UnsupportedFormatException or TagLib.CorruptFileException or NotImplementedException)
        {
            return null;
        }
    }

    internal static string Decode(byte[] bytes)
    {
        if (bytes.AsSpan().StartsWith(new byte[] { 0xEF, 0xBB, 0xBF })) return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
        if (bytes.AsSpan().StartsWith(new byte[] { 0xFF, 0xFE })) return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
        if (bytes.AsSpan().StartsWith(new byte[] { 0xFE, 0xFF })) return Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);
        try
        {
            return new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            return Encoding.GetEncoding("GB18030").GetString(bytes);
        }
    }
}
