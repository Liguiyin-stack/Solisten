using Solisten.Core.Model;

namespace Solisten.Audio.Engine;

/// <summary>§5.3 音频时钟：渲染层每帧直接读取，不经过 UI 线程转发。</summary>
public readonly record struct PlaybackClock(TrackId? Track, long SamplePosition, int SampleRate, bool IsRunning)
{
    public TimeSpan Position => SampleRate > 0 ? TimeSpan.FromSeconds(SamplePosition / (double)SampleRate) : TimeSpan.Zero;
}

/// <summary>滑块（0..1）到线性增益的对数刻度：60 dB 动态范围，0 为静音。</summary>
public static class VolumeCurve
{
    public const double RangeDb = 60;

    public static float FromSlider(double slider) =>
        slider <= 0 ? 0f : (float)Math.Pow(10, (Math.Clamp(slider, 0, 1) - 1) * RangeDb / 20);

    public static double ToSlider(float gain) =>
        gain <= 0 ? 0 : Math.Clamp(1 + 20 * Math.Log10(gain) / RangeDb, 0, 1);
}
