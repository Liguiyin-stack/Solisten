using FFmpeg.AutoGen;

namespace Solisten.Audio.Decoding;

/// <summary>
/// 动态加载 FFmpeg 共享库（LGPL）。查找顺序：SOLISTEN_FFMPEG_DIR → 程序目录\ffmpeg → 程序目录。
/// </summary>
public static class FFmpegLoader
{
    private static readonly Lock Gate = new();
    private static bool? _available;

    public static string? LoadedFrom { get; private set; }
    public static string? Version { get; private set; }

    public static bool IsAvailable
    {
        get
        {
            lock (Gate)
            {
                _available ??= TryInitialize();
                return _available.Value;
            }
        }
    }

    public static void EnsureInitialized()
    {
        if (!IsAvailable) throw new AudioDecodeException("找不到 FFmpeg 共享库（avcodec / avformat / avutil / swresample）");
    }

    private static bool TryInitialize()
    {
        var candidates = new List<string>();
        if (Environment.GetEnvironmentVariable("SOLISTEN_FFMPEG_DIR") is { Length: > 0 } env) candidates.Add(env);
        candidates.Add(Path.Combine(AppContext.BaseDirectory, "ffmpeg"));
        candidates.Add(AppContext.BaseDirectory);

        foreach (var dir in candidates)
        {
            if (!File.Exists(Path.Combine(dir, "avcodec-62.dll"))) continue;
            try
            {
                ffmpeg.RootPath = dir;
                Version = ffmpeg.av_version_info();
                ffmpeg.av_log_set_level(ffmpeg.AV_LOG_ERROR);
                LoadedFrom = dir;
                return true;
            }
            catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException or NotSupportedException or BadImageFormatException)
            {
            }
        }
        return false;
    }
}
