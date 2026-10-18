using Solisten.Core.Model;
using static Solisten.Core.Tests.Fixtures;

namespace Solisten.Core.Tests;

public class PlaybackRuleTests
{
    [Fact]
    public void LibraryLoaded_WithoutSession_CuesFirstTrackPausedInItsGroup()
    {
        var h = new Harness();
        Assert.Equal(T(1), h.Playing);
        Assert.True(h.State.Playing!.IsPaused);
        Assert.Equal(ScopeKey.Group("A"), h.State.Playing.ContextSnapshot.Scope);
        Assert.Contains(h.Effects, e => e is LoadTrack { Paused: true } l && l.Track == T(1));
        Assert.Contains(h.Effects, e => e is SetSuccessor s && s.Track == T(2));
    }

    [Fact]
    public void LibraryLoaded_RestoresSessionWithoutAutoplay()
    {
        var first = new Harness();
        first.PlayById(5);
        first.Do(new PositionChanged(TimeSpan.FromSeconds(42)));
        var session = Reducer.ToSession(first.State);

        var h = new Harness(session: session);
        Assert.Equal(T(5), h.Playing);
        Assert.True(h.State.Playing!.IsPaused);
        Assert.Equal(TimeSpan.FromSeconds(42), h.State.Playing.Position);
        Assert.Contains(h.Effects, e => e is LoadTrack { Paused: true } l && l.StartAt == TimeSpan.FromSeconds(42));
    }

    [Fact]
    public void Confirm_SnapshotsTheFocusedGroup()
    {
        var h = new Harness();
        h.PlayById(4);
        var p = h.State.Playing!;
        Assert.Equal(T(4), p.Track);
        Assert.False(p.IsPaused);
        Assert.Equal([4, 5], p.ContextSnapshot.Order.Select(t => t.Value));
        Assert.Equal(0, p.IndexInContext);
    }

    [Fact]
    public void Snapshot_IsStableWhenArrangementChanges() // I7
    {
        var h = new Harness();
        h.PlayById(4);
        var snap = h.State.Playing!.ContextSnapshot;
        h.Do(new EnterNavigate());
        h.Do(new SetArrangement(Arrangement.Album));
        h.Do(new SetArrangement(Arrangement.Listening));
        h.Do(new CancelNavigate());
        Assert.Same(snap, h.State.Playing!.ContextSnapshot);
        h.Do(new Next());
        Assert.Equal(T(5), h.Playing);
    }

    [Fact]
    public void Next_StopsAtEndOfSnapshotByDefault()
    {
        var h = new Harness();
        h.PlayById(5);
        h.Do(new Next());
        Assert.Equal(T(5), h.Playing);

        h.Do(new TrackEnded(T(5)));
        Assert.Equal(T(5), h.Playing);
        Assert.True(h.State.Playing!.IsPaused);
        Assert.Equal(Duration, h.State.Playing.Position);

        // 停在末端后再次播放：从本曲开头重新开始。
        var fx = h.Do(new Resume());
        Assert.Contains(fx, e => e is LoadTrack { Paused: false } l && l.StartAt == TimeSpan.Zero);
    }

    [Fact]
    public void ContinueNextGroup_CreatesNewSnapshotFromNextGroup()
    {
        var h = new Harness(settings: Settings.Default with { EndBehavior = EndBehavior.ContinueNextGroup });
        h.PlayById(5);
        Assert.Contains(h.Effects, e => e is SetSuccessor s && s.Track == T(6));
        h.Do(new TrackBoundaryReached(T(6)));
        Assert.Equal(T(6), h.Playing);
        Assert.Equal(ScopeKey.Group("C"), h.State.Playing!.ContextSnapshot.Scope);
    }

    [Fact]
    public void Previous_RestartsWhenPastThreeSeconds_OtherwiseGoesBack()
    {
        var h = new Harness();
        h.PlayById(2);
        h.Do(new PositionChanged(TimeSpan.FromSeconds(1)));
        h.Do(new PositionChanged(TimeSpan.FromSeconds(2.5)));
        h.Do(new PositionChanged(TimeSpan.FromSeconds(4)));
        var fx = h.Do(new Previous());
        Assert.Equal(T(2), h.Playing);
        Assert.Contains(fx, e => e is SeekAudio { Position.Ticks: 0 });

        h.Do(new Previous());
        Assert.Equal(T(1), h.Playing);

        h.Do(new Previous());
        Assert.Equal(T(1), h.Playing);
    }

    [Fact]
    public void Next_SkipsUnavailableTracks()
    {
        var h = new Harness(Library(unavailable: 2));
        Assert.Contains(h.Effects, e => e is SetSuccessor s && s.Track == T(3));
        h.Do(new Resume());
        h.Do(new Next());
        Assert.Equal(T(3), h.Playing);
        h.Do(new Previous());
        Assert.Equal(T(1), h.Playing);
    }

    [Fact]
    public void RepeatOne_UsesSameTrackAsSuccessor()
    {
        var h = new Harness();
        h.PlayById(2);
        var fx = h.Do(new ToggleRepeatOne());
        Assert.Contains(fx, e => e is SetSuccessor s && s.Track == T(2));
        h.Do(new TrackBoundaryReached(T(2)));
        Assert.Equal(T(2), h.Playing);
        Assert.Equal(TimeSpan.Zero, h.State.Playing!.Position);

        fx = h.Do(new TrackEnded(T(2)));
        Assert.Contains(fx, e => e is LoadTrack l && l.Track == T(2));

        // 用户手动下一首仍然可以离开单曲循环的歌。
        h.Do(new Next());
        Assert.Equal(T(3), h.Playing);
    }

    [Fact]
    public void Boundary_AdvancesSnapshotIndexAndPlanSuccessor()
    {
        var h = new Harness();
        h.PlayById(1);
        var fx = h.Do(new TrackBoundaryReached(T(2)));
        Assert.Equal(T(2), h.Playing);
        Assert.Equal(1, h.State.Playing!.IndexInContext);
        Assert.Contains(fx, e => e is SetSuccessor s && s.Track == T(3));
        Assert.DoesNotContain(fx, e => e is LoadTrack);
    }

    [Fact]
    public void PlayCount_RecordedOnceAfterHalfTheTrack()
    {
        var h = new Harness();
        h.PlayById(1);
        var recorded = 0;
        for (int sec = 1; sec <= 200; sec++)
        {
            recorded += h.Do(new PositionChanged(TimeSpan.FromSeconds(sec))).Count(e => e is RecordPlay);
            if (sec == 100) Assert.Equal(0, recorded);
        }
        Assert.Equal(1, recorded);
    }

    [Fact]
    public void PlayCount_SeekingDoesNotCountAsListening()
    {
        var h = new Harness();
        h.PlayById(1);
        h.Do(new Seek(TimeSpan.FromSeconds(150)));
        var fx = h.Do(new PositionChanged(TimeSpan.FromSeconds(150.2)));
        Assert.DoesNotContain(fx, e => e is RecordPlay);
    }

    [Fact]
    public void PlayCount_LongTracksCountAfterFourMinutes()
    {
        var lib = LibraryIndex.From([MakeTrack(1, "A", "long.flac", duration: TimeSpan.FromMinutes(20))]);
        var h = new Harness(lib);
        h.Do(new Resume());
        int recorded = 0;
        for (int sec = 1; sec <= 241; sec++)
            recorded += h.Do(new PositionChanged(TimeSpan.FromSeconds(sec))).Count(e => e is RecordPlay);
        Assert.Equal(1, recorded);
    }

    [Fact]
    public void ThreeConsecutiveFailures_StopPlayback()
    {
        var h = new Harness();
        h.PlayById(1);
        h.Do(new PlaybackFailed(T(1), "bad"));
        Assert.Equal(T(2), h.Playing);
        h.Do(new PlaybackFailed(T(2), "bad"));
        Assert.Equal(T(3), h.Playing);
        var fx = h.Do(new PlaybackFailed(T(3), "bad"));
        Assert.Equal(T(3), h.Playing);
        Assert.True(h.State.Playing!.IsPaused);
        Assert.Contains(fx, e => e is StopAudio);
    }

    [Fact]
    public void Failures_ResetAfterSuccessfulPlayback()
    {
        var h = new Harness();
        h.PlayById(1);
        h.Do(new PlaybackFailed(T(1), "bad"));
        h.Do(new PositionChanged(TimeSpan.FromSeconds(1)));
        Assert.Equal(0, h.State.ConsecutiveFailures);
    }

    [Fact]
    public void Shuffle_KeepsCurrentFirstAndPermutesTheRest()
    {
        var h = new Harness();
        h.PlayById(2);
        h.Do(new ShuffleSnapshot(1234));
        var p = h.State.Playing!;
        Assert.Equal(T(2), p.ContextSnapshot.Order[0]);
        Assert.Equal(0, p.IndexInContext);
        Assert.Equal([1, 2, 3], p.ContextSnapshot.Order.Select(t => t.Value).Order());
    }

    [Fact]
    public void Seek_IsClampedToTrackDuration()
    {
        var h = new Harness();
        h.Do(new Seek(TimeSpan.FromHours(1)));
        Assert.Equal(Duration, h.State.Playing!.Position);
        h.Do(new SeekBy(TimeSpan.FromHours(-2)));
        Assert.Equal(TimeSpan.Zero, h.State.Playing!.Position);
    }

    [Fact]
    public void Markers_RenameAndDeleteOnlyEmitEffects()
    {
        var h = new Harness();
        var before = h.State;
        var fx = h.Do(new RenameMarker(T(1), 7, "  副歌  "));
        Assert.Equal(new SaveMarkerLabel(T(1), 7, "副歌"), Assert.Single(fx));
        Assert.Equal(new SaveMarkerLabel(T(1), 7, null), Assert.Single(h.Do(new RenameMarker(T(1), 7, " "))));
        Assert.Equal(new RemoveMarker(T(1), 7), Assert.Single(h.Do(new DeleteMarker(T(1), 7))));
        Assert.Same(before, h.State);
    }

    [Fact]
    public void DeviceLost_Pauses()
    {
        var h = new Harness();
        h.Do(new Resume());
        var fx = h.Do(new OutputDeviceLost());
        Assert.True(h.State.Playing!.IsPaused);
        Assert.Contains(fx, e => e is PauseAudio);
    }
}
