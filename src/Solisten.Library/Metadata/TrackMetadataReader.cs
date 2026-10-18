using Solisten.Core.Model;

namespace Solisten.Library.Metadata;

public sealed record EmbeddedCover(byte[] Data, string MimeType);

public sealed record ReadMetadata(TrackMeta Meta, AudioFormat Format, EmbeddedCover? Cover);

/// <summary>§6.1 ②③：只读地读取标签；缺失字段按文件夹结构兜底。</summary>
public static class TrackMetadataReader
{
    public const string Unknown = "未知";

    public static readonly IReadOnlySet<string> SupportedExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        ".flac", ".m4a", ".mp4", ".alac", ".wav", ".aif", ".aiff", ".mp3", ".aac", ".ogg", ".oga", ".opus",
    };

    public static bool IsSupported(string path) => SupportedExtensions.Contains(Path.GetExtension(path));

    /// <param name="rootPath">曲库根目录；用于判断"上上级文件夹"是否仍在曲库内。</param>
    public static ReadMetadata Read(string path, string rootPath)
    {
        string? title = null, artist = null, albumArtist = null, album = null;
        int disc = 0, trackNo = 0;
        int? year = null;
        TimeSpan duration = TimeSpan.Zero;
        var format = new AudioFormat(Path.GetExtension(path).TrimStart('.').ToLowerInvariant(), 0, 0, 0);
        EmbeddedCover? cover = null;

        try
        {
            using var file = TagLib.File.Create(new ReadOnlyFile(path), TagLib.ReadStyle.Average);
            var tag = file.Tag;
            title = Clean(tag.Title);
            artist = Clean(tag.JoinedPerformers);
            albumArtist = Clean(tag.JoinedAlbumArtists);
            album = Clean(tag.Album);
            disc = (int)tag.Disc;
            trackNo = (int)tag.Track;
            year = tag.Year > 0 ? (int)tag.Year : null;

            var props = file.Properties;
            if (props is not null)
            {
                duration = props.Duration;
                var codec = props.Codecs.FirstOrDefault(c => c is TagLib.IAudioCodec);
                format = new AudioFormat(
                    CodecName(codec?.Description, format.Codec),
                    props.AudioSampleRate,
                    props.BitsPerSample,
                    props.AudioChannels);
            }

            var pic = tag.Pictures.FirstOrDefault(p => p.Type == TagLib.PictureType.FrontCover) ?? tag.Pictures.FirstOrDefault();
            if (pic is not null && pic.Data.Count > 0)
                cover = new EmbeddedCover(pic.Data.Data, pic.MimeType ?? "image/jpeg");
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            // 标签不可读（损坏、格式不支持、TagLib 内部错误）时仍然导入，缺失字段使用兜底规则。
        }

        var (fallbackAlbum, fallbackArtist) = FolderFallback(path, rootPath);
        var meta = new TrackMeta(
            Title: title ?? Path.GetFileNameWithoutExtension(path),
            Artist: artist ?? albumArtist ?? fallbackArtist,
            AlbumArtist: albumArtist ?? "",
            Album: album ?? fallbackAlbum,
            Disc: disc,
            TrackNo: trackNo,
            Year: year,
            Duration: duration);
        return new ReadMetadata(meta, format, cover);
    }

    /// <summary>专辑 = 父文件夹名；艺术家 = 上上级文件夹名（必须仍在曲库根目录之内），否则"未知"。</summary>
    public static (string Album, string Artist) FolderFallback(string path, string rootPath)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(rootPath));
        var parent = Path.GetDirectoryName(Path.GetFullPath(path)) ?? root;
        var album = Path.GetFileName(parent);
        if (string.IsNullOrEmpty(album)) album = Unknown;

        var grand = Path.GetDirectoryName(parent);
        bool grandInsideRoot = grand is not null && grand.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        var artist = grandInsideRoot ? Path.GetFileName(grand!) : Unknown;
        return (album, artist);
    }

    private static string? Clean(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    private static string CodecName(string? description, string extension)
    {
        if (string.IsNullOrEmpty(description)) return extension;
        var d = description.ToLowerInvariant();
        if (d.Contains("flac")) return "flac";
        if (d.Contains("alac") || d.Contains("apple lossless")) return "alac";
        if (d.Contains("mpeg") && d.Contains("layer 3")) return "mp3";
        if (d.Contains("aac") || d.Contains("mpeg-4 audio")) return "aac";
        if (d.Contains("vorbis")) return "vorbis";
        if (d.Contains("opus")) return "opus";
        if (d.Contains("pcm")) return "pcm";
        return extension;
    }

    /// <summary>以只读、共享方式打开文件，保证永不写回音频文件。TagLib 每次需要读取时都会重新取 ReadStream。</summary>
    private sealed class ReadOnlyFile(string path) : TagLib.File.IFileAbstraction
    {
        public string Name => path;
        public Stream ReadStream => new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        public Stream WriteStream => throw new NotSupportedException("Solisten never writes audio files");
        public void CloseStream(Stream stream) => stream.Dispose();
    }
}
