using System.Runtime.InteropServices.WindowsRuntime;
using Solisten.Audio.Decoding;
using Solisten.Core.Audio;
using Solisten.Core.Model;
using Solisten.Library.Analysis;
using Solisten.Library.Metadata;
using Solisten.Library.Scanning;
using Solisten.Library.Search;
using Solisten.Library.Storage;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace Solisten.Library.Tests;

internal static class Wav
{
    public static string Write(string path, float[] mono, int rate = 22050)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var w = new BinaryWriter(File.Create(path));
        w.Write("RIFF"u8); w.Write(36 + mono.Length * 4); w.Write("WAVE"u8);
        w.Write("fmt "u8); w.Write(16); w.Write((short)3); w.Write((short)1); w.Write(rate); w.Write(rate * 4); w.Write((short)4); w.Write((short)32);
        w.Write("data"u8); w.Write(mono.Length * 4);
        foreach (var s in mono) w.Write(s);
        return path;
    }

    public static float[] Sine(int frames, double amp = 0.8, int rate = 22050) =>
        Enumerable.Range(0, frames).Select(i => (float)(amp * Math.Sin(2 * Math.PI * 220 * i / rate))).ToArray();

    public static async Task<byte[]> SolidPng(byte r, byte g, byte b, int size = 32)
    {
        var px = new byte[size * size * 4];
        for (int i = 0; i < px.Length; i += 4) { px[i] = b; px[i + 1] = g; px[i + 2] = r; px[i + 3] = 255; }
        using var ms = new InMemoryRandomAccessStream();
        var enc = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, ms);
        enc.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore, (uint)size, (uint)size, 96, 96, px);
        await enc.FlushAsync();
        var bytes = new byte[ms.Size];
        ms.Seek(0);
        await ms.ReadAsync(bytes.AsBuffer(), (uint)bytes.Length, InputStreamOptions.None);
        return bytes;
    }
}

public class SearchTextTests
{
    [Theory]
    [InlineData("晴天", "qingtian qt")]
    [InlineData("七里香", "qilixiang qlx")]
    [InlineData("Jay 晴天", "qingtian qt")]
    [InlineData("Hello", "")]
    public void Pinyin_SearchKey(string input, string expected) => Assert.Equal(expected, Pinyin.SearchKey(input));

    [Fact]
    public void MatchQuery_SplitsHanIntoPhrasesAndLatinIntoPrefixes()
    {
        Assert.Equal("\"晴 天\"", SearchText.MatchQuery("晴天"));
        Assert.Equal("\"jay\"* AND \"晴\"", SearchText.MatchQuery("Jay 晴"));
        Assert.Null(SearchText.MatchQuery("  \"*  "));
    }
}

public sealed class LibraryFixture : IDisposable
{
    public string Dir { get; } = Path.Combine(Path.GetTempPath(), "solisten-lib-tests", Guid.NewGuid().ToString("N"));
    public string Music => Path.Combine(Dir, "Music");
    public AppPaths Paths { get; }

    public LibraryFixture()
    {
        Directory.CreateDirectory(Music);
        Paths = new AppPaths(Path.Combine(Dir, "appdata"));
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(Dir, recursive: true); } catch (IOException) { }
    }
}

public sealed class MetadataTests : IDisposable
{
    private readonly LibraryFixture _f = new();
    public void Dispose() => _f.Dispose();

    [Fact]
    public void Untagged_FallsBackToFolderStructure()
    {
        var path = Wav.Write(Path.Combine(_f.Music, "Artist X", "Album Y", "03 Song.wav"), Wav.Sine(22050));
        var md = TrackMetadataReader.Read(path, _f.Music);
        Assert.Equal("03 Song", md.Meta.Title);
        Assert.Equal("Album Y", md.Meta.Album);
        Assert.Equal("Artist X", md.Meta.Artist);
        Assert.Equal(1.0, md.Meta.Duration.TotalSeconds, 1);
        Assert.Equal(22050, md.Format.SampleRate);
    }

    [Fact]
    public void ArtistFallback_DoesNotUseFoldersOutsideTheRoot()
    {
        var path = Wav.Write(Path.Combine(_f.Music, "Album Only", "a.wav"), Wav.Sine(100));
        Assert.Equal(("Album Only", TrackMetadataReader.Unknown), TrackMetadataReader.FolderFallback(path, _f.Music));
    }

    [Fact]
    public void Tags_AreReadAndFileIsNeverModified()
    {
        var path = Wav.Write(Path.Combine(_f.Music, "x", "t.wav"), Wav.Sine(2205));
        using (var tf = TagLib.File.Create(path))
        {
            tf.Tag.Title = "晴天";
            tf.Tag.Performers = ["周杰伦"];
            tf.Tag.Album = "叶惠美";
            tf.Tag.Track = 7;
            tf.Save();
        }
        var before = File.ReadAllBytes(path);
        var md = TrackMetadataReader.Read(path, _f.Music);
        Assert.Equal("晴天", md.Meta.Title);
        Assert.Equal("周杰伦", md.Meta.Artist);
        Assert.Equal("叶惠美", md.Meta.Album);
        Assert.Equal(7, md.Meta.TrackNo);
        Assert.Equal(before, File.ReadAllBytes(path));
    }
}

public sealed class ScannerAndDatabaseTests : IDisposable
{
    private readonly LibraryFixture _f = new();
    private readonly LibraryDatabase _db;

    public ScannerAndDatabaseTests() => _db = LibraryDatabase.Open(_f.Paths.Database);

    public void Dispose()
    {
        _db.Dispose();
        _f.Dispose();
    }

    [Fact]
    public async Task Scan_DetectsAddedChangedRemovedAndOfflineRoot()
    {
        var a = Wav.Write(Path.Combine(_f.Music, "Art", "Alb", "01 a.wav"), Wav.Sine(2205));
        var b = Wav.Write(Path.Combine(_f.Music, "Art", "Alb", "02 b.wav"), Wav.Sine(2205));
        File.WriteAllBytes(Path.Combine(_f.Music, "Art", "Alb", "x.ape"), [0]);
        File.WriteAllBytes(Path.Combine(_f.Music, "Art", "Alb", "cover.png"), await Wav.SolidPng(200, 30, 30));

        var root = _db.AddRoot(_f.Music);
        var scanner = new LibraryScanner(_db, _f.Paths);
        var r1 = scanner.Scan(root);
        Assert.Equal(2, r1.AddedOrChanged.Count);
        Assert.Single(r1.Unsupported);

        var tracks = _db.LoadTracks();
        Assert.All(tracks, t => Assert.NotNull(_db.GetCoverHash(t.Id)));
        Assert.Single(tracks.Select(t => _db.GetCoverHash(t.Id)).Distinct());
        Assert.Equal("1:Art/Alb", tracks[0].Groups.Folder);

        var r2 = scanner.Scan(root);
        Assert.Empty(r2.AddedOrChanged);
        Assert.Equal(2, r2.Unchanged);

        Wav.Write(a, Wav.Sine(4410));
        File.SetLastWriteTimeUtc(a, DateTime.UtcNow.AddMinutes(1));
        File.Delete(b);
        var r3 = scanner.Scan(root);
        Assert.Single(r3.AddedOrChanged);
        Assert.Single(r3.Removed);
        var index = _db.LoadIndex();
        Assert.Equal(1, index.Tracks.Values.Count(t => t.Available));
        Assert.Equal(r1.AddedOrChanged[0], r3.AddedOrChanged[0]); // 同一路径保持同一 id

        Directory.Move(_f.Music, _f.Music + "-offline");
        var r4 = scanner.Scan(root);
        Assert.False(r4.RootAvailable);
        Assert.All(_db.LoadTracks(), t => Assert.False(t.Available));

        Directory.Move(_f.Music + "-offline", _f.Music);
        var r5 = scanner.Scan(root);
        Assert.Single(r5.Restored);
        Assert.Single(_db.LoadTracks(), t => t.Available);
    }

    [Fact]
    public void Search_MatchesHanSubstringsAndPinyin()
    {
        var root = _db.AddRoot(_f.Music);
        var meta = (string t, string ar, string al) => new TrackMeta(t, ar, "", al, 1, 1, null, TimeSpan.FromMinutes(4));
        var fmt = new AudioFormat("flac", 44100, 16, 2);
        var ids = _db.UpsertTracks(root.Id,
        [
            new ScannedTrack(@"周杰伦\叶惠美\01 晴天.flac", 1, 1, meta("晴天", "周杰伦", "叶惠美"), fmt, null),
            new ScannedTrack(@"周杰伦\七里香\01 七里香.flac", 1, 1, meta("七里香", "周杰伦", "七里香"), fmt, null),
            new ScannedTrack(@"Beatles\Abbey Road\01 Come Together.flac", 1, 1, meta("Come Together", "The Beatles", "Abbey Road"), fmt, null),
        ]);

        Assert.Equal([ids[0]], _db.Search("晴天").ToArray());
        Assert.Equal([ids[0]], _db.Search("天").ToArray());
        Assert.Equal([ids[0]], _db.Search("qt").ToArray());
        Assert.Equal([ids[0]], _db.Search("qingt").ToArray());
        Assert.Equal([ids[1]], _db.Search("qlx").ToArray());
        Assert.Equal(2, _db.Search("周杰伦").Length);
        Assert.Equal([ids[2]], _db.Search("come tog").ToArray());
        Assert.Equal([ids[2]], _db.Search("abbey").ToArray());
        Assert.Empty(_db.Search("\"*"));
    }

    [Fact]
    public void UserData_SessionTagsMarkersRoundTrip()
    {
        var root = _db.AddRoot(_f.Music);
        var ids = _db.UpsertTracks(root.Id,
        [
            new ScannedTrack("a.flac", 1, 1, new TrackMeta("A", "X", "", "Y", 1, 1, null, TimeSpan.FromMinutes(3)), new AudioFormat("flac", 44100, 16, 2), null),
            new ScannedTrack("b.flac", 1, 1, new TrackMeta("B", "X", "", "Y", 1, 2, null, TimeSpan.FromMinutes(3)), new AudioFormat("flac", 44100, 16, 2), null),
        ]);

        var now = DateTimeOffset.Parse("2026-10-18T12:00:00Z");
        _db.RecordPlay(ids[0], now);
        var user = _db.RecordPlay(ids[0], now.AddMinutes(5));
        Assert.Equal(2, user.PlayCount);
        _db.SetFavorite(ids[1], true);
        _db.SetNote(ids[1], "副歌很好");
        var all = _db.LoadUserData();
        Assert.True(all[ids[1]].Favorite);
        Assert.Equal("副歌很好", _db.GetNote(ids[1]));

        _db.SetTags(ids[0], ["夜晚", "钢琴", "夜晚"]);
        Assert.Equal(["夜晚", "钢琴"], _db.GetTags(ids[0]).Order(StringComparer.Ordinal));
        _db.SetTags(ids[0], ["钢琴"]);
        Assert.Equal(["钢琴"], _db.GetTags(ids[0]));

        var m = _db.AddMarker(ids[0], TimeSpan.FromSeconds(61.5), "高潮");
        Assert.Equal(TimeSpan.FromMilliseconds(61500), _db.GetMarkers(ids[0]).Single().Position);
        _db.SetMarkerLabel(m.Id, "尾声");
        Assert.Equal("尾声", _db.GetMarkers(ids[0]).Single().Label);
        _db.DeleteMarker(m.Id);
        Assert.Empty(_db.GetMarkers(ids[0]));

        var index = _db.LoadIndex();
        var ctx = index.Arrange(Arrangement.Folder);
        var session = new SessionSnapshot(ids[1], ctx, 1, TimeSpan.FromSeconds(42), RepeatMode.One, Arrangement.Album);
        _db.SaveSession(session);
        var loaded = _db.LoadSession()!;
        Assert.Equal(session.Track, loaded.Track);
        Assert.Equal(ctx.Order.ToArray(), loaded.ContextSnapshot.Order.ToArray());
        Assert.Equal(ctx.Groups.ToArray(), loaded.ContextSnapshot.Groups.ToArray());
        Assert.Equal(ctx.Scope, loaded.ContextSnapshot.Scope);
        Assert.Equal(TimeSpan.FromSeconds(42), loaded.Position);
        Assert.Equal(RepeatMode.One, loaded.Repeat);
        Assert.Equal(Arrangement.Album, loaded.Arrangement);

        _db.RebuildGroups(index);
        _db.SaveAnalysis(ids[0], WaveformData.CurrentVersion, SoundFeatures.CurrentVersion, Palette.Neutral);
        Assert.Equal(Palette.Neutral, _db.GetAnalysis(ids[0])!.Value.Palette);
        Assert.Equal([ids[1]], _db.GetUnanalyzed(WaveformData.CurrentVersion, SoundFeatures.CurrentVersion));
        // 特征版本过期：只要重新分析就会被列出
        _db.SaveAnalysis(ids[0], WaveformData.CurrentVersion, 0, Palette.Neutral);
        Assert.Equal(2, _db.GetUnanalyzed(WaveformData.CurrentVersion, SoundFeatures.CurrentVersion).Count);

        var withSwatches = Palette.Neutral with { Swatches = SwatchSet.Of([new Rgb(1, 2, 3), new Rgb(200, 100, 50)]) };
        _db.SaveAnalysis(ids[0], WaveformData.CurrentVersion, SoundFeatures.CurrentVersion, withSwatches);
        Assert.Equal(withSwatches, _db.GetAnalysis(ids[0])!.Value.Palette);
    }
}

public sealed class AnalysisTests : IDisposable
{
    private readonly LibraryFixture _f = new();
    public void Dispose() => _f.Dispose();

    [Fact]
    public void Waveform_HasFixedBinsAndReflectsLoudness()
    {
        // 前半段正弦，后半段静音
        var samples = Wav.Sine(22050 * 4).Concat(new float[22050 * 4]).ToArray();
        var path = Wav.Write(Path.Combine(_f.Dir, "w.wav"), samples);
        using var reader = new WavPcmReader(path, 22050, 1);
        var wf = WaveformAnalyzer.Analyze(reader);

        Assert.Equal(WaveformData.OverviewBins, wf.OverviewRms.Length);
        Assert.Equal(8.0, wf.Duration.TotalSeconds, 3);
        Assert.Equal(800, wf.DetailRms.Length);
        Assert.Equal(WaveformAnalyzer.DetailRate, wf.DetailRate);
        Assert.Equal(65535, wf.OverviewRms.Max());           // 最响处归一化为 1
        Assert.True(wf.OverviewRms[100] > 60000);
        Assert.Equal(0, wf.OverviewRms[2000]);
        Assert.Equal(0, wf.DetailPeak[700]);
        Assert.True(wf.OverviewPeak[100] > 60000);

        using var ms = new MemoryStream();
        wf.Write(ms);
        Assert.InRange(ms.Length, 8000, 12000); // 整曲 8KB + 8 秒高密度包络
        ms.Position = 0;
        var back = WaveformData.Read(ms)!;
        Assert.Equal(wf.OverviewRms, back.OverviewRms);
        Assert.Equal(wf.DetailPeak, back.DetailPeak);
        Assert.Equal(wf.Duration, back.Duration);
    }

    [Fact]
    public void Waveform_CompressionKeepsQuietPassagesVisible()
    {
        var v = WaveformAnalyzer.Compress([1.0, 0.1, 0.0]);
        Assert.Equal(1.0, v[0], 6);
        Assert.Equal(Math.Log10(1.9), v[1], 6); // ≈0.28，远高于线性的 0.1
        Assert.Equal(0.0, v[2]);
    }

    [Fact]
    public void Waveform_SilentTrackIsFlat()
    {
        var path = Wav.Write(Path.Combine(_f.Dir, "s.wav"), new float[22050]);
        using var reader = new WavPcmReader(path);
        var wf = WaveformAnalyzer.Analyze(reader);
        Assert.All(wf.OverviewRms, v => Assert.Equal(0, v));
    }

    [Theory]
    [InlineData(220, 30, 30)]
    [InlineData(250, 240, 200)]
    [InlineData(20, 20, 25)]
    [InlineData(120, 120, 120)]
    public void Palette_SolidColorsProduceReadableText(byte r, byte g, byte b)
    {
        var px = new byte[64 * 64 * 4];
        for (int i = 0; i < px.Length; i += 4) { px[i] = r; px[i + 1] = g; px[i + 2] = b; px[i + 3] = 255; }
        var p = PaletteExtractor.Extract(px);
        Assert.True(PaletteExtractor.Contrast(p.TextOnBase, p.Base) >= PaletteExtractor.MinTextContrast);
        Assert.True(PaletteExtractor.Contrast(p.Accent, p.Dim) >= 3.0);
        Assert.True(PaletteExtractor.Contrast(p.Accent, p.Base) >= Palette.MinAccentContrast, $"accent {p.Accent} on base {p.Base}");
        Assert.True(PaletteExtractor.RelativeLuminance(p.Dim) <= PaletteExtractor.RelativeLuminance(p.Base));
    }

    [Fact]
    public void Palette_PicksDominantChromaticColor()
    {
        // 70% 深红 + 30% 灰
        var px = new byte[64 * 64 * 4];
        for (int i = 0; i < 64 * 64; i++)
        {
            bool red = i < 64 * 64 * 7 / 10;
            px[i * 4] = red ? (byte)170 : (byte)128;
            px[i * 4 + 1] = red ? (byte)20 : (byte)128;
            px[i * 4 + 2] = red ? (byte)30 : (byte)128;
            px[i * 4 + 3] = 255;
        }
        var p = PaletteExtractor.Extract(px);
        Assert.True(p.Base.R > p.Base.G * 2, $"base {p.Base}");
    }

    [Fact]
    public void OkLab_RoundTrips()
    {
        foreach (var c in new[] { new Rgb(0, 0, 0), new Rgb(255, 255, 255), new Rgb(12, 200, 99), new Rgb(250, 3, 128) })
            Assert.Equal(c, OkLab.FromRgb(c).ToRgb());
    }

    [Fact]
    public async Task Scheduler_ProducesWaveformPaletteAndThumbnails()
    {
        var music = _f.Music;
        Wav.Write(Path.Combine(music, "A", "B", "t.wav"), Wav.Sine(22050 * 2));
        File.WriteAllBytes(Path.Combine(music, "A", "B", "folder.png"), await Wav.SolidPng(30, 90, 200, 600));

        using var db = LibraryDatabase.Open(_f.Paths.Database);
        var root = db.AddRoot(music);
        new LibraryScanner(db, _f.Paths).Scan(root);
        var index = db.LoadIndex();
        var track = index.Tracks.Values.Single();

        using var scheduler = new AnalysisScheduler(db, _f.Paths, new PcmReaderFactory { PreferManagedWav = true }, index.Find, concurrency: 1);
        var done = new TaskCompletionSource<TrackId>();
        scheduler.Analyzed += id => done.TrySetResult(id);
        scheduler.Failed += (_, e) => done.TrySetException(e);
        scheduler.Enqueue(track.Id, AnalysisPriority.Background);
        scheduler.Enqueue(track.Id, AnalysisPriority.Playing); // 插队：同一首只分析一次
        Assert.Equal(track.Id, await done.Task.WaitAsync(TimeSpan.FromSeconds(30)));

        Assert.True(scheduler.IsAnalyzed(track.Id));
        var wf = AnalysisScheduler.LoadWaveform(_f.Paths, track.Id)!;
        Assert.Equal(2.0, wf.Duration.TotalSeconds, 2);
        var palette = db.GetAnalysis(track.Id)!.Value.Palette!;
        Assert.True(palette.Base.B > palette.Base.R, $"base {palette.Base}");
        Assert.True(palette.SpaceColors.Count >= 2);
        var features = AnalysisScheduler.LoadFeatures(_f.Paths, track.Id)!;
        Assert.Equal(2.0, features.Duration.TotalSeconds, 2);
        Assert.Equal(200, features.Length);
        var hash = db.GetCoverHash(track.Id)!;
        foreach (var size in AnalysisScheduler.ThumbSizes) Assert.True(File.Exists(_f.Paths.Thumb(hash, size)));
    }
}
