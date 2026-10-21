using Dapper;
using Solisten.Audio.Decoding;
using Solisten.Library.Analysis;
using Microsoft.Data.Sqlite;
using Solisten.Core.Audio;
using Solisten.Core.Model;
using Solisten.Library.Remote;
using Solisten.Library.Storage;

namespace Solisten.Library.Tests;

internal static class Remote
{
    public static RemoteTrack Song(string id, string title, string artist, int seconds, string album = "") =>
        new(new RemoteTrackId(RemoteProvider.NetEase, id), title, artist, album, TimeSpan.FromSeconds(seconds), null, null, false, DateTimeOffset.UtcNow);

    public static RemotePlaylist Playlist(string id, string name, bool owned = true, int count = 0) =>
        new(RemoteProvider.NetEase, id, name, null, null, owned, count, DateTimeOffset.UtcNow);

    public static Track Local(long id, string title, string artist, int seconds, string album = "", string? file = null, bool available = true) =>
        new(new TrackId(id), file ?? $@"C:\Music\{id}.flac",
            new TrackMeta(title, artist, "", album, 1, 1, null, TimeSpan.FromSeconds(seconds)),
            new AudioFormat("flac", 44100, 16, 2), new GroupKeys("1:", "Music", "a", "a"), available);

    public static ScannedTrack Scanned(string rel, string title, string artist, int seconds, string album = "") =>
        new(rel, 1, 1, new TrackMeta(title, artist, "", album, 1, 1, null, TimeSpan.FromSeconds(seconds)), new AudioFormat("flac", 44100, 16, 2), null);
}

public class TrackMatcherTests
{
    private static TrackMatcher Matcher(params Track[] tracks) => new(LibraryIndex.From(tracks));

    [Theory]
    [InlineData("晴天", "晴天")]
    [InlineData("ＡＢＣ　Ｄｅｆ", "abc def")]
    [InlineData("晴天（Live）", "晴天(live)")]
    [InlineData("晴天 (Live)", "晴天(live)")]
    [InlineData("【DJ版】夜曲", "[dj版]夜曲")]
    [InlineData("  Hello   World ", "hello world")]
    public void Normalize_UnifiesWidthCaseBracketsAndSpace(string input, string expected) =>
        Assert.Equal(expected, TrackMatcher.Normalize(input));

    [Fact]
    public void SplitArtists_KeepsEveryArtist()
    {
        Assert.Equal(["a", "b"], TrackMatcher.SplitArtists("A / B").Order(StringComparer.Ordinal));
        Assert.Equal(["周杰伦", "费玉清"], TrackMatcher.SplitArtists("周杰伦、费玉清").Order(StringComparer.Ordinal));
        Assert.Equal(["a", "b"], TrackMatcher.SplitArtists("A feat. B").Order(StringComparer.Ordinal));
        Assert.Equal(["a", "b"], TrackMatcher.SplitArtists("A & B").Order(StringComparer.Ordinal));
    }

    [Fact]
    public void TitleArtistAndCloseDuration_Matches()
    {
        var m = Matcher(Remote.Local(1, "晴天", "周杰伦", 270)).Match(Remote.Song("r1", "晴天", "周杰伦", 269));
        Assert.Equal(TrackMatchStatus.Matched, m.Status);
        Assert.Equal(new TrackId(1), m.LocalTrack);
        Assert.True(m.Confidence >= TrackMatcher.AutoMatchThreshold);
    }

    [Fact]
    public void FullWidthAndCaseDifferences_StillMatch()
    {
        var m = Matcher(Remote.Local(1, "Yesterday Once More", "Carpenters", 230)).Match(Remote.Song("r1", "ＹＥＳＴＥＲＤＡＹ ONCE more", "carpenters", 231));
        Assert.Equal(TrackMatchStatus.Matched, m.Status);
    }

    [Fact]
    public void ArtistListWithFeat_MatchesPrimaryArtist()
    {
        var m = Matcher(Remote.Local(1, "Song", "A", 200)).Match(Remote.Song("r1", "Song", "A feat. B", 201));
        Assert.Equal(TrackMatchStatus.Matched, m.Status);
    }

    [Theory]
    [InlineData(2, TrackMatchStatus.Matched)]
    [InlineData(4, TrackMatchStatus.MetadataConflict)]
    [InlineData(30, TrackMatchStatus.MetadataConflict)]
    public void DurationThresholds(int diffSeconds, TrackMatchStatus expected)
    {
        var m = Matcher(Remote.Local(1, "夜曲", "周杰伦", 226)).Match(Remote.Song("r1", "夜曲", "周杰伦", 226 + diffSeconds));
        Assert.Equal(expected, m.Status);
        if (expected != TrackMatchStatus.Matched) Assert.Null(m.LocalTrack);
    }

    [Fact]
    public void LiveVersion_DoesNotMatchStudioVersion()
    {
        var m = Matcher(Remote.Local(1, "晴天", "周杰伦", 269)).Match(Remote.Song("r1", "晴天 (Live)", "周杰伦", 269));
        Assert.Equal(TrackMatchStatus.Unmatched, m.Status);
        Assert.Null(m.LocalTrack);
    }

    [Fact]
    public void TitleOnly_IsAmbiguousNotMatched()
    {
        var m = Matcher(Remote.Local(1, "红豆", "方大同", 254)).Match(Remote.Song("r1", "红豆", "王菲", 254));
        Assert.NotEqual(TrackMatchStatus.Matched, m.Status);
        Assert.Null(m.LocalTrack);
    }

    [Fact]
    public void TwoEqualCandidates_AreAmbiguous()
    {
        var matcher = Matcher(Remote.Local(1, "晴天", "周杰伦", 269), Remote.Local(2, "晴天", "周杰伦", 269));
        var m = matcher.Match(Remote.Song("r1", "晴天", "周杰伦", 269));
        Assert.Equal(TrackMatchStatus.Ambiguous, m.Status);
        Assert.Null(m.LocalTrack);
        Assert.Equal(2, matcher.Candidates(Remote.Song("r1", "晴天", "周杰伦", 269)).Count);
    }

    [Fact]
    public void SameAlbum_BreaksTie()
    {
        var matcher = Matcher(Remote.Local(1, "晴天", "周杰伦", 269, "叶惠美"), Remote.Local(2, "晴天", "周杰伦", 269, "精选"));
        var m = matcher.Match(Remote.Song("r1", "晴天", "周杰伦", 269, "叶惠美"));
        Assert.Equal(TrackMatchStatus.Matched, m.Status);
        Assert.Equal(new TrackId(1), m.LocalTrack);
    }

    [Fact]
    public void FileNameFallback_WhenTagsAreMissing()
    {
        var local = Remote.Local(1, "Track 01", "周杰伦", 269, file: @"C:\Music\周杰伦 - 晴天.mp3");
        var m = Matcher(local).Match(Remote.Song("r1", "晴天", "周杰伦", 269));
        Assert.Equal(TrackMatchStatus.Matched, m.Status);
    }

    [Fact]
    public void UnavailableLocalFile_IsNotMatched_AndPreviousMatchBecomesMissing()
    {
        var remote = Remote.Song("r1", "晴天", "周杰伦", 269);
        var previous = new RemoteTrackMapping(remote.Id, new TrackId(1), TrackMatchStatus.Matched, 0.95, null);
        var m = Matcher(Remote.Local(1, "晴天", "周杰伦", 269, available: false)).Match(remote, previous);
        Assert.Equal(TrackMatchStatus.LocalFileMissing, m.Status);
        Assert.False(m.IsPlayable(LibraryIndex.Empty));
    }

    [Fact]
    public void ManualMapping_IsKept()
    {
        var remote = Remote.Song("r1", "完全不同的标题", "某人", 100);
        var manual = new RemoteTrackMapping(remote.Id, new TrackId(1), TrackMatchStatus.Matched, 1, "用户选择", IsManual: true);
        var m = Matcher(Remote.Local(1, "晴天", "周杰伦", 269)).Match(remote, manual);
        Assert.Equal(TrackMatchStatus.Matched, m.Status);
        Assert.Equal(new TrackId(1), m.LocalTrack);
        Assert.True(m.IsManual);
    }
}

public class RemoteMusicDatabaseTests
{
    private static (LibraryDatabase Db, RemoteMusicDatabase Remote, long RootId) Open()
    {
        var db = LibraryDatabase.OpenInMemory();
        var root = db.AddRoot(Path.Combine(Path.GetTempPath(), "solisten-remote-tests"));
        return (db, new RemoteMusicDatabase(db), root.Id);
    }

    private static long Account(RemoteMusicDatabase remote) =>
        remote.UpsertAccount(new RemoteAccount(RemoteProvider.NetEase, "u1", "测试", null)).Id;

    [Fact]
    public void ResyncingSamePlaylist_IsIdempotent()
    {
        var (db, remote, _) = Open();
        using var _db = db;
        var account = Account(remote);
        var tracks = new[] { Remote.Song("1", "晴天", "周杰伦", 269), Remote.Song("2", "七里香", "周杰伦", 299) };
        for (int i = 0; i < 3; i++)
        {
            remote.UpsertPlaylists(account, [Remote.Playlist("p1", "我喜欢的音乐", count: 2)], complete: true);
            remote.ReplacePlaylistItems(RemoteProvider.NetEase, "p1", tracks);
        }
        Assert.Single(remote.GetPlaylists(RemoteProvider.NetEase));
        Assert.Equal(2, remote.GetTracks(RemoteProvider.NetEase).Count);
        Assert.Equal(["1", "2"], remote.GetPlaylistEntries(RemoteProvider.NetEase, "p1").Select(e => e.Track.Id.Value));
    }

    [Fact]
    public void ReorderAndRemoval_OnlyChangesPlaylistItems()
    {
        var (db, remote, _) = Open();
        using var _db = db;
        var account = Account(remote);
        remote.UpsertPlaylists(account, [Remote.Playlist("p1", "A")], complete: true);
        remote.ReplacePlaylistItems(RemoteProvider.NetEase, "p1", [Remote.Song("1", "a", "x", 1), Remote.Song("2", "b", "x", 1), Remote.Song("3", "c", "x", 1)]);
        remote.ReplacePlaylistItems(RemoteProvider.NetEase, "p1", [Remote.Song("3", "c", "x", 1), Remote.Song("1", "a", "x", 1)]);

        Assert.Equal(["3", "1"], remote.GetPlaylistEntries(RemoteProvider.NetEase, "p1").Select(e => e.Track.Id.Value));
        Assert.Equal(3, remote.GetTracks(RemoteProvider.NetEase).Count); // 远程曲目本身保留
    }

    [Fact]
    public void RemoteDeletedPlaylist_IsMarkedNotDeleted_AndLocalTracksUntouched()
    {
        var (db, remote, rootId) = Open();
        using var _db = db;
        var local = db.UpsertTracks(rootId, [Remote.Scanned("晴天.flac", "晴天", "周杰伦", 269)])[0];
        var account = Account(remote);
        remote.UpsertPlaylists(account, [Remote.Playlist("p1", "A"), Remote.Playlist("p2", "B")], complete: true);
        remote.ReplacePlaylistItems(RemoteProvider.NetEase, "p2", [Remote.Song("1", "晴天", "周杰伦", 269)]);
        remote.SaveMappings([new RemoteTrackMapping(new RemoteTrackId(RemoteProvider.NetEase, "1"), local, TrackMatchStatus.Matched, 0.95, null)]);

        remote.UpsertPlaylists(account, [Remote.Playlist("p1", "A")], complete: true);

        var p2 = remote.GetPlaylists(RemoteProvider.NetEase).Single(p => p.Playlist.Id == "p2");
        Assert.True(p2.RemoteDeleted);
        Assert.Single(remote.GetPlaylistEntries(RemoteProvider.NetEase, "p2"));
        Assert.NotNull(db.LoadIndex().Find(local));
    }

    [Fact]
    public void DeletingLocalTrack_TurnsMatchIntoLocalFileMissing()
    {
        var (db, remote, rootId) = Open();
        using var _db = db;
        var local = db.UpsertTracks(rootId, [Remote.Scanned("晴天.flac", "晴天", "周杰伦", 269)])[0];
        var account = Account(remote);
        remote.UpsertPlaylists(account, [Remote.Playlist("p1", "A")], complete: true);
        remote.ReplacePlaylistItems(RemoteProvider.NetEase, "p1", [Remote.Song("1", "晴天", "周杰伦", 269)]);
        remote.SaveMappings([new RemoteTrackMapping(new RemoteTrackId(RemoteProvider.NetEase, "1"), local, TrackMatchStatus.Matched, 0.95, null)]);
        Assert.Equal([local], remote.PlayableTracks(RemoteProvider.NetEase, "p1", db.LoadIndex()).ToArray());

        db.RemoveRoot(rootId);

        var entry = remote.GetPlaylistEntries(RemoteProvider.NetEase, "p1").Single();
        Assert.Equal(TrackMatchStatus.LocalFileMissing, entry.Mapping.Status);
        Assert.Empty(remote.PlayableTracks(RemoteProvider.NetEase, "p1", db.LoadIndex()));
    }

    [Fact]
    public void PlayableTracks_ContainsOnlyMatchedAvailableTracks()
    {
        var (db, remote, rootId) = Open();
        using var _db = db;
        var ids = db.UpsertTracks(rootId, [Remote.Scanned("a.flac", "a", "x", 100), Remote.Scanned("b.flac", "b", "x", 100)]);
        db.SetAvailability([ids[1]], false);
        var account = Account(remote);
        remote.UpsertPlaylists(account, [Remote.Playlist("p1", "A")], complete: true);
        remote.ReplacePlaylistItems(RemoteProvider.NetEase, "p1", [Remote.Song("1", "a", "x", 100), Remote.Song("2", "b", "x", 100), Remote.Song("3", "c", "x", 100)]);
        remote.SaveMappings([
            new RemoteTrackMapping(new RemoteTrackId(RemoteProvider.NetEase, "1"), ids[0], TrackMatchStatus.Matched, 0.95, null),
            new RemoteTrackMapping(new RemoteTrackId(RemoteProvider.NetEase, "2"), ids[1], TrackMatchStatus.Matched, 0.95, null),
        ]);

        Assert.Equal([ids[0]], remote.PlayableTracks(RemoteProvider.NetEase, "p1", db.LoadIndex()).ToArray());
        Assert.Equal(TrackMatchStatus.Unmatched, remote.GetPlaylistEntries(RemoteProvider.NetEase, "p1")[2].Mapping.Status);
    }

    [Fact]
    public void InterruptedRun_IsClosedOnNextStart()
    {
        var (db, remote, _) = Open();
        using var _db = db;
        remote.StartSyncRun(RemoteProvider.NetEase, null);
        var second = remote.StartSyncRun(RemoteProvider.NetEase, null);
        remote.FinishSyncRun(second, new SyncRunResult(SyncRunStatus.Succeeded, 1, 2, 1, 0, null));

        var last = remote.GetLastSyncRun(RemoteProvider.NetEase)!;
        Assert.Equal(SyncRunStatus.Succeeded, last.Status);
        Assert.Equal(0, db.Locked(c => c.ExecuteScalar<long>("SELECT COUNT(*) FROM sync_runs WHERE status='Running'")));
    }

    [Fact]
    public void DeleteProviderData_RemovesRemoteCacheOnly()
    {
        var (db, remote, rootId) = Open();
        using var _db = db;
        db.UpsertTracks(rootId, [Remote.Scanned("a.flac", "a", "x", 100)]);
        var account = Account(remote);
        remote.UpsertPlaylists(account, [Remote.Playlist("p1", "A")], complete: true);
        remote.ReplacePlaylistItems(RemoteProvider.NetEase, "p1", [Remote.Song("1", "a", "x", 100)]);

        remote.DeleteProviderData(RemoteProvider.NetEase);

        Assert.Null(remote.GetAccount(RemoteProvider.NetEase));
        Assert.Empty(remote.GetPlaylists(RemoteProvider.NetEase));
        Assert.Empty(remote.GetTracks(RemoteProvider.NetEase));
        Assert.Equal(1, db.LoadIndex().Count);
    }

    [Fact]
    public void Migration_FromSchema2_AddsRemoteTablesAndKeepsData()
    {
        var dir = Path.Combine(Path.GetTempPath(), "solisten-migrate-tests", Guid.NewGuid().ToString("N"));
        var path = Path.Combine(dir, "library.db");
        try
        {
            TrackId local;
            using (var db = LibraryDatabase.Open(path))
            {
                var root = db.AddRoot(dir);
                local = db.UpsertTracks(root.Id, [Remote.Scanned("a.flac", "a", "x", 100)])[0];
                // 退回到 schema 2：删除远程表。
                db.Locked(c => c.Execute("""
                    DROP TABLE sync_runs; DROP TABLE remote_track_mappings; DROP TABLE remote_playlist_items;
                    DROP TABLE remote_tracks; DROP TABLE remote_playlists; DROP TABLE remote_accounts;
                    PRAGMA user_version=2;
                    """));
            }

            using (var db = LibraryDatabase.Open(path))
            {
                Assert.Equal(LibraryDatabase.SchemaVersion, db.Locked(c => c.ExecuteScalar<long>("PRAGMA user_version")));
                Assert.NotNull(db.LoadIndex().Find(local));
                var remote = new RemoteMusicDatabase(db);
                Assert.Empty(remote.GetPlaylists(RemoteProvider.NetEase));
                Assert.NotEqual(0, Account(remote));
            }
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public void NoCredentialColumns_InRemoteSchema()
    {
        using var db = LibraryDatabase.OpenInMemory();
        var columns = db.Locked(c => c.Query<string>("""
            SELECT p.name FROM sqlite_master m JOIN pragma_table_info(m.name) p
            WHERE m.type='table' AND (m.name LIKE 'remote_%' OR m.name='sync_runs')
            """).ToList());
        Assert.DoesNotContain(columns, c => c.Contains("cookie", StringComparison.OrdinalIgnoreCase)
                                          || c.Contains("token", StringComparison.OrdinalIgnoreCase)
                                          || c.Contains("secret", StringComparison.OrdinalIgnoreCase));
    }
}

public sealed class StreamAnalysisTests : IDisposable
{
    private readonly LibraryFixture _f = new();

    public void Dispose() => _f.Dispose();

    [Fact]
    public async Task OnlineTrack_PaletteAndFeaturesAreCachedOutsideDatabase()
    {
        var cover = Path.Combine(_f.Dir, "cover.png");
        await File.WriteAllBytesAsync(cover, await Wav.SolidPng(200, 40, 40));
        using var db = LibraryDatabase.OpenInMemory();
        using var scheduler = new AnalysisScheduler(db, _f.Paths, new PcmReaderFactory { PreferManagedWav = true }, _ => null, concurrency: 1);
        var id = new TrackId(-123456);

        var palette = scheduler.EnsureStreamPalette(id, cover);
        Assert.NotNull(palette);
        Assert.Equal(palette, AnalysisScheduler.LoadStreamPalette(_f.Paths, id));
        Assert.Equal(palette, scheduler.EnsureStreamPalette(id, null)); // 已经提取过：不需要封面
        Assert.Null(scheduler.EnsureStreamPalette(new TrackId(-1), Path.Combine(_f.Dir, "missing.png")));

        Assert.False(AnalysisScheduler.HasStreamFeatures(_f.Paths, id));
        var features = new SoundFeatures(TimeSpan.FromSeconds(1), 100, new ushort[100], new ushort[100], new ushort[100], new short[100], []);
        scheduler.SaveStreamFeatures(id, features);
        Assert.True(AnalysisScheduler.HasStreamFeatures(_f.Paths, id));
        Assert.Equal(100, AnalysisScheduler.LoadFeatures(_f.Paths, id)!.Length);
        Assert.Null(db.GetAnalysis(id)); // 在线曲目不写数据库
    }
}

public class LiveFeatureTests
{
    private const int Rate = 22050;

    /// <summary>有起伏和重音的立体声测试信号：每 0.6 秒一次短促的强脉冲，叠在缓慢变化的正弦上。</summary>
    private static float[] Signal(double seconds)
    {
        int frames = (int)(seconds * Rate);
        var s = new float[frames * 2];
        for (int i = 0; i < frames; i++)
        {
            double t = i / (double)Rate;
            double beat = (t % 0.6) < 0.03 ? 0.9 : 0;
            double tone = 0.3 * (0.6 + 0.4 * Math.Sin(t)) * Math.Sin(2 * Math.PI * 220 * t);
            s[i * 2] = (float)(tone + beat * Math.Sin(2 * Math.PI * 60 * t));
            s[i * 2 + 1] = (float)(tone * 0.7);
        }
        return s;
    }

    private static void Feed(IDecodeTap tap, TrackId id, float[] pcm, long fromFrame, long toFrame, int chunk = 4096)
    {
        for (long f = fromFrame; f < toFrame; f += chunk)
        {
            int n = (int)Math.Min(chunk, toFrame - f);
            tap.Decoded(id, f, pcm.AsSpan((int)f * 2, n * 2), Rate, 2);
        }
    }

    private static void AssertSame(SoundFeatures a, SoundFeatures b)
    {
        Assert.Equal(a.Length, b.Length);
        Assert.Equal(a.Low, b.Low);
        Assert.Equal(a.Mid, b.Mid);
        Assert.Equal(a.High, b.High);
        Assert.Equal(a.Balance, b.Balance);
        Assert.Equal(a.Onsets, b.Onsets);
    }

    [Fact]
    public async Task PlayedThrough_RecordsExactlyTheOfflineFeatures()
    {
        var pcm = Signal(8);
        var offline = new SoundFeatureAnalyzer.Accumulator(Rate, 2, TimeSpan.FromSeconds(8));
        offline.Add(pcm);
        var expected = offline.Finish();
        Assert.NotEmpty(expected.Onsets);

        var id = new TrackId(-7);
        using var recorder = new LiveFeatureRecorder(t => t.IsRemote);
        var done = new TaskCompletionSource<SoundFeatures>();
        int live = 0;
        recorder.LiveUpdated += (t, f) => { if (t == id) Interlocked.Increment(ref live); };
        recorder.Completed += (t, f) => { if (t == id) done.TrySetResult(f); };
        Feed(recorder, id, pcm, 0, pcm.Length / 2);
        recorder.Ended(id);

        AssertSame(expected, await done.Task.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.InRange(live, 7, 10); // 约每秒一次实时快照
    }

    [Fact]
    public async Task LiveSnapshot_GrowsWithDecodedAudio()
    {
        var pcm = Signal(4);
        var id = new TrackId(-8);
        using var recorder = new LiveFeatureRecorder(_ => true);
        var snapshots = new List<SoundFeatures>();
        var got3 = new TaskCompletionSource();
        recorder.LiveUpdated += (_, f) => { lock (snapshots) { snapshots.Add(f); if (snapshots.Count == 3) got3.TrySetResult(); } };
        Feed(recorder, id, pcm, 0, Rate * 3);
        await got3.Task.WaitAsync(TimeSpan.FromSeconds(10));
        lock (snapshots)
        {
            Assert.True(snapshots[1].Length > snapshots[0].Length);
            Assert.InRange(snapshots[^1].Duration.TotalSeconds, 2.0, 3.0);
            Assert.True(snapshots[^1].Sample(TimeSpan.FromSeconds(1.5)).Low > 0);
        }
    }

    [Fact]
    public async Task SeekingForward_LeavesGap_NotSaved_UntilGapIsReplayed()
    {
        var pcm = Signal(6);
        var id = new TrackId(-9);
        using var recorder = new LiveFeatureRecorder(_ => true);
        var completed = new List<SoundFeatures>();
        var ended = new SemaphoreSlim(0);
        recorder.Completed += (_, f) => { lock (completed) completed.Add(f); };
        recorder.LiveUpdated += (_, _) => { };

        // 0–2 秒，然后跳到 4 秒播完：中间有空缺，不保存。
        Feed(recorder, id, pcm, 0, Rate * 2);
        Feed(recorder, id, pcm, Rate * 4, Rate * 6);
        recorder.Ended(id);

        // 第二次：从 3 秒开始（中途开始也是空缺），跳回 0 秒从头播完：空缺被补上，保存。
        Feed(recorder, new TrackId(-10), pcm, 0, Rate); // 换一首歌，结束上一个会话
        Feed(recorder, id, pcm, Rate * 3, Rate * 4);
        Feed(recorder, id, pcm, 0, Rate * 6);
        var done = new TaskCompletionSource<SoundFeatures>();
        recorder.Completed += (t, f) => { if (t == id) done.TrySetResult(f); };
        recorder.Ended(id);
        var full = await done.Task.WaitAsync(TimeSpan.FromSeconds(10));

        lock (completed) Assert.Single(completed);
        var offline = new SoundFeatureAnalyzer.Accumulator(Rate, 2, TimeSpan.FromSeconds(6));
        offline.Add(pcm);
        Assert.Equal(offline.Finish().Length, full.Length);
    }

    [Fact]
    public void Accumulator_JumpForwardPadsAndMarksGap_JumpBackClearsIt()
    {
        var pcm = Signal(3);
        var acc = new SoundFeatureAnalyzer.Accumulator(Rate, 2, TimeSpan.FromSeconds(3));
        acc.Add(pcm.AsSpan(0, Rate * 2)); // 1 秒
        acc.JumpTo(Rate * 2);
        Assert.True(acc.HasGaps);
        Assert.Equal(Rate * 2, acc.Position);
        acc.Add(pcm.AsSpan(Rate * 4, Rate * 2));
        Assert.InRange(acc.Snapshot().Duration.TotalSeconds, 2.9, 3.0);

        acc.JumpTo(Rate / 2);
        Assert.False(acc.HasGaps);
        Assert.InRange(acc.Snapshot().Duration.TotalSeconds, 0.49, 0.5);
    }

    [Fact]
    public void RejectedTracks_AreIgnored()
    {
        using var recorder = new LiveFeatureRecorder(_ => false);
        bool any = false;
        recorder.LiveUpdated += (_, _) => any = true;
        recorder.Decoded(new TrackId(5), 0, new float[8192], Rate, 2);
        Thread.Sleep(200);
        Assert.False(any);
    }
}
