using Solisten.Core.Model;

namespace Solisten.Core.Audio;

/// <summary>
/// 播放引擎解码出的 PCM 的旁路：用于边播边分析（在线歌曲没有离线分析结果）。
/// 在引擎线程上调用，实现必须立即返回（复制数据后交给别的线程），不能阻塞或抛出异常。
/// </summary>
public interface IDecodeTap
{
    /// <param name="startFrame">这段数据在曲目时间轴上的起始帧（按 <paramref name="sampleRate"/>）；跳转后会不连续。</param>
    void Decoded(TrackId track, long startFrame, ReadOnlySpan<float> interleaved, int sampleRate, int channels);

    /// <summary>曲目自然解码到了结尾（不是被切走或停止）。</summary>
    void Ended(TrackId track);
}
