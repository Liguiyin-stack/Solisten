using Dapper;
using Microsoft.Data.Sqlite;
using Solisten.Core.Audio;
using Solisten.Core.Model;
using Solisten.Library.Analysis;
using Solisten.Library.Storage;

namespace Solisten.Library.Tests;

/// <summary>内存中的立体声 PCM。</summary>
internal sealed class ArrayReader(float[] interleaved, int channels, int rate = 22050) : IPcmReader
{
    private int _pos;
    public int SampleRate => rate;
    public int Channels => channels;
    public TimeSpan Duration => TimeSpan.FromSeconds(interleaved.Length / channels / (double)rate);

    public int Read(Span<float> dst)
    {
        int n = Math.Min(dst.Length - dst.Length % channels, interleaved.Length - _pos);
        interleaved.AsSpan(_pos, n).CopyTo(dst);
        _pos += n;
        return n;
    }

    public void Dispose() { }
}

public class SoundFeatureTests
{
    private const int Rate = 22050;

    private static float[] Stereo(int frames, Func<int, (float L, float R)> f)
    {
        var a = new float[frames * 2];
        for (int i = 0; i < frames; i++) (a[2 * i], a[2 * i + 1]) = f(i);
        return a;
    }

    private static float[] Tone(double hz, double seconds, double amp = 0.5) =>
        Stereo((int)(seconds * Rate), i => { var v = (float)(amp * Math.Sin(2 * Math.PI * hz * i / Rate)); return (v, v); });

    private static SoundFeatures Analyze(float[] stereo) => SoundFeatureAnalyzer.Analyze(new ArrayReader(stereo, 2));

    /// <summary>某段时间内各频段的平均值（原始 0..65535 → 0..1）。</summary>
    private static (double Low, double Mid, double High) Mean(SoundFeatures f, double from, double to)
    {
        int a = (int)(from * f.Rate), b = (int)(to * f.Rate);
        double l = 0, m = 0, h = 0;
        for (int i = a; i < b; i++) { l += f.Low[i]; m += f.Mid[i]; h += f.High[i]; }
        int n = b - a;
        return (l / n / 65535, m / n / 65535, h / n / 65535);
    }

    [Fact]
    public void Tones_LandInTheirBands()
    {
        // 三段各 2 秒：60 Hz、1 kHz、8 kHz。每个频段按全曲归一化，所以比较的是"这一段里哪个频段最亮"。
        var signal = Tone(60, 2).Concat(Tone(1000, 2)).Concat(Tone(8000, 2)).ToArray();
        var f = Analyze(signal);
        Assert.Equal(600, f.Length);
        Assert.Equal(6.0, f.Duration.TotalSeconds, 2);

        var bass = Mean(f, 0.6, 1.8);
        var voice = Mean(f, 2.6, 3.8);
        var air = Mean(f, 4.6, 5.8);
        Assert.True(bass.Low > 0.9 && bass.Mid < 0.1 && bass.High < 0.05, $"60 Hz: {bass}");
        Assert.True(voice.Mid > 0.9 && voice.Low < 0.1 && voice.High < 0.1, $"1 kHz: {voice}");
        Assert.True(air.High > 0.9 && air.Low < 0.05 && air.Mid < 0.1, $"8 kHz: {air}");
    }

    [Fact]
    public void LowBand_RisesSlowerThanHighBand()
    {
        // 低频起 80 ms / 落 450 ms，高频起 10 ms / 落 120 ms：同样突然出现的声音，低频更慢达到一半。
        var signal = new float[Rate * 2 * 2].Concat(Tone(60, 2)).ToArray(); // 2 秒立体声静音
        var lowF = Analyze(signal);
        var highF = Analyze(new float[Rate * 2 * 2].Concat(Tone(8000, 2)).ToArray());
        int HalfRise(ushort[] a) => Array.FindIndex(a, 200, v => v > 32767) - 200;
        Assert.True(HalfRise(lowF.Low) > 3 * HalfRise(highF.High), $"low {HalfRise(lowF.Low)} high {HalfRise(highF.High)}");
    }

    [Fact]
    public void ClickTrain_OnsetsAreFoundWithin10ms_AndNoneInSilence()
    {
        // 前 1 秒静音；之后每 500 ms 一个 20 ms 的噪声脉冲，共 8 个；最后 1 秒静音。
        var rng = new Random(3);
        int frames = Rate * 6;
        var clicks = Enumerable.Range(0, 8).Select(k => 1.0 + 0.5 * k).ToArray();
        var signal = Stereo(frames, i =>
        {
            double t = i / (double)Rate;
            bool on = clicks.Any(c => t >= c && t < c + 0.02);
            var v = on ? (float)(rng.NextDouble() * 1.6 - 0.8) : 0f;
            return (v, v);
        });
        var f = Analyze(signal);

        Assert.Equal(clicks.Length, f.Onsets.Length);
        for (int k = 0; k < clicks.Length; k++)
            Assert.InRange(f.Onsets[k].Time, clicks[k] - 0.0101, clicks[k] + 0.0101);
        Assert.DoesNotContain(f.Onsets, o => o.Time < 0.99 || o.Time > 4.6);

        // 等强度的脉冲：明显重音之间至少相隔 400 ms（这里 500 ms 一个，全部可以是明显重音）
        var strong = f.Onsets.Where(o => o.Strong).Select(o => o.Time).ToArray();
        Assert.NotEmpty(strong);
        for (int i = 1; i < strong.Length; i++) Assert.True(strong[i] - strong[i - 1] >= 0.399f);
    }

    [Fact]
    public void SteadyTone_HasNoOnsetsAfterItStarts()
    {
        var f = Analyze(Tone(440, 3));
        Assert.True(f.Onsets.Length <= 1, $"{f.Onsets.Length} onsets");
        Assert.All(f.Onsets, o => Assert.True(o.Time < 0.05));
    }

    [Fact]
    public void Balance_FollowsTheLouderChannel()
    {
        var left = Stereo(Rate * 2, i => ((float)(0.5 * Math.Sin(i * 0.1)), 0f));
        var right = Stereo(Rate * 2, i => (0f, (float)(0.5 * Math.Sin(i * 0.1))));
        var both = Tone(440, 2);
        var f = Analyze([.. left, .. right, .. both]);

        Assert.True(f.Sample(TimeSpan.FromSeconds(1.5)).Balance > 0.95f);
        Assert.True(f.Sample(TimeSpan.FromSeconds(3.5)).Balance < -0.95f);
        Assert.InRange(f.Sample(TimeSpan.FromSeconds(5.5)).Balance, -0.02f, 0.02f);
    }

    [Fact]
    public void Silence_IsAllZero()
    {
        var f = Analyze(new float[Rate * 2 * 2]);
        Assert.All(f.Low, v => Assert.Equal(0, v));
        Assert.All(f.High, v => Assert.Equal(0, v));
        Assert.Empty(f.Onsets);
        Assert.Equal(0f, f.Sample(TimeSpan.FromSeconds(1)).Balance);
    }

    [Fact]
    public void Mono_IsCentered()
    {
        var mono = Enumerable.Range(0, Rate).Select(i => (float)Math.Sin(i * 0.05)).ToArray();
        var f = SoundFeatureAnalyzer.Analyze(new ArrayReader(mono, 1));
        Assert.Equal(0f, f.Sample(TimeSpan.FromSeconds(0.5)).Balance);
    }

    [Fact]
    public void Sample_InterpolatesAndClamps()
    {
        var f = new SoundFeatures(TimeSpan.FromSeconds(0.02), 100, [0, 65535], [0, 0], [0, 0], [-32767, 32767], []);
        Assert.Equal(0.5f, f.Sample(TimeSpan.FromSeconds(0.005)).Low, 3);
        Assert.Equal(0f, f.Sample(TimeSpan.FromSeconds(0.005)).Balance, 3);
        Assert.Equal(1f, f.Sample(TimeSpan.FromSeconds(9)).Low);
        Assert.Equal(0f, f.Sample(TimeSpan.FromSeconds(-1)).Low);
    }

    [Fact]
    public void LastStrongOnset_SkipsWeakOnes()
    {
        var f = new SoundFeatures(TimeSpan.FromSeconds(3), 100, [0], [0], [0], [0],
            [new Onset(0.5f, 1, true), new Onset(1.0f, 0.2f, false), new Onset(2.0f, 1, true)]);
        Assert.Equal(-1, f.LastStrongOnsetAtOrBefore(TimeSpan.FromSeconds(0.4)));
        Assert.Equal(0, f.LastStrongOnsetAtOrBefore(TimeSpan.FromSeconds(1.5)));
        Assert.Equal(2, f.LastStrongOnsetAtOrBefore(TimeSpan.FromSeconds(9)));
    }

    [Fact]
    public void Features_RoundTripThroughTheCacheFormat()
    {
        var f = Analyze(Tone(60, 1).Concat(Tone(3000, 1)).ToArray());
        using var ms = new MemoryStream();
        f.Write(ms);
        ms.Position = 0;
        var back = SoundFeatures.Read(ms)!;
        Assert.Equal(f.Low, back.Low);
        Assert.Equal(f.Mid, back.Mid);
        Assert.Equal(f.High, back.High);
        Assert.Equal(f.Balance, back.Balance);
        Assert.Equal(f.Onsets, back.Onsets);
        Assert.Equal(f.Duration, back.Duration);

        ms.Position = 0;
        ms.WriteByte((byte)'X');
        ms.Position = 0;
        Assert.Null(SoundFeatures.Read(ms));
    }
}

public sealed class SchemaMigrationTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), "solisten-migrate-" + Guid.NewGuid().ToString("N") + ".db");

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { File.Delete(_path); } catch (IOException) { }
    }

    [Fact]
    public void Version1Database_GainsFeaturesColumn_AndKeepsWaveformResults()
    {
        // 模拟旧版本：只有 waveform_ver 的 analysis 表，user_version = 1。
        using (var db = LibraryDatabase.Open(_path))
        {
            var root = db.AddRoot(Path.GetTempPath());
            var ids = db.UpsertTracks(root.Id, [new ScannedTrack("a.flac", 1, 1, new TrackMeta("A", "X", "", "Y", 1, 1, null, TimeSpan.FromMinutes(3)), new AudioFormat("flac", 44100, 16, 2), null)]);
            db.SaveAnalysis(ids[0], WaveformData.CurrentVersion, SoundFeatures.CurrentVersion, Palette.Neutral);
        }
        using (var raw = new SqliteConnection($"Data Source={_path};Pooling=False"))
        {
            raw.Open();
            raw.Execute("""
                CREATE TABLE analysis_v1(track_id INTEGER PRIMARY KEY, waveform_ver INTEGER NOT NULL, palette_json TEXT, analyzed_at TEXT NOT NULL);
                INSERT INTO analysis_v1 SELECT track_id, waveform_ver, palette_json, analyzed_at FROM analysis;
                DROP TABLE analysis;
                ALTER TABLE analysis_v1 RENAME TO analysis;
                PRAGMA user_version=1;
                """);
        }

        using var migrated = LibraryDatabase.Open(_path);
        var id = migrated.LoadTracks().Single().Id;
        var a = migrated.GetAnalysis(id)!.Value;
        Assert.Equal(WaveformData.CurrentVersion, a.WaveformVersion);
        Assert.Equal(0, a.FeaturesVersion);
        Assert.Equal(Palette.Neutral, a.Palette);
        // 只需补算特征
        Assert.Equal([id], migrated.GetUnanalyzed(WaveformData.CurrentVersion, SoundFeatures.CurrentVersion));
    }
}
