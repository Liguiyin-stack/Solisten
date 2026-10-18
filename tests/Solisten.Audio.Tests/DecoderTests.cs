using System.Diagnostics;
using Solisten.Audio.Decoding;

namespace Solisten.Audio.Tests;

public sealed class DecoderTests : IDisposable
{
    private readonly string _dir = TestAudio.TempDir();

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static float[] ReadAll(Solisten.Core.Audio.IPcmReader r)
    {
        var all = new List<float>();
        var buf = new float[3000];
        int n;
        while ((n = r.Read(buf)) > 0) all.AddRange(buf.AsSpan(0, n));
        return [.. all];
    }

    [Fact]
    public void Wav_ReadsFloatSamplesExactly()
    {
        var signal = TestAudio.Stereo(10000);
        var path = TestAudio.WriteWav(Path.Combine(_dir, "a.wav"), signal);
        using var r = new WavPcmReader(path);
        Assert.Equal(TestAudio.Rate, r.SampleRate);
        Assert.Equal(2, r.Channels);
        Assert.Equal(signal, ReadAll(r));
    }

    [Fact]
    public void Wav_SeekIsSampleAccurate()
    {
        var signal = TestAudio.Stereo(TestAudio.Rate * 2);
        var path = TestAudio.WriteWav(Path.Combine(_dir, "a.wav"), signal);
        using var r = new WavPcmReader(path);
        r.Seek(TimeSpan.FromSeconds(1.2345));
        int frame = (int)Math.Round(1.2345 * TestAudio.Rate);
        Assert.Equal(frame, r.PositionFrames);
        var buf = new float[20];
        r.Read(buf);
        Assert.Equal(signal.AsSpan(frame * 2, 20).ToArray(), buf);
    }

    [Fact]
    public void Wav_DownmixesAndResamples()
    {
        var path = TestAudio.WriteWav16(Path.Combine(_dir, "a.wav"), TestAudio.Constant(44100, 0.5f));
        using var r = new WavPcmReader(path, 22050, 1);
        var all = ReadAll(r);
        Assert.InRange(all.Length, 22049, 22051);
        Assert.All(all, v => Assert.Equal(0.5f, v, 3));
    }

    [Fact]
    public void FFmpeg_DecodesWavIdenticallyToManagedReader()
    {
        if (!FFmpegLoader.IsAvailable) return; // 本机没有 FFmpeg 共享库时跳过
        var signal = TestAudio.Stereo(20000);
        var path = TestAudio.WriteWav(Path.Combine(_dir, "a.wav"), signal);
        using var r = new FFmpegPcmReader(path);
        Assert.Equal(TestAudio.Rate, r.SampleRate);
        Assert.Equal(signal, ReadAll(r));
    }

    [Fact]
    public void FFmpeg_SeekIsSampleAccurate()
    {
        if (!FFmpegLoader.IsAvailable) return;
        var signal = TestAudio.Stereo(TestAudio.Rate * 3);
        var path = TestAudio.WriteWav(Path.Combine(_dir, "a.wav"), signal);
        using var r = new FFmpegPcmReader(path);
        r.Seek(TimeSpan.FromSeconds(2.0004));
        int frame = (int)Math.Round(2.0004 * TestAudio.Rate);
        var buf = new float[20];
        Assert.Equal(20, r.Read(buf));
        Assert.Equal(signal.AsSpan(frame * 2, 20).ToArray(), buf);
    }

    [Fact]
    public void FFmpeg_ResamplesToDeviceRate()
    {
        if (!FFmpegLoader.IsAvailable) return;
        var path = TestAudio.WriteWav16(Path.Combine(_dir, "a.wav"), TestAudio.Stereo(44100, 44100), rate: 44100);
        using var r = new FFmpegPcmReader(path, 48000, 2);
        var all = ReadAll(r);
        Assert.InRange(all.Length / 2, 47990, 48010);
    }

    /// <summary>MP3/AAC 的编码器延迟与填充必须被裁掉：分段编码后解码出的长度应与原始分段一致。</summary>
    [Theory]
    [InlineData("flac", 0)]
    [InlineData("mp3", 0)]
    [InlineData("m4a", 1)] // FFmpeg 的 mp4 封装把时长多记 1 个采样
    public void FFmpeg_TrimsEncoderDelayAndPadding(string ext, int tolerance)
    {
        if (!FFmpegLoader.IsAvailable || FindFfmpegExe() is not { } exe) return;
        const int frames = 44100 * 2 + 1234;
        var src = TestAudio.WriteWav16(Path.Combine(_dir, "src.wav"), TestAudio.Stereo(frames, 44100), rate: 44100);
        var encoded = Path.Combine(_dir, "enc." + ext);
        var args = ext switch
        {
            "mp3" => $"-y -loglevel error -i \"{src}\" -c:a libmp3lame -b:a 192k \"{encoded}\"",
            "m4a" => $"-y -loglevel error -i \"{src}\" -c:a aac -b:a 192k \"{encoded}\"",
            _ => $"-y -loglevel error -i \"{src}\" \"{encoded}\"",
        };
        using (var p = Process.Start(new ProcessStartInfo(exe, args) { UseShellExecute = false, RedirectStandardError = true })!)
        {
            p.WaitForExit();
            if (p.ExitCode != 0) return; // 该构建不含对应编码器
        }

        using var r = new FFmpegPcmReader(encoded);
        int decoded = ReadAll(r).Length / 2;
        Assert.InRange(decoded, frames - tolerance, frames + tolerance);
    }

    private static string? FindFfmpegExe()
    {
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            var p = Path.Combine(dir, "ffmpeg.exe");
            if (File.Exists(p)) return p;
        }
        return null;
    }
}
