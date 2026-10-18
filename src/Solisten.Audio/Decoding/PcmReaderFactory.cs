using Solisten.Core.Audio;

namespace Solisten.Audio.Decoding;

/// <summary>优先使用 FFmpeg；FFmpeg 不可用时 WAV 仍可通过托管读取器播放和分析。</summary>
public sealed class PcmReaderFactory : IPcmReaderFactory
{
    public static readonly PcmReaderFactory Default = new();

    public bool PreferManagedWav { get; init; }

    IPcmReader IPcmReaderFactory.Open(string path, int? targetSampleRate, int? targetChannels) => Open(path, targetSampleRate, targetChannels);

    public ISeekablePcmReader Open(string path, int? targetSampleRate = null, int? targetChannels = null)
    {
        bool isWav = Path.GetExtension(path).Equals(".wav", StringComparison.OrdinalIgnoreCase);
        if (isWav && (PreferManagedWav || !FFmpegLoader.IsAvailable))
        {
            try { return new WavPcmReader(path, targetSampleRate, targetChannels); }
            catch (AudioDecodeException) when (FFmpegLoader.IsAvailable) { }
        }
        return new FFmpegPcmReader(path, targetSampleRate, targetChannels);
    }
}
