using System.Buffers.Binary;
using Solisten.Audio.Engine;

namespace Solisten.Audio.Tests;

internal static class TestAudio
{
    public const int Rate = 48000;

    /// <summary>写一个 float32 交错 WAV。</summary>
    public static string WriteWav(string path, float[] interleaved, int channels = 2, int rate = Rate)
    {
        using var fs = File.Create(path);
        using var w = new BinaryWriter(fs);
        int dataBytes = interleaved.Length * 4;
        w.Write("RIFF"u8);
        w.Write(36 + dataBytes);
        w.Write("WAVE"u8);
        w.Write("fmt "u8);
        w.Write(16);
        w.Write((short)3);
        w.Write((short)channels);
        w.Write(rate);
        w.Write(rate * channels * 4);
        w.Write((short)(channels * 4));
        w.Write((short)32);
        w.Write("data"u8);
        w.Write(dataBytes);
        foreach (var s in interleaved) w.Write(s);
        return path;
    }

    /// <summary>写一个 16 位 PCM WAV（测试 TagLib 与格式转换）。</summary>
    public static string WriteWav16(string path, float[] interleaved, int channels = 2, int rate = 44100)
    {
        using var fs = File.Create(path);
        using var w = new BinaryWriter(fs);
        int dataBytes = interleaved.Length * 2;
        w.Write("RIFF"u8);
        w.Write(36 + dataBytes);
        w.Write("WAVE"u8);
        w.Write("fmt "u8);
        w.Write(16);
        w.Write((short)1);
        w.Write((short)channels);
        w.Write(rate);
        w.Write(rate * channels * 2);
        w.Write((short)(channels * 2));
        w.Write((short)16);
        w.Write("data"u8);
        w.Write(dataBytes);
        Span<byte> b = stackalloc byte[2];
        foreach (var s in interleaved)
        {
            BinaryPrimitives.WriteInt16LittleEndian(b, (short)Math.Clamp(Math.Round(s * 32767), -32768, 32767));
            w.Write(b);
        }
        return path;
    }

    /// <summary>立体声测试信号：左 440Hz，右 660Hz，相位连续。</summary>
    public static float[] Stereo(int frames, int rate = Rate, int startFrame = 0)
    {
        var a = new float[frames * 2];
        for (int i = 0; i < frames; i++)
        {
            double t = (i + startFrame) / (double)rate;
            a[2 * i] = (float)(0.6 * Math.Sin(2 * Math.PI * 440 * t));
            a[2 * i + 1] = (float)(0.4 * Math.Sin(2 * Math.PI * 660 * t));
        }
        return a;
    }

    public static float[] Constant(int frames, float value, int channels = 2) => Enumerable.Repeat(value, frames * channels).ToArray();

    public static string TempDir()
    {
        var d = Path.Combine(Path.GetTempPath(), "solisten-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(d);
        return d;
    }
}

/// <summary>模拟设备：在自己的线程上按固定周期调用渲染回调并记录全部输出。</summary>
internal sealed class PumpOutput(IRenderSource source, int periodFrames = 480, int periodMs = 2) : IAudioOutput
{
    private readonly List<float> _recorded = [];
    private readonly Lock _gate = new();
    private Thread? _thread;
    private volatile bool _running;

    public int SampleRate => TestAudio.Rate;
    public int Channels => 2;
    public string DeviceName => "pump";

    public event Action<string>? Failed { add { } remove { } }

    public float[] Recorded
    {
        get { lock (_gate) return [.. _recorded]; }
    }

    public int RecordedFrames
    {
        get { lock (_gate) return _recorded.Count / 2; }
    }

    public void Start()
    {
        _running = true;
        _thread = new Thread(() =>
        {
            var buf = new float[periodFrames * 2];
            while (_running)
            {
                source.Render(buf, periodFrames);
                lock (_gate) _recorded.AddRange(buf);
                Thread.Sleep(periodMs);
            }
        }) { IsBackground = true };
        _thread.Start();
    }

    public void Stop()
    {
        _running = false;
        _thread?.Join();
    }

    public void Dispose() => Stop();
}
