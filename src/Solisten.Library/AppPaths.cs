using Solisten.Core.Model;

namespace Solisten.Library;

/// <summary>§6.4 存储位置：%LOCALAPPDATA%\Solisten\。</summary>
public sealed class AppPaths
{
    public AppPaths(string? root = null)
    {
        Root = root ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Solisten");
        Directory.CreateDirectory(Root);
        foreach (var d in new[] { WaveformDir, FeaturesDir, ThumbDir, CoverDir, LogsDir }) Directory.CreateDirectory(d);
    }

    public string Root { get; }
    public string Database => Path.Combine(Root, "library.db");
    public string Settings => Path.Combine(Root, "settings.json");
    public string WaveformDir => Path.Combine(Root, "cache", "waveform");
    public string FeaturesDir => Path.Combine(Root, "cache", "features");
    public string ThumbDir => Path.Combine(Root, "cache", "thumb");
    public string CoverDir => Path.Combine(Root, "cache", "cover");
    public string LogsDir => Path.Combine(Root, "logs");

    public string Waveform(TrackId id) => Path.Combine(WaveformDir, $"{id.Value}.wf");
    public string Features(TrackId id) => Path.Combine(FeaturesDir, $"{id.Value}.sf");
    public string Thumb(string coverHash, int size) => Path.Combine(ThumbDir, $"{coverHash}_{size}.png");

    /// <summary>原始封面（保留原编码，扩展名为 .jpg 或 .png）。</summary>
    public string? FindCover(string coverHash)
    {
        foreach (var ext in new[] { ".jpg", ".png" })
        {
            var p = Path.Combine(CoverDir, coverHash + ext);
            if (File.Exists(p)) return p;
        }
        return null;
    }

    public string CoverPath(string coverHash, string extension) => Path.Combine(CoverDir, coverHash + extension);
}
