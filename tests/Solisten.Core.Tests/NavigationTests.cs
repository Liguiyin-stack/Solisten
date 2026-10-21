using System.Collections.Immutable;
using Solisten.Core.Model;
using static Solisten.Core.Tests.Fixtures;

namespace Solisten.Core.Tests;

public class NavigationTests
{
    [Fact]
    public void MovingFocus_EntersNavigateWithoutChangingPlayback() // I3
    {
        var h = new Harness();
        h.Do(new Resume());
        var playing = h.State.Playing;

        h.Do(new MoveFocus(+3));
        Assert.Equal(Layer.Navigate, h.State.Layer);
        Assert.Equal(T(4), h.State.Focus!.Track);
        Assert.Equal(T(4), h.State.Canvas.BrowseLens);
        Assert.Equal(T(1), h.State.Canvas.PlayingAnchor);
        Assert.Same(playing, h.State.Playing);
    }

    [Fact]
    public void Cancel_DiscardsBrowseLensAndReturnsToPlaying() // I6
    {
        var h = new Harness();
        h.Do(new MoveFocus(+5));
        h.Do(new CancelNavigate());
        Assert.Equal(Layer.Listen, h.State.Layer);
        Assert.Null(h.State.Canvas.BrowseLens);
        Assert.Equal(h.Playing, h.State.Focus!.Track);
    }

    [Fact]
    public void Confirm_UpdatesAnchorAndReturnsToListen() // I4, I6
    {
        var h = new Harness();
        h.Do(new MoveFocus(+5));
        h.Do(new ConfirmFocus());
        Assert.Equal(T(6), h.Playing);
        Assert.Equal(T(6), h.State.Canvas.PlayingAnchor);
        Assert.Equal(Layer.Listen, h.State.Layer);
        Assert.Equal(T(6), h.State.Focus!.Track);
    }

    [Fact]
    public void ConfirmCurrentTrack_DoesNotRestartIt()
    {
        var h = new Harness();
        h.Do(new Resume());
        h.Do(new PositionChanged(TimeSpan.FromSeconds(1)));
        h.Do(new EnterNavigate());
        var fx = h.Do(new ConfirmFocus());
        Assert.DoesNotContain(fx, e => e is LoadTrack);
        Assert.Equal(TimeSpan.FromSeconds(1), h.State.Playing!.Position);
    }

    [Fact]
    public void ConfirmUnavailable_DoesNothing()
    {
        var h = new Harness(Library(unavailable: 4));
        h.Do(new MoveFocus(+3));
        var fx = h.Do(new ConfirmFocus());
        Assert.Equal(T(1), h.Playing);
        Assert.Equal(Layer.Navigate, h.State.Layer);
        Assert.Contains(fx, e => e is ShowToast);
    }

    [Fact]
    public void MoveFocusToGroup_JumpsBetweenGroupStarts()
    {
        var h = new Harness();
        h.Do(new EnterNavigate());
        h.Do(new MoveFocusToGroup(+1));
        Assert.Equal(T(4), h.State.Focus!.Track);
        h.Do(new MoveFocusToGroup(+5));
        Assert.Equal(T(6), h.State.Focus!.Track);
        h.Do(new MoveFocusToGroup(-1));
        Assert.Equal(T(4), h.State.Focus!.Track);
    }

    [Fact]
    public void GroupZoom_MovesByGroups()
    {
        var h = new Harness();
        h.Do(new EnterNavigate());
        h.Do(new SetZoom(ZoomLevel.Group));
        h.Do(new MoveFocus(+1));
        Assert.Equal(T(4), h.State.Focus!.Track);
    }

    [Fact]
    public void Search_IsATemporaryBrowseContext()
    {
        var h = new Harness();
        h.Do(new Resume());
        var playing = h.State.Playing;
        var fx = h.Do(new SetQuery("b"));
        Assert.Contains(fx, e => e is RunSearch { Query: "b" });
        Assert.Equal(Layer.Navigate, h.State.Layer);

        h.Do(new SearchResultsArrived("stale", [T(7)]));
        Assert.NotEqual(ScopeKind.Search, h.State.Focus!.BrowseContext.Scope.Kind);

        h.Do(new SearchResultsArrived("b", [T(2), T(4), T(5)]));
        Assert.Equal(ScopeKey.Search("b"), h.State.Focus!.BrowseContext.Scope);
        Assert.Equal(T(2), h.State.Focus.Track);

        h.Do(new ClearQuery());
        Assert.Equal(ScopeKind.All, h.State.Focus!.BrowseContext.Scope.Kind);
        Assert.Equal(T(2), h.State.Focus.Track);
        Assert.Same(playing, h.State.Playing);
    }

    [Fact]
    public void ConfirmFromSearch_SnapshotsSearchResults()
    {
        var h = new Harness();
        h.Do(new SetQuery("q"));
        h.Do(new SearchResultsArrived("q", [T(7), T(2)]));
        h.Do(new ConfirmFocus());
        Assert.Equal(T(7), h.Playing);
        Assert.Equal([7, 2], h.State.Playing!.ContextSnapshot.Order.Select(t => t.Value));
        h.Do(new Next());
        Assert.Equal(T(2), h.Playing);
    }

    [Fact]
    public void EmptySearch_HasNoBrowseLens()
    {
        var h = new Harness();
        h.Do(new SetQuery("zzz"));
        h.Do(new SearchResultsArrived("zzz", ImmutableArray<TrackId>.Empty));
        Assert.Null(h.State.Canvas.BrowseLens);
        h.Do(new ConfirmFocus());
        Assert.Equal(T(1), h.Playing);
    }

    [Fact]
    public void ReturnFocusToPlaying_LeavesSearchIfNeeded()
    {
        var h = new Harness();
        h.Do(new SetQuery("q"));
        h.Do(new SearchResultsArrived("q", [T(7)]));
        h.Do(new ReturnFocusToPlaying());
        Assert.Equal(T(1), h.State.Focus!.Track);
        Assert.Null(h.State.Focus.Query);
    }

    [Fact]
    public void RevealTrack_ShowsFolderWithoutPlaying()
    {
        var h = new Harness();
        h.Do(new RevealTrack(T(5)));
        Assert.Equal(Layer.Navigate, h.State.Layer);
        Assert.Equal(T(5), h.State.Canvas.BrowseLens);
        Assert.Equal([4, 5], h.State.Focus!.BrowseContext.Order.Select(t => t.Value));
        Assert.Equal(T(1), h.Playing);
    }

    private static PlaybackContext Remote(params long[] ids) => new(
        Arrangement.Folder, ScopeKey.Remote("netease:p1"), [.. ids.Select(T)], [new ContextGroup("netease:p1", "我喜欢的音乐", 0, ids.Length)]);

    [Fact]
    public void ShowContext_BrowsesRemoteProjectionWithoutPlaying()
    {
        var h = new Harness();
        h.Do(new ShowContext(Remote(6, 4)));
        Assert.Equal(Layer.Navigate, h.State.Layer);
        Assert.Equal([6, 4], h.State.Focus!.BrowseContext.Order.Select(t => t.Value));
        Assert.Equal(T(6), h.State.Focus.Track);
        Assert.Equal(T(1), h.Playing); // 当前歌曲不变
    }

    [Fact]
    public void ShowContext_ConfirmPlaysWithinRemoteSnapshot_AndStopsAtItsEnd()
    {
        var h = new Harness(settings: Settings.Default with { EndBehavior = EndBehavior.ContinueNextGroup });
        h.Do(new ShowContext(Remote(6, 4)));
        h.Do(new ConfirmFocus());
        Assert.Equal(T(6), h.Playing);
        Assert.Equal(ScopeKind.Remote, h.State.Playing!.ContextSnapshot.Scope.Kind);
        h.Do(new Next());
        Assert.Equal(T(4), h.Playing);
        h.Do(new TrackEnded(T(4)));
        Assert.Equal(T(4), h.Playing); // 远程歌单末尾不会跳到本地的下一个分组
    }

    [Fact]
    public void ShowContext_FocusesPlayingTrack_AndDropsUnknownIds()
    {
        var h = new Harness();
        h.Do(new ShowContext(Remote(99, 4, 1)));
        Assert.Equal([4, 1], h.State.Focus!.BrowseContext.Order.Select(t => t.Value));
        Assert.Equal(T(1), h.State.Focus.Track);
        Assert.Equal(2, h.State.Focus.BrowseContext.Groups[0].Count);
    }

    [Fact]
    public void ShowContext_Empty_IsIgnored()
    {
        var h = new Harness();
        var before = h.State;
        h.Do(new ShowContext(Remote(99)));
        Assert.Same(before, h.State);
    }

    [Fact]
    public void InfoLayer_EntersFromListenAndExits()
    {
        var h = new Harness();
        h.Do(new EnterInfo());
        Assert.Equal(Layer.Info, h.State.Layer);
        h.Do(new MoveFocus(1));
        Assert.Equal(Layer.Info, h.State.Layer);
        h.Do(new ExitLayer());
        Assert.Equal(Layer.Listen, h.State.Layer);
    }

    [Fact]
    public void FirstImport_CuesFirstTrackWithoutPlaying()
    {
        var h = new Harness(LibraryIndex.Empty);
        var fx = h.Do(new LibraryUpdated(Library()));
        Assert.Equal(T(1), h.Playing);
        Assert.True(h.State.Playing!.IsPaused);
        Assert.Contains(fx, e => e is LoadTrack { Paused: true });
    }

    [Fact]
    public void RemovingPlayingTracksFolder_StopsAndCuesRemainingLibrary()
    {
        var h = new Harness();
        h.PlayById(2);
        var remaining = LibraryIndex.From(Library().Tracks.Values.Where(t => t.Groups.Folder != "A"));
        var fx = h.Do(new LibraryUpdated(remaining));
        Assert.Contains(fx, e => e is StopAudio);
        Assert.Equal(T(4), h.Playing);
        Assert.True(h.State.Playing!.IsPaused);

        fx = h.Do(new LibraryUpdated(LibraryIndex.Empty));
        Assert.Null(h.Playing);
        Assert.Null(h.State.Canvas.PlayingAnchor);
    }

    [Fact]
    public void EmptyLibrary_HasNoPlayingOrFocus()
    {
        var h = new Harness(LibraryIndex.Empty);
        h.Do(new EnterNavigate());
        h.Do(new MoveFocus(1));
        h.Do(new TogglePause());
        Assert.Null(h.State.Playing);
        Assert.Null(h.State.Focus);
        Assert.Equal(Layer.Listen, h.State.Layer);
    }
}

public class OnlineTrackTests
{
    private static Track Online(long songId, string title = "在线") =>
        new(new TrackId(-songId), $"netease:song:{songId}", new TrackMeta(title, "A", "", "B", 1, 0, null, TimeSpan.FromSeconds(200)),
            new AudioFormat("online", 0, 0, 2), new GroupKeys("netease:", "网易云音乐", "netease:B", "B"), true);

    private static LibraryIndex WithOnline(params long[] ids) =>
        Fixtures.Library().WithRemote(ids.Select(i => Online(i)).ToImmutableDictionary(t => t.Id));

    [Fact]
    public void OnlineTracks_AreFindable_ButNotInLocalArrangements()
    {
        var lib = WithOnline(100, 200);
        Assert.True(new TrackId(-100).IsRemote);
        Assert.NotNull(lib.Find(new TrackId(-100)));
        Assert.True(lib.IsAvailable(new TrackId(-200)));
        Assert.Equal(7, lib.Count);
        foreach (var a in Enum.GetValues<Arrangement>())
            Assert.DoesNotContain(lib.Arrange(a).Order, t => t.IsRemote);
        Assert.Equal(2, lib.WithUserData(Fixtures.T(1), UserTrackData.Empty).Remote.Count);
    }

    private static PlaybackContext Mixed() => new(Arrangement.Folder, ScopeKey.Remote("netease:p"),
        [new TrackId(-100), Fixtures.T(4), new TrackId(-200)], [new ContextGroup("netease:p", "歌单", 0, 3)]);

    [Fact]
    public void MixedPlaylist_PlaysOnlineAndLocalInOrder()
    {
        var h = new Harness(WithOnline(100, 200));
        h.Do(new ShowContext(Mixed()));
        h.Do(new ConfirmFocus());
        Assert.Equal(new TrackId(-100), h.Playing);
        Assert.Contains(h.Effects, e => e is LoadTrack l && l.Track == new TrackId(-100));
        h.Do(new Next());
        Assert.Equal(Fixtures.T(4), h.Playing);
        h.Do(new Next());
        Assert.Equal(new TrackId(-200), h.Playing);
    }

    [Fact]
    public void OnlineTrack_IsNeverPersistedToLibraryData()
    {
        var h = new Harness(WithOnline(100));
        h.Do(new ShowContext(Mixed() with { Order = [new TrackId(-100)], Groups = [new ContextGroup("netease:p", "歌单", 0, 1)] }));
        h.Do(new ConfirmFocus());
        h.Effects.Clear();
        h.Do(new ToggleFavorite(new TrackId(-100)));
        h.Do(new AddMarker(TimeSpan.FromSeconds(3)));
        h.Do(new SetTags(new TrackId(-100), ["x"]));
        h.Do(new SetNote(new TrackId(-100), "n"));
        h.Do(new PositionChanged(TimeSpan.FromSeconds(150)));
        Assert.DoesNotContain(h.Effects, e => e is SaveFavorite or SaveMarker or SaveTags or SaveNote or RecordPlay);
        Assert.Equal(2, h.Effects.OfType<ShowToast>().Count());
        Assert.False(h.State.Library.User(new TrackId(-100)).Favorite);
    }

    [Fact]
    public void LoggingOut_RemovesOnlineTracks_StopsOnlinePlayback()
    {
        var h = new Harness(WithOnline(100));
        h.Do(new ShowContext(Mixed() with { Order = [new TrackId(-100)], Groups = [new ContextGroup("netease:p", "歌单", 0, 1)] }));
        h.Do(new ConfirmFocus());
        h.Do(new LibraryUpdated(Fixtures.Library()));
        Assert.NotEqual(new TrackId(-100), h.Playing);
        Assert.Contains(h.Effects, e => e is StopAudio);
    }
}

public class PlaylistsArrangementTests
{
    private static Track Online(long songId) =>
        new(new TrackId(-songId), $"netease:song:{songId}", new TrackMeta($"在线{songId}", "A", "", "B", 1, 0, null, TimeSpan.FromSeconds(200)),
            new AudioFormat("online", 0, 0, 2), new GroupKeys("netease:", "网易云音乐", "netease:B", "B"), true);

    /// <summary>两个歌单："我喜欢"（在线 100、本地 4）和"通勤"（本地 6、在线 200）。</summary>
    private static LibraryIndex Library() => Fixtures.Library().WithRemote(
        new[] { Online(100), Online(200) }.ToImmutableDictionary(t => t.Id),
        new PlaybackContext(Arrangement.Playlists, ScopeKey.All, [new TrackId(-100), T(4), T(6), new TrackId(-200)],
            [new ContextGroup("NetEase:1", "我喜欢", 0, 2), new ContextGroup("NetEase:2", "通勤", 2, 2)]));

    [Fact]
    public void PlaylistsArrangement_ComesFromHost_WithOneGroupPerPlaylist()
    {
        var h = new Harness(Library());
        h.Do(new EnterNavigate());
        h.Do(new SetArrangement(Arrangement.Playlists));
        var ctx = h.State.Focus!.BrowseContext;
        Assert.Equal(Arrangement.Playlists, ctx.Arrangement);
        Assert.Equal(["我喜欢", "通勤"], ctx.Groups.Select(g => g.DisplayName));
    }

    [Fact]
    public void ConfirmInPlaylist_PlaysThatPlaylist_ThenContinuesToNextPlaylist()
    {
        var h = new Harness(Library(), settings: Settings.Default with { EndBehavior = EndBehavior.ContinueNextGroup });
        h.Do(new SetArrangement(Arrangement.Playlists));
        h.Do(new ShowContext(h.State.Library.Arrange(Arrangement.Playlists), 0));
        h.Do(new ConfirmFocus());
        Assert.Equal(new TrackId(-100), h.Playing);
        Assert.Equal(ScopeKey.Group("NetEase:1"), h.State.Playing!.ContextSnapshot.Scope);
        h.Do(new TrackEnded(new TrackId(-100)));
        Assert.Equal(T(4), h.Playing);
        h.Do(new TrackEnded(T(4)));
        Assert.Equal(T(6), h.Playing); // 下一个歌单
    }

    [Fact]
    public void ShowContext_FocusIndex_SelectsPlaylist_AndUnknownIdsAreRegrouped()
    {
        var h = new Harness(Library());
        var ctx = new PlaybackContext(Arrangement.Playlists, ScopeKey.All, [new TrackId(-999), T(4), T(6), new TrackId(-200)],
            [new ContextGroup("NetEase:1", "我喜欢", 0, 2), new ContextGroup("NetEase:2", "通勤", 2, 2)]);
        h.Do(new ShowContext(ctx, 2));
        var f = h.State.Focus!;
        Assert.Equal([4, 6, -200], f.BrowseContext.Order.Select(t => t.Value));
        Assert.Equal([(0, 1), (1, 2)], f.BrowseContext.Groups.Select(g => (g.Start, g.Count)));
        Assert.Equal(T(6), f.Track);
    }

    [Fact]
    public void EmptyPlaylists_FallBackToFolders()
    {
        var h = new Harness();
        h.Do(new SetArrangement(Arrangement.Playlists));
        h.Do(new EnterNavigate());
        Assert.Equal(Layer.Navigate, h.State.Layer);
        Assert.Equal(Arrangement.Folder, h.State.Arrangement);
    }
}
