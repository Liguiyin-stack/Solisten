using System.Collections.Immutable;
using Solisten.Core.Model;
using static Solisten.Core.Tests.Fixtures;

namespace Solisten.Core.Tests;

/// <summary>§11：随机生成 Action 序列，每一步之后断言 I1–I7。</summary>
public class InvariantPropertyTests
{
    /// <summary>这些动作只属于浏览/画布，绝不能修改 Playing（I3、I5）。</summary>
    private static bool IsBrowseOnly(AppAction a) => a is EnterNavigate or MoveFocus or MoveFocusToGroup or CancelNavigate
        or ReturnFocusToPlaying or SetZoom or SetArrangement or SetQuery or ClearQuery or SearchResultsArrived or RevealTrack
        or EnterInfo or ExitLayer or RevealControls or HideControls or SetListeningViewport;

    /// <summary>允许替换正在播放曲目的动作（I4：浏览层中只有 ConfirmFocus / PlayTrack）。</summary>
    private static bool MayChangeTrack(AppAction a) => a is PlayTrack or ConfirmFocus or Next or Previous
        or TrackBoundaryReached or TrackEnded or PlaybackFailed or LibraryLoaded;

    private static bool MayChangeSnapshot(AppAction a) => MayChangeTrack(a) || a is ShuffleSnapshot;

    private static AppAction RandomAction(Random rng, AppState s)
    {
        var ids = s.Library.Tracks.Keys.ToArray();
        TrackId AnyId() => ids.Length == 0 ? T(99) : ids[rng.Next(ids.Length)];
        var current = s.Playing?.Track ?? T(99);

        return rng.Next(31) switch
        {
            0 => new TogglePause(),
            1 => new Seek(TimeSpan.FromSeconds(rng.Next(-10, 250))),
            2 => new SeekBy(TimeSpan.FromSeconds(rng.Next(-30, 30))),
            3 => new Next(),
            4 => new Previous(),
            5 => new ToggleRepeatOne(),
            6 => new ShuffleSnapshot(rng.Next()),
            7 => new TrackBoundaryReached(rng.Next(3) == 0 ? AnyId() : Reducer.PlanNext(s, true) is { } p ? p.Context.Order[p.Index] : AnyId()),
            8 => new TrackEnded(current),
            9 => new PositionChanged((s.Playing?.Position ?? TimeSpan.Zero) + TimeSpan.FromMilliseconds(rng.Next(0, 1500))),
            10 => new PlaybackFailed(current, "x"),
            11 or 12 or 13 => new MoveFocus(rng.Next(-4, 5)),
            14 => new MoveFocusToGroup(rng.Next(-2, 3)),
            15 or 16 => new ConfirmFocus(),
            17 => new CancelNavigate(),
            18 => new ReturnFocusToPlaying(),
            19 => new SetZoom(rng.Next(2) == 0 ? ZoomLevel.Track : ZoomLevel.Group),
            20 => new SetArrangement((Arrangement)rng.Next(3)),
            21 => new SetQuery(rng.Next(3) == 0 ? "" : "q"),
            22 => new SearchResultsArrived("q", [.. Enumerable.Range(0, rng.Next(4)).Select(_ => AnyId())]),
            23 => new ClearQuery(),
            24 => new RevealTrack(AnyId()),
            25 => rng.Next(2) == 0 ? new EnterInfo() : new ExitLayer(),
            26 => new SetListeningViewport(new ListeningViewport(TimeSpan.FromSeconds(rng.Next(200)), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10), rng.Next(3))),
            27 => rng.Next(2) == 0 ? new RevealControls() : new HideControls(),
            28 => new EnterNavigate(),
            29 => rng.Next(2) == 0 ? new RenameMarker(current, rng.Next(5), "x") : new DeleteMarker(current, rng.Next(5)),
            _ => new ToggleFavorite(AnyId()),
        };
    }

    [Theory]
    [MemberData(nameof(Seeds))]
    public void RandomActionSequences_PreserveInvariants(int seed)
    {
        var rng = new Random(seed);
        var lib = Library(unavailable: rng.Next(4) == 0 ? [3] : []);
        var settings = Settings.Default with { EndBehavior = rng.Next(2) == 0 ? EndBehavior.Stop : EndBehavior.ContinueNextGroup };
        var state = Reducer.Reduce(AppState.Initial(settings), new LibraryLoaded(lib, null)).State;

        for (int step = 0; step < 400; step++)
        {
            var action = RandomAction(rng, state);
            var before = state;
            state = Reducer.Reduce(state, action).State;
            var ctx = $"seed={seed} step={step} action={action}";

            // I1
            Assert.True(state.Canvas.PlayingAnchor == state.Playing?.Track, $"I1 {ctx}");
            if (state.Playing is not null)
            {
                var p = state.Playing;
                Assert.True(p.IndexInContext >= 0 && p.IndexInContext < p.ContextSnapshot.Count, $"index range {ctx}");
                Assert.True(p.ContextSnapshot.Order[p.IndexInContext] == p.Track, $"index matches track {ctx}");
            }

            // I2 / I6
            if (state.Layer != Layer.Navigate)
            {
                Assert.True(state.Canvas.BrowseLens is null, $"I2 lens {ctx}");
                Assert.True(state.Focus?.Track == state.Playing?.Track, $"I2 focus {ctx}");
            }
            else
            {
                var f = state.Focus!;
                Assert.True(f.Index == -1 || f.BrowseContext.Order[f.Index] == f.Track, $"focus index {ctx}");
                Assert.True(state.Canvas.BrowseLens == (f.Index >= 0 ? f.Track : null), $"lens mirrors focus {ctx}");
            }

            // I3 / I5
            if (IsBrowseOnly(action))
                Assert.True(ReferenceEquals(before.Playing, state.Playing), $"I3 {ctx}");

            // I4
            if (!MayChangeTrack(action))
                Assert.True(before.Playing?.Track == state.Playing?.Track, $"I4 {ctx}");

            // I7
            if (!MayChangeSnapshot(action) && action is not ConfirmFocus)
                Assert.True(ReferenceEquals(before.Playing?.ContextSnapshot, state.Playing?.ContextSnapshot), $"I7 {ctx}");
        }
    }

    public static TheoryData<int> Seeds()
    {
        var data = new TheoryData<int>();
        for (int i = 0; i < 300; i++) data.Add(i);
        return data;
    }

    [Fact]
    public void Store_SerializesReentrantDispatch()
    {
        var store = new PlayerStore();
        var handler = new SearchEcho();
        store.AddEffectHandler(handler);
        store.Dispatch(new LibraryLoaded(Library(), null));
        store.Dispatch(new SetQuery("q"));
        Assert.Equal(ScopeKey.Search("q"), store.State.Focus!.BrowseContext.Scope);
        Assert.Equal(T(5), store.State.Focus.Track);
    }

    private sealed class SearchEcho : IEffectHandler
    {
        public void Handle(Effect effect, PlayerStore store)
        {
            if (effect is RunSearch r) store.Dispatch(new SearchResultsArrived(r.Query, ImmutableArray.Create(T(5))));
        }
    }
}
