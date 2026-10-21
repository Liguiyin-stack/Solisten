using Solisten.Core.Model;
using Solisten.Integrations.NetEase.Api;
using Solisten.Integrations.NetEase.Auth;
using Solisten.Integrations.NetEase.Sync;
using Solisten.Library.Remote;
using Solisten.Library.Storage;
using static Solisten.Integrations.NetEase.Api.FakeMusicProvider;

namespace Solisten.Integrations.NetEase.Tests;

public sealed class SyncFixture : IDisposable
{
    public static readonly SyncPolicy Fast = new()
    {
        PageSize = 2,
        MinRequestInterval = TimeSpan.Zero,
        RetryBaseDelay = TimeSpan.FromMilliseconds(1),
        RateLimitDelay = TimeSpan.FromMilliseconds(1),
        RequestTimeout = TimeSpan.FromSeconds(5),
    };

    public LibraryDatabase Db { get; } = LibraryDatabase.OpenInMemory();
    public RemoteMusicDatabase Remote { get; }
    public FakeMusicProvider Provider { get; } = new();
    public InMemoryCredentialStore Store { get; } = new();
    public NetEaseSyncService Sync { get; }
    public long RootId { get; }

    public SyncFixture(SyncPolicy? policy = null)
    {
        Remote = new RemoteMusicDatabase(Db);
        RootId = Db.AddRoot(Path.Combine(Path.GetTempPath(), "solisten-sync-tests")).Id;
        Sync = new NetEaseSyncService(Provider, Store, Remote, Db.LoadIndex, policy ?? Fast);
    }

    public void SignIn() => Store.Save(RemoteProvider.NetEase, Provider.Account.UserId, new ProviderCredential("fake-session"));

    public TrackId AddLocal(string rel, string title, string artist, int seconds) =>
        Db.UpsertTracks(RootId, [new ScannedTrack(rel, 1, 1, new TrackMeta(title, artist, "", "", 1, 1, null, TimeSpan.FromSeconds(seconds)), new AudioFormat("flac", 44100, 16, 2), null)])[0];

    public Task<SyncSummary> Run(SyncOptions? options = null, IProgress<SyncProgress>? progress = null, CancellationToken ct = default) =>
        Sync.SyncAsync(options ?? SyncOptions.OwnedOnly, progress, ct);

    public void Dispose() => Db.Dispose();
}

public class SyncTests
{
    private static RemoteTrack[] Songs(string prefix, int n) =>
        [.. Enumerable.Range(1, n).Select(i => Song($"{prefix}{i}", $"歌{prefix}{i}", "某人", 200 + i))];

    [Fact]
    public async Task NotSignedIn_FailsWithoutAnyRequest()
    {
        using var f = new SyncFixture();
        f.Provider.AddPlaylist("p1", "A", owned: true, Songs("a", 1));

        var summary = await f.Run();

        Assert.Equal(SyncRunStatus.Failed, summary.Status);
        Assert.Equal("登录已失效，请重新登录", summary.Message);
        Assert.Equal(0, f.Provider.Calls);
        Assert.Equal(SyncRunStatus.Failed, f.Remote.GetLastSyncRun(RemoteProvider.NetEase)!.Status);
    }

    [Fact]
    public async Task PagedPlaylistsAndTracks_AreStoredInOrder()
    {
        using var f = new SyncFixture();
        f.SignIn();
        for (int i = 1; i <= 5; i++) f.Provider.AddPlaylist($"p{i}", $"歌单{i}", owned: true, Songs($"p{i}-", 5));

        var progress = new Recorder<SyncProgress>();
        var summary = await f.Run(progress: progress);

        Assert.Equal(SyncRunStatus.Succeeded, summary.Status);
        Assert.Equal(5, summary.Playlists);
        Assert.Equal(25, summary.Tracks);
        Assert.Equal(["p1", "p2", "p3", "p4", "p5"], f.Remote.GetPlaylists(RemoteProvider.NetEase).Select(p => p.Playlist.Id));
        Assert.Equal(Songs("p3-", 5).Select(s => s.Id.Value), f.Remote.GetPlaylistEntries(RemoteProvider.NetEase, "p3").Select(e => e.Track.Id.Value));
        Assert.Equal(SyncStage.Completed, progress.Items[^1].Stage);
        Assert.NotNull(f.Remote.GetAccount(RemoteProvider.NetEase)!.LastSyncAt);
    }

    [Fact]
    public async Task SubscribedPlaylists_OnlySyncedWhenSelected()
    {
        using var f = new SyncFixture();
        f.SignIn();
        f.Provider.AddPlaylist("mine", "我的", owned: true, Songs("m", 1))
                  .AddPlaylist("fav", "收藏", owned: false, Songs("f", 1));

        await f.Run();
        Assert.Empty(f.Remote.GetPlaylistEntries(RemoteProvider.NetEase, "fav"));
        Assert.Equal(2, f.Remote.GetPlaylists(RemoteProvider.NetEase).Count); // 名称仍然记录，供选择

        await f.Run(new SyncOptions(new HashSet<string> { "fav" }));
        Assert.Single(f.Remote.GetPlaylistEntries(RemoteProvider.NetEase, "fav"));
    }

    [Fact]
    public async Task RepeatedSync_IsIdempotent()
    {
        using var f = new SyncFixture();
        f.SignIn();
        f.AddLocal("晴天.flac", "晴天", "周杰伦", 269);
        f.Provider.AddPlaylist("p1", "A", owned: true, Song("1", "晴天", "周杰伦", 269), Song("2", "七里香", "周杰伦", 299));

        var first = await f.Run();
        var second = await f.Run();

        Assert.Equal(first with { FailedPlaylists = [] }, second with { FailedPlaylists = [] });
        Assert.Equal(2, f.Remote.GetTracks(RemoteProvider.NetEase).Count);
        Assert.Equal(2, f.Remote.GetMappings(RemoteProvider.NetEase).Count);
    }

    [Fact]
    public async Task MatchedTracks_AreTheOnlyPlayableOnes()
    {
        using var f = new SyncFixture();
        f.SignIn();
        var local = f.AddLocal("晴天.flac", "晴天", "周杰伦", 269);
        f.Provider.AddPlaylist("p1", "A", owned: true, Song("1", "晴天", "周杰伦", 270), Song("2", "七里香", "周杰伦", 299));

        var summary = await f.Run();

        Assert.Equal(1, summary.Matched);
        Assert.Equal(1, summary.Unmatched);
        Assert.Equal([local], f.Remote.PlayableTracks(RemoteProvider.NetEase, "p1", f.Db.LoadIndex()).ToArray());
        var entries = f.Remote.GetPlaylistEntries(RemoteProvider.NetEase, "p1");
        Assert.Equal(TrackMatchStatus.Unmatched, entries[1].Mapping.Status);
    }

    [Fact]
    public async Task LocalFileAddedLater_MatchesOnRematch()
    {
        using var f = new SyncFixture();
        f.SignIn();
        f.Provider.AddPlaylist("p1", "A", owned: true, Song("1", "晴天", "周杰伦", 269));
        Assert.Equal(0, (await f.Run()).Matched);

        f.AddLocal("晴天.flac", "晴天", "周杰伦", 269);
        Assert.Equal((1, 0), f.Sync.Rematch());
    }

    [Fact]
    public async Task OnePlaylistFailing_IsPartialSuccess_AndKeepsOthers()
    {
        using var f = new SyncFixture();
        f.SignIn();
        f.Provider.AddPlaylist("ok", "正常", owned: true, Songs("o", 3))
                  .AddPlaylist("bad", "出错", owned: true, Songs("b", 3));
        f.Provider.Fail = call => call == "tracks:bad" ? new NetEaseApiException(NetEaseErrorKind.InvalidResponse, "unknown shape") : null;

        var summary = await f.Run();

        Assert.Equal(SyncRunStatus.PartiallySucceeded, summary.Status);
        Assert.Equal(["出错"], summary.FailedPlaylists);
        Assert.Equal(3, f.Remote.GetPlaylistEntries(RemoteProvider.NetEase, "ok").Count);
    }

    [Fact]
    public async Task ServerErrors_AreRetriedWithLimit()
    {
        using var f = new SyncFixture();
        f.SignIn();
        f.Provider.AddPlaylist("p1", "A", owned: true, Songs("a", 1));
        int failures = 0;
        f.Provider.Fail = call => call == "playlists" && failures++ < 2 ? new NetEaseApiException(NetEaseErrorKind.ServerError, "502") : null;

        Assert.Equal(SyncRunStatus.Succeeded, (await f.Run()).Status);
        Assert.Equal(3, failures);

        failures = 0;
        f.Provider.Fail = call => call == "playlists" && ++failures > 0 ? new NetEaseApiException(NetEaseErrorKind.ServerError, "502") : null;
        var summary = await f.Run();
        Assert.Equal(SyncRunStatus.Failed, summary.Status);
        Assert.Equal(SyncFixture.Fast.MaxAttempts, failures);
    }

    [Fact]
    public async Task RateLimited_StopsAfterOneRetry_AndKeepsCache()
    {
        using var f = new SyncFixture();
        f.SignIn();
        f.Provider.AddPlaylist("p1", "A", owned: true, Songs("a", 2));
        await f.Run();

        int calls = 0;
        f.Provider.Fail = call => call.StartsWith("tracks", StringComparison.Ordinal) && ++calls > 0 ? new NetEaseApiException(NetEaseErrorKind.RateLimited, "429") : null;
        var summary = await f.Run();

        Assert.Equal(SyncRunStatus.Failed, summary.Status);
        Assert.Equal("服务暂时限制请求，请稍后再试", summary.Message);
        Assert.Equal(2, calls);
        Assert.Equal(2, f.Remote.GetPlaylistEntries(RemoteProvider.NetEase, "p1").Count);
    }

    [Fact]
    public async Task ExpiredSession_FailsAndKeepsCache()
    {
        using var f = new SyncFixture();
        f.SignIn();
        f.Provider.AddPlaylist("p1", "A", owned: true, Songs("a", 2));
        await f.Run();

        f.Store.Save(RemoteProvider.NetEase, "u", new ProviderCredential("expired"));
        var summary = await f.Run();

        Assert.Equal(SyncRunStatus.Failed, summary.Status);
        Assert.Null(f.Store.Load(RemoteProvider.NetEase));
        Assert.Single(await f.Sync.GetPlaylistsAsync(default));
        Assert.Equal(2, (await f.Sync.GetTracksAsync("p1", default)).Count);
    }

    [Fact]
    public async Task RemotelyDeletedPlaylist_IsHiddenButCached()
    {
        using var f = new SyncFixture();
        f.SignIn();
        f.Provider.AddPlaylist("p1", "A", owned: true, Songs("a", 1)).AddPlaylist("p2", "B", owned: true, Songs("b", 1));
        await f.Run();

        f.Provider.Playlists.RemoveAll(p => p.Id == "p2");
        await f.Run();

        Assert.Equal(["p1"], (await f.Sync.GetPlaylistsAsync(default)).Select(p => p.Id));
        Assert.True(f.Remote.GetPlaylists(RemoteProvider.NetEase).Single(p => p.Playlist.Id == "p2").RemoteDeleted);
    }

    [Fact]
    public async Task Cancel_RecordsCancelledRun_NotRunning()
    {
        using var f = new SyncFixture();
        f.SignIn();
        f.Provider.AddPlaylist("p1", "A", owned: true, Songs("a", 10));
        f.Provider.Latency = TimeSpan.FromMilliseconds(50);
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(120));

        var summary = await f.Run(ct: cts.Token);

        Assert.Equal(SyncRunStatus.Cancelled, summary.Status);
        Assert.Equal(SyncRunStatus.Cancelled, f.Remote.GetLastSyncRun(RemoteProvider.NetEase)!.Status);
        Assert.False(f.Sync.IsRunning);
    }

    [Fact]
    public async Task SlowRequest_TimesOut()
    {
        using var f = new SyncFixture(SyncFixture.Fast with { RequestTimeout = TimeSpan.FromMilliseconds(20), MaxAttempts = 2 });
        f.SignIn();
        f.Provider.Latency = TimeSpan.FromSeconds(5);

        var summary = await f.Run();

        Assert.Equal(SyncRunStatus.Failed, summary.Status);
        Assert.Equal("网络请求超时", summary.Message);
    }

    [Fact]
    public async Task ConcurrentSync_IsRejected()
    {
        using var f = new SyncFixture();
        f.SignIn();
        f.Provider.AddPlaylist("p1", "A", owned: true, Songs("a", 4));
        f.Provider.Latency = TimeSpan.FromMilliseconds(30);

        var first = f.Run();
        Assert.True(f.Sync.IsRunning);
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Run());
        Assert.Equal(SyncRunStatus.Succeeded, (await first).Status);
    }
}
