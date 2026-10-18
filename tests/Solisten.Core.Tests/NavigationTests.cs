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
