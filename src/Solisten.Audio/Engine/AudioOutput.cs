namespace Solisten.Audio.Engine;

/// <summary>渲染线程回调：填满 buffer（交错 float），queuedFramesAfterWrite 是写入后设备中尚未播放的帧数。</summary>
public interface IRenderSource
{
    void Render(Span<float> buffer, int queuedFramesAfterWrite);
}

/// <summary>音频输出设备。Start 之后由输出自己的高优先级线程周期性调用 <see cref="IRenderSource.Render"/>。</summary>
public interface IAudioOutput : IDisposable
{
    int SampleRate { get; }
    int Channels { get; }
    string DeviceName { get; }

    void Start();
    void Stop();

    /// <summary>设备失效（拔出、被禁用）。在输出线程上触发。</summary>
    event Action<string>? Failed;
}

public delegate IAudioOutput AudioOutputFactory(IRenderSource source);
