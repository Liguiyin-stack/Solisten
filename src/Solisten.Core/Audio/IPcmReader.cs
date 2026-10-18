namespace Solisten.Core.Audio;

/// <summary>按顺序读取解码后的交错 float32 PCM。由 Solisten.Audio 提供实现（FFmpeg / WAV）。</summary>
public interface IPcmReader : IDisposable
{
    int SampleRate { get; }
    int Channels { get; }

    /// <summary>估计时长；未知时为 <see cref="TimeSpan.Zero"/>。</summary>
    TimeSpan Duration { get; }

    /// <summary>读取交错采样，返回写入的 float 个数（是 Channels 的整数倍）；0 表示结束。</summary>
    int Read(Span<float> interleaved);
}

public interface IPcmReaderFactory
{
    /// <summary>打开文件。targetSampleRate / targetChannels 为 null 时保持源格式。</summary>
    IPcmReader Open(string path, int? targetSampleRate = null, int? targetChannels = null);
}
