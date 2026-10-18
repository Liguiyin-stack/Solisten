using System.Collections.Concurrent;
using Solisten.Audio.Decoding;
using Solisten.Audio.Engine;
using Solisten.Core.Model;

namespace Solisten.Audio.Tests;

public sealed class EngineTests : IDisposable
{
    private readonly string _dir = TestAudio.TempDir();
    private PumpOutput? _output;
    private readonly PlaybackEngine _engine;
    private readonly BlockingCollection<string> _events = [];

    public EngineTests()
    {
        _engine = new PlaybackEngine(src => _output = new PumpOutput(src),
            (path, rate, ch) => new WavPcmReader(path, rate, ch));
        _engine.TrackBoundaryReached += t => _events.Add($"boundary:{t}");
        _engine.TrackEnded += t => _events.Add($"ended:{t}");
        _engine.PlaybackFailed += (t, m) => _events.Add($"failed:{t}");
    }

    public void Dispose()
    {
        _engine.Dispose();
        Directory.Delete(_dir, recursive: true);
    }

    private string NextEvent(int timeoutMs = 10000)
    {
        Assert.True(_events.TryTake(out var e, timeoutMs), "timed out waiting for engine event");
        return e!;
    }

    private static int FirstNonZeroFrame(float[] interleaved)
    {
        for (int i = 0; i < interleaved.Length; i++)
            if (interleaved[i] != 0f) return i / 2;
        return -1;
    }

    [Fact]
    public void Gapless_ConsecutiveTracksAreSampleContinuous()
    {
        const int total = 72000;      // 1.5 s
        const int split = 37013;      // 不对齐任何缓冲区边界
        var signal = TestAudio.Stereo(total, startFrame: 1); // 第一个采样非零，便于对齐
        var a = TestAudio.WriteWav(Path.Combine(_dir, "a.wav"), signal[..(split * 2)]);
        var b = TestAudio.WriteWav(Path.Combine(_dir, "b.wav"), signal[(split * 2)..]);

        _engine.SetSuccessor(new TrackId(2), b);
        _engine.Load(new TrackId(1), a, TimeSpan.Zero, paused: false);

        Assert.Equal("boundary:2", NextEvent());
        Assert.Equal("ended:2", NextEvent());
        Thread.Sleep(50);
        var rec = _output!.Recorded;

        int start = FirstNonZeroFrame(rec);
        Assert.True(start >= 0);
        int fadeFrames = (int)(0.005 * TestAudio.Rate) + 1;
        // 起播 5ms 淡入之后，输出与原始信号逐采样一致，跨越边界没有任何间隙或重复。
        for (int i = fadeFrames; i < total; i++)
        {
            int o = (start + i) * 2;
            if (rec[o] != signal[i * 2] || rec[o + 1] != signal[i * 2 + 1])
                Assert.Fail($"sample mismatch at frame {i} (split {split}): {rec[o]} vs {signal[i * 2]}");
        }
        // 结束之后只剩静音
        for (int i = (start + total) * 2; i < rec.Length; i++) Assert.Equal(0f, rec[i]);
    }

    [Fact]
    public void Pause_FadesOutWithinTenMilliseconds()
    {
        var path = TestAudio.WriteWav(Path.Combine(_dir, "dc.wav"), TestAudio.Constant(TestAudio.Rate * 3, 0.5f));
        _engine.Load(new TrackId(1), path, TimeSpan.Zero, paused: false);
        Thread.Sleep(300);
        _engine.Pause();
        Thread.Sleep(200);
        var rec = _output!.Recorded;

        int lastFull = -1, firstZeroAfter = -1;
        for (int f = rec.Length / 2 - 1; f >= 0; f--)
        {
            if (rec[f * 2] >= 0.5f) { lastFull = f; break; }
        }
        Assert.True(lastFull > 0);
        for (int f = lastFull; f < rec.Length / 2; f++)
        {
            if (rec[f * 2] == 0f) { firstZeroAfter = f; break; }
        }
        int fade = firstZeroAfter - lastFull;
        Assert.InRange(fade, 470, 490); // 10ms @ 48kHz
        for (int f = lastFull + 1; f < firstZeroAfter; f++) Assert.True(rec[f * 2] < rec[(f - 1) * 2]);
        Assert.False(_engine.Clock.IsRunning);
    }

    [Fact]
    public void Volume_RampsLinearlyOverTwentyMilliseconds()
    {
        var path = TestAudio.WriteWav(Path.Combine(_dir, "dc.wav"), TestAudio.Constant(TestAudio.Rate * 3, 0.5f));
        _engine.Load(new TrackId(1), path, TimeSpan.Zero, paused: false);
        Thread.Sleep(300);
        _engine.Volume = 0f;
        Thread.Sleep(200);
        var rec = _output!.Recorded;

        int lastFull = Array.FindLastIndex(rec, v => v >= 0.5f) / 2;
        int firstZero = -1;
        for (int f = lastFull; f < rec.Length / 2; f++)
            if (rec[f * 2] == 0f) { firstZero = f; break; }
        Assert.InRange(firstZero - lastFull, 950, 970); // 20ms @ 48kHz
    }

    [Fact]
    public void Clock_TracksPlaybackAndSeek()
    {
        var path = TestAudio.WriteWav(Path.Combine(_dir, "s.wav"), TestAudio.Stereo(TestAudio.Rate * 10));
        _engine.Load(new TrackId(7), path, TimeSpan.FromSeconds(2), paused: true);
        Thread.Sleep(100);
        var c = _engine.Clock;
        Assert.Equal(new TrackId(7), c.Track);
        Assert.False(c.IsRunning);
        Assert.Equal(2.0, c.Position.TotalSeconds, 3);

        _engine.Seek(TimeSpan.FromSeconds(5));
        Thread.Sleep(100);
        Assert.Equal(5.0, _engine.Clock.Position.TotalSeconds, 3);

        _engine.Resume();
        Thread.Sleep(200);
        c = _engine.Clock;
        Assert.True(c.IsRunning);
        Assert.InRange(c.Position.TotalSeconds, 5.0, 8.0); // 测试泵比实时快
    }

    [Fact]
    public void Load_ReportsFailureForMissingFile()
    {
        _engine.Load(new TrackId(9), Path.Combine(_dir, "missing.wav"), TimeSpan.Zero, paused: false);
        Assert.Equal("failed:9", NextEvent());
    }

    [Fact]
    public void NoSuccessor_EndsTrack()
    {
        var path = TestAudio.WriteWav(Path.Combine(_dir, "short.wav"), TestAudio.Stereo(4800));
        _engine.Load(new TrackId(3), path, TimeSpan.Zero, paused: false);
        Assert.Equal("ended:3", NextEvent());
    }

    [Fact]
    public void BrokenSuccessor_EndsCurrentTrackInsteadOfStalling()
    {
        var path = TestAudio.WriteWav(Path.Combine(_dir, "short.wav"), TestAudio.Stereo(4800));
        var broken = Path.Combine(_dir, "broken.wav");
        File.WriteAllBytes(broken, [1, 2, 3]);
        _engine.SetSuccessor(new TrackId(4), broken);
        _engine.Load(new TrackId(3), path, TimeSpan.Zero, paused: false);
        Assert.Equal("ended:3", NextEvent());
    }
}

public class PrimitiveTests
{
    [Fact]
    public void RingBuffer_WrapsAroundAndTracksTotals()
    {
        var rb = new SpscRingBuffer(10);
        Assert.Equal(16, rb.Capacity);
        var tmp = new float[16];
        float next = 0, expect = 0;
        for (int round = 0; round < 50; round++)
        {
            var src = Enumerable.Range(0, 7).Select(_ => next++).ToArray();
            Assert.Equal(7, rb.Write(src));
            Assert.Equal(7, rb.Read(tmp.AsSpan(0, 7)));
            for (int i = 0; i < 7; i++) Assert.Equal(expect++, tmp[i]);
        }
        Assert.Equal(350, rb.TotalWritten);
        Assert.Equal(16, rb.Write(new float[20]));
        rb.DiscardAll();
        Assert.Equal(0, rb.Available);
    }

    [Fact]
    public void Queue_RespectsCapacity()
    {
        var q = new SpscQueue<int>(4);
        for (int i = 0; i < 4; i++) Assert.True(q.TryEnqueue(i));
        Assert.False(q.TryEnqueue(9));
        Assert.True(q.TryDequeue(out var v));
        Assert.Equal(0, v);
        q.Clear();
        Assert.False(q.TryPeek(out _));
    }

    [Fact]
    public void VolumeCurve_IsLogarithmicAndInvertible()
    {
        Assert.Equal(0f, VolumeCurve.FromSlider(0));
        Assert.Equal(1f, VolumeCurve.FromSlider(1));
        Assert.Equal(0.001f, VolumeCurve.FromSlider(0.0001), 3);
        Assert.Equal(0.5, VolumeCurve.ToSlider(VolumeCurve.FromSlider(0.5)), 6);
        Assert.True(VolumeCurve.FromSlider(0.5) < 0.5f);
    }
}
