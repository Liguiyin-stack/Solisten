using System.Collections.Immutable;
using Solisten.Core.Model;

namespace Solisten.Core;

public readonly record struct ReduceResult(AppState State, ImmutableArray<Effect> Effects);

/// <summary>纯函数 Reduce(state, action) → (state, effects)。§2.2 的不变量在 <see cref="Normalize"/> 中强制执行。</summary>
public static class Reducer
{
    public static readonly TimeSpan PreviousRestartThreshold = TimeSpan.FromSeconds(3);
    public static readonly TimeSpan PlayCountAbsolute = TimeSpan.FromMinutes(4);
    public const int MaxConsecutiveFailures = 3;
    private static readonly TimeSpan MaxTickDelta = TimeSpan.FromSeconds(2);

    public static ReduceResult Reduce(AppState state, AppAction action)
    {
        var fx = ImmutableArray.CreateBuilder<Effect>();
        var next = Apply(state, action, fx);
        return new ReduceResult(Normalize(next), fx.ToImmutable());
    }

    private static AppState Apply(AppState s, AppAction action, ImmutableArray<Effect>.Builder fx) => action switch
    {
        PlayTrack a => PlayTrackIn(s, a.Context, a.Context.IndexOf(a.Track), a.StartAt, fx),
        TogglePause => s.Playing is { IsPaused: true } ? DoResume(s, fx) : DoPause(s, fx),
        Pause => DoPause(s, fx),
        Resume => DoResume(s, fx),
        Seek a => DoSeek(s, a.Position, fx),
        SeekBy a => s.Playing is null ? s : DoSeek(s, s.Playing.Position + a.Delta, fx),
        Next => DoNext(s, fx),
        Previous => DoPrevious(s, fx),
        ToggleRepeatOne => DoToggleRepeat(s, fx),
        ShuffleSnapshot a => DoShuffle(s, a.Seed, fx),

        TrackBoundaryReached a => DoBoundary(s, a.NewTrack, fx),
        TrackEnded a => DoTrackEnded(s, a.Track, fx),
        PositionChanged a => DoPosition(s, a.Position, fx),
        PlaybackFailed a => DoFailed(s, a.Track, a.Reason, fx),
        OutputDeviceLost => DoDeviceLost(s, fx),

        EnterNavigate => DoEnterNavigate(s),
        MoveFocus a => DoMoveFocus(s, a.Delta, fx),
        MoveFocusToGroup a => DoMoveFocusToGroup(s, a.Delta, fx),
        ConfirmFocus => DoConfirm(s, fx),
        CancelNavigate => s.Layer == Layer.Navigate ? s with { Layer = Layer.Listen } : s,
        ReturnFocusToPlaying => DoReturnFocus(s),
        SetZoom a => s.Layer == Layer.Navigate && s.Focus is not null ? s with { Focus = s.Focus with { Zoom = a.Level } } : s,
        SetArrangement a => DoSetArrangement(s, a.Arrangement),
        SetQuery a => DoSetQuery(s, a.Text, fx),
        ClearQuery => DoClearQuery(s),
        SearchResultsArrived a => DoSearchResults(s, a.Query, a.Results),
        RevealTrack a => DoRevealTrack(s, a.Track),
        ShowContext a => DoShowContext(s, a.Context, a.FocusIndex),

        EnterInfo => s.Layer == Layer.Listen && s.Playing is not null ? s with { Layer = Layer.Info } : s,
        ExitLayer => s.Layer == Layer.Listen ? s : s with { Layer = Layer.Listen },

        RevealControls => s with { Canvas = s.Canvas with { ControlsRevealed = true } },
        HideControls => s with { Canvas = s.Canvas with { ControlsRevealed = false } },
        SetListeningViewport a => s with { Canvas = s.Canvas with { Viewport = a.Viewport } },

        AddMarker a => DoAddMarker(s, a.Position, a.Label, fx),
        RenameMarker a => Emit(s, fx, new SaveMarkerLabel(a.Track, a.MarkerId, string.IsNullOrWhiteSpace(a.Label) ? null : a.Label.Trim())),
        DeleteMarker a => Emit(s, fx, new RemoveMarker(a.Track, a.MarkerId)),
        SetTags a => a.Track.IsRemote ? s : Emit(s, fx, new SaveTags(a.Track, a.Tags)),
        SetNote a => a.Track.IsRemote ? s : Emit(s, fx, new SaveNote(a.Track, a.Note)),
        ToggleFavorite a => DoToggleFavorite(s, a.Track, fx),

        LibraryLoaded a => DoLibraryLoaded(s, a.Library, a.Session, fx),
        // 空曲库首次导入后（或正在播放的歌已不存在时）像启动一样准备好第一首，但不自动播放。
        LibraryUpdated a => DoLibraryUpdated(s, a.Library, fx),
        SetEndBehavior a => WithSuccessor(s with { Settings = s.Settings with { EndBehavior = a.Behavior } }, fx),
        SetListeningWindow a => s with
        {
            Settings = s.Settings with { ListeningWindowHalfWidth = a.HalfWidth },
            Canvas = s.Canvas with { Viewport = s.Canvas.Viewport with { VisibleBefore = a.HalfWidth, VisibleAfter = a.HalfWidth } },
        },

        _ => throw new ArgumentOutOfRangeException(nameof(action), action, "Unknown action"),
    };

    // ───────────────────────── 不变量 ─────────────────────────

    /// <summary>I1：播放锚点等于正在播放的歌。I2：聆听/信息层时焦点回到正在播放的歌，Browse Lens 为空。</summary>
    internal static AppState Normalize(AppState s)
    {
        var canvas = s.Canvas;
        if (canvas.PlayingAnchor != s.Playing?.Track) canvas = canvas with { PlayingAnchor = s.Playing?.Track };

        if (s.Layer == Layer.Navigate && s.Focus is not null)
        {
            TrackId? lens = s.Focus.Index >= 0 ? s.Focus.Track : null;
            if (canvas.BrowseLens != lens) canvas = canvas with { BrowseLens = lens };
            return canvas == s.Canvas ? s : s with { Canvas = canvas };
        }

        if (s.Layer == Layer.Navigate) s = s with { Layer = Layer.Listen };
        if (canvas.BrowseLens is not null) canvas = canvas with { BrowseLens = null };

        var focus = s.Focus;
        bool focusOk = s.Playing is null
            ? focus is null
            : focus is not null && focus.Track == s.Playing.Track && focus.Query is null && focus.Zoom == ZoomLevel.Track
              && focus.BrowseContext.Arrangement == s.Arrangement;
        if (!focusOk) focus = FocusOnPlaying(s);

        return canvas == s.Canvas && ReferenceEquals(focus, s.Focus) ? s : s with { Canvas = canvas, Focus = focus };
    }

    internal static FocusState? FocusOnPlaying(AppState s)
    {
        if (s.Playing is null) return null;
        var ctx = s.Library.Arrange(s.Arrangement);
        int idx = ctx.IndexOf(s.Playing.Track);
        if (idx < 0)
        {
            ctx = s.Playing.ContextSnapshot;
            idx = s.Playing.IndexInContext;
        }
        return new FocusState(s.Playing.Track, idx, ctx, ZoomLevel.Track, null);
    }

    // ───────────────────────── 播放 ─────────────────────────

    private static AppState PlayTrackIn(AppState s, PlaybackContext ctx, int index, TimeSpan startAt, ImmutableArray<Effect>.Builder fx, bool paused = false, RepeatMode? repeat = null)
    {
        if (index < 0 || index >= ctx.Count) return s;
        var id = ctx.Order[index];
        if (!s.Library.IsAvailable(id))
        {
            fx.Add(new ShowToast("文件不可用"));
            return s;
        }

        var playing = new PlayingState(id, ctx, index, Clamp(s, id, startAt), paused, repeat ?? s.Playing?.Repeat ?? RepeatMode.Off);
        s = s with { Playing = playing };
        fx.Add(new LoadTrack(id, playing.Position, paused));
        fx.Add(new PrioritizeAnalysis(id));
        s = WithSuccessor(s, fx);
        fx.Add(new PersistSession(ToSession(s)));
        return s;
    }

    private static AppState DoPause(AppState s, ImmutableArray<Effect>.Builder fx)
    {
        if (s.Playing is null || s.Playing.IsPaused) return s;
        s = s with { Playing = s.Playing with { IsPaused = true } };
        fx.Add(new PauseAudio());
        fx.Add(new PersistSession(ToSession(s)));
        return s;
    }

    private static AppState DoResume(AppState s, ImmutableArray<Effect>.Builder fx)
    {
        var p = s.Playing;
        if (p is null || !p.IsPaused) return s;
        if (!s.Library.IsAvailable(p.Track))
        {
            fx.Add(new ShowToast("文件不可用"));
            return s;
        }
        if (IsAtEnd(s, p))
        {
            // 停在快照末尾后再次播放：从本曲开头重新开始。
            return PlayTrackIn(s, p.ContextSnapshot, p.IndexInContext, TimeSpan.Zero, fx);
        }
        s = s with { Playing = p with { IsPaused = false } };
        fx.Add(new ResumeAudio());
        return s;
    }

    private static AppState DoSeek(AppState s, TimeSpan position, ImmutableArray<Effect>.Builder fx)
    {
        if (s.Playing is null) return s;
        var pos = Clamp(s, s.Playing.Track, position);
        if (IsAtEnd(s, s.Playing) && s.Playing.IsPaused)
        {
            // 引擎已停止，需要重新加载。
            s = s with { Playing = s.Playing with { Position = pos } };
            fx.Add(new LoadTrack(s.Playing.Track, pos, true));
            return s;
        }
        s = s with { Playing = s.Playing with { Position = pos } };
        fx.Add(new SeekAudio(pos));
        return s;
    }

    private static AppState DoNext(AppState s, ImmutableArray<Effect>.Builder fx)
    {
        if (s.Playing is null) return s;
        var plan = PlanNext(s, respectRepeat: false);
        if (plan is null) return s;
        return PlayTrackIn(s, plan.Value.Context, plan.Value.Index, TimeSpan.Zero, fx, paused: s.Playing.IsPaused && !IsAtEnd(s, s.Playing));
    }

    private static AppState DoPrevious(AppState s, ImmutableArray<Effect>.Builder fx)
    {
        var p = s.Playing;
        if (p is null) return s;
        if (p.Position > PreviousRestartThreshold) return DoSeek(s, TimeSpan.Zero, fx);

        for (int i = p.IndexInContext - 1; i >= 0; i--)
        {
            if (s.Library.IsAvailable(p.ContextSnapshot.Order[i]))
                return PlayTrackIn(s, p.ContextSnapshot, i, TimeSpan.Zero, fx, paused: p.IsPaused && !IsAtEnd(s, p));
        }
        return DoSeek(s, TimeSpan.Zero, fx);
    }

    private static AppState DoToggleRepeat(AppState s, ImmutableArray<Effect>.Builder fx)
    {
        if (s.Playing is null) return s;
        var repeat = s.Playing.Repeat == RepeatMode.One ? RepeatMode.Off : RepeatMode.One;
        s = WithSuccessor(s with { Playing = s.Playing with { Repeat = repeat } }, fx);
        fx.Add(new PersistSession(ToSession(s)));
        return s;
    }

    /// <summary>§3.2 范围内随机：对快照做一次 Fisher–Yates，当前曲目移到最前，存为新快照。</summary>
    private static AppState DoShuffle(AppState s, int seed, ImmutableArray<Effect>.Builder fx)
    {
        var p = s.Playing;
        if (p is null || p.ContextSnapshot.Count < 2) return s;

        var order = p.ContextSnapshot.Order.ToBuilder();
        order.RemoveAt(p.IndexInContext);
        var rng = new Random(seed);
        for (int i = order.Count - 1; i > 0; i--)
        {
            int j = rng.Next(i + 1);
            (order[i], order[j]) = (order[j], order[i]);
        }
        order.Insert(0, p.Track);

        var groupName = p.ContextSnapshot.Groups.Length == 1 ? p.ContextSnapshot.Groups[0].DisplayName : "随机";
        var ctx = p.ContextSnapshot with
        {
            Order = order.ToImmutable(),
            Groups = [new ContextGroup(p.ContextSnapshot.Scope.Key, groupName, 0, order.Count)],
        };
        s = WithSuccessor(s with { Playing = p with { ContextSnapshot = ctx, IndexInContext = 0 } }, fx);
        fx.Add(new PersistSession(ToSession(s)));
        return s;
    }

    private static AppState DoBoundary(AppState s, TrackId newTrack, ImmutableArray<Effect>.Builder fx)
    {
        var p = s.Playing;
        if (p is null) return s;

        var plan = PlanNext(s, respectRepeat: true);
        PlaybackContext ctx;
        int index;
        if (plan is { } pl && pl.Context.Order[pl.Index] == newTrack)
        {
            (ctx, index) = (pl.Context, pl.Index);
        }
        else
        {
            // 引擎与 Store 的后继计划不一致（例如切歌命令与边界在同一时刻）：以引擎实际播放的曲目为准。
            ctx = p.ContextSnapshot;
            index = ctx.IndexOf(newTrack, p.IndexInContext + 1);
            if (index < 0)
            {
                ctx = s.Library.Arrange(s.Arrangement);
                index = ctx.IndexOf(newTrack);
                if (index < 0) return s;
            }
        }

        s = s with
        {
            Playing = new PlayingState(newTrack, ctx, index, TimeSpan.Zero, false, p.Repeat),
            ConsecutiveFailures = 0,
        };
        fx.Add(new PrioritizeAnalysis(newTrack));
        s = WithSuccessor(s, fx);
        fx.Add(new PersistSession(ToSession(s)));
        return s;
    }

    private static AppState DoTrackEnded(AppState s, TrackId track, ImmutableArray<Effect>.Builder fx)
    {
        var p = s.Playing;
        if (p is null || p.Track != track) return s;

        if (p.Repeat == RepeatMode.One)
            return PlayTrackIn(s, p.ContextSnapshot, p.IndexInContext, TimeSpan.Zero, fx);

        var plan = PlanNext(s, respectRepeat: false);
        if (plan is { } pl) return PlayTrackIn(s, pl.Context, pl.Index, TimeSpan.Zero, fx);

        // EndBehavior.Stop：停在最后一首，光标停在末端。
        var duration = s.Library.Find(p.Track)?.Meta.Duration ?? p.Position;
        s = s with { Playing = p with { IsPaused = true, Position = duration } };
        fx.Add(new PersistSession(ToSession(s)));
        return s;
    }

    private static AppState DoPosition(AppState s, TimeSpan position, ImmutableArray<Effect>.Builder fx)
    {
        var p = s.Playing;
        if (p is null) return s;

        var delta = position - p.Position;
        var listened = p.Listened;
        if (!p.IsPaused && delta > TimeSpan.Zero && delta <= MaxTickDelta) listened += delta;

        bool counted = p.PlayCounted;
        if (!counted)
        {
            var duration = s.Library.Find(p.Track)?.Meta.Duration ?? TimeSpan.MaxValue;
            if (listened > PlayCountAbsolute || (duration > TimeSpan.Zero && listened.Ticks * 2 > duration.Ticks))
            {
                counted = true;
                if (!p.Track.IsRemote) fx.Add(new RecordPlay(p.Track)); // 播放次数只记录本地曲目
            }
        }

        return s with
        {
            Playing = p with { Position = Clamp(s, p.Track, position), Listened = listened, PlayCounted = counted },
            ConsecutiveFailures = position > TimeSpan.Zero && !p.IsPaused ? 0 : s.ConsecutiveFailures,
        };
    }

    private static AppState DoFailed(AppState s, TrackId track, string reason, ImmutableArray<Effect>.Builder fx)
    {
        var p = s.Playing;
        if (p is null || p.Track != track) return s;

        int failures = s.ConsecutiveFailures + 1;
        s = s with { ConsecutiveFailures = failures };
        if (failures >= MaxConsecutiveFailures)
        {
            fx.Add(new StopAudio());
            fx.Add(new ShowToast($"连续 {MaxConsecutiveFailures} 首无法播放，已停止"));
            return s with { Playing = p with { IsPaused = true } };
        }

        fx.Add(new ShowToast($"无法播放：{reason}"));
        var plan = PlanNext(s, respectRepeat: false);
        if (plan is { } pl) return PlayTrackIn(s, pl.Context, pl.Index, TimeSpan.Zero, fx);
        fx.Add(new StopAudio());
        return s with { Playing = p with { IsPaused = true } };
    }

    private static AppState DoDeviceLost(AppState s, ImmutableArray<Effect>.Builder fx)
    {
        if (s.Playing is null) return s;
        fx.Add(new ShowToast("输出设备不可用，已暂停"));
        return DoPause(s, fx);
    }

    /// <summary>计算快照中的下一首（跳过不可用曲目），必要时按 EndBehavior 进入下一组。</summary>
    internal static (PlaybackContext Context, int Index)? PlanNext(AppState s, bool respectRepeat)
    {
        var p = s.Playing;
        if (p is null) return null;
        if (respectRepeat && p.Repeat == RepeatMode.One) return (p.ContextSnapshot, p.IndexInContext);

        var snap = p.ContextSnapshot;
        for (int i = p.IndexInContext + 1; i < snap.Count; i++)
        {
            if (s.Library.IsAvailable(snap.Order[i])) return (snap, i);
        }

        if (s.Settings.EndBehavior != EndBehavior.ContinueNextGroup || snap.Scope.Kind != ScopeKind.Group) return null;

        var arranged = s.Library.Arrange(snap.Arrangement);
        int g = -1;
        for (int i = 0; i < arranged.Groups.Length; i++)
        {
            if (arranged.Groups[i].Key == snap.Scope.Key) { g = i; break; }
        }
        if (g < 0) return null;

        for (int ng = g + 1; ng < arranged.Groups.Length; ng++)
        {
            var slice = arranged.SliceGroup(ng);
            for (int i = 0; i < slice.Count; i++)
            {
                if (s.Library.IsAvailable(slice.Order[i])) return (slice, i);
            }
        }
        return null;
    }

    private static AppState WithSuccessor(AppState s, ImmutableArray<Effect>.Builder fx)
    {
        if (s.Playing is null) return s;
        var plan = PlanNext(s, respectRepeat: true);
        fx.Add(new SetSuccessor(plan is { } pl ? pl.Context.Order[pl.Index] : null));
        return s;
    }

    // ───────────────────────── 导航层 ─────────────────────────

    private static AppState DoEnterNavigate(AppState s)
    {
        if (s.Library.IsEmpty) return s;
        if (s.Layer == Layer.Navigate) return s;
        // 外部歌单为空（例如退出了网易云）：回到文件夹排列，导航层总有内容可看。
        if (s.Arrangement == Arrangement.Playlists && s.Library.Playlists.IsEmpty) s = s with { Arrangement = Arrangement.Folder };

        var focus = FocusOnPlaying(s);
        if (focus is null)
        {
            var ctx = s.Library.Arrange(s.Arrangement);
            if (ctx.IsEmpty) return s;
            focus = new FocusState(ctx.Order[0], 0, ctx, ZoomLevel.Track, null);
        }
        return s with { Layer = Layer.Navigate, Focus = focus };
    }

    private static AppState DoMoveFocus(AppState s, int delta, ImmutableArray<Effect>.Builder fx)
    {
        if (s.Layer == Layer.Info) return s;
        if (s.Layer == Layer.Listen) s = DoEnterNavigate(s);
        if (s.Layer != Layer.Navigate || s.Focus is not { } f || f.BrowseContext.IsEmpty) return s;

        if (f.Zoom == ZoomLevel.Group) return DoMoveFocusToGroup(s, delta, fx);

        int idx = Math.Clamp(f.Index + delta, 0, f.BrowseContext.Count - 1);
        return FocusAt(s, idx, fx);
    }

    private static AppState DoMoveFocusToGroup(AppState s, int delta, ImmutableArray<Effect>.Builder fx)
    {
        if (s.Layer != Layer.Navigate || s.Focus is not { } f || f.BrowseContext.IsEmpty) return s;
        var groups = f.BrowseContext.Groups;
        if (groups.IsDefaultOrEmpty) return s;

        int g = Math.Max(0, f.BrowseContext.GroupIndexAt(Math.Max(0, f.Index)));
        int target = Math.Clamp(g + delta, 0, groups.Length - 1);
        return FocusAt(s, groups[target].Start, fx);
    }

    private static AppState FocusAt(AppState s, int idx, ImmutableArray<Effect>.Builder fx)
    {
        var f = s.Focus!;
        if (idx == f.Index) return s;
        var id = f.BrowseContext.Order[idx];
        fx.Add(new PrioritizeAnalysis(id));
        return s with { Focus = f with { Track = id, Index = idx } };
    }

    private static AppState DoConfirm(AppState s, ImmutableArray<Effect>.Builder fx)
    {
        if (s.Layer != Layer.Navigate || s.Focus is not { Index: >= 0 } f) return s;
        var browse = f.BrowseContext;

        PlaybackContext snapshot;
        int index;
        int g = browse.GroupIndexAt(f.Index);
        if (browse.Scope.Kind == ScopeKind.All && g >= 0)
        {
            snapshot = browse.SliceGroup(g);
            index = f.Index - browse.Groups[g].Start;
        }
        else
        {
            snapshot = browse;
            index = f.Index;
        }

        if (!s.Library.IsAvailable(f.Track))
        {
            fx.Add(new ShowToast("文件不可用"));
            return s;
        }

        // 确认的正是当前歌曲：不重新开始，只把快照换成新的上下文并回到聆听画布。
        if (s.Playing is { } p && p.Track == f.Track)
        {
            if (p.ContextSnapshot.Scope != snapshot.Scope || p.ContextSnapshot.Arrangement != snapshot.Arrangement)
            {
                s = WithSuccessor(s with { Playing = p with { ContextSnapshot = snapshot, IndexInContext = index } }, fx);
                fx.Add(new PersistSession(ToSession(s)));
            }
            if (p.IsPaused) s = DoResume(s, fx);
            return s with { Layer = Layer.Listen };
        }

        // I6：先更新播放锚点，再离开导航层。
        s = PlayTrackIn(s, snapshot, index, TimeSpan.Zero, fx);
        return s with { Layer = Layer.Listen };
    }

    private static AppState DoReturnFocus(AppState s)
    {
        if (s.Layer != Layer.Navigate || s.Focus is not { } f || s.Playing is null) return s;

        var ctx = f.BrowseContext;
        int idx = ctx.IndexOf(s.Playing.Track);
        if (idx < 0)
        {
            ctx = f.ContextBeforeSearch ?? s.Library.Arrange(s.Arrangement);
            idx = ctx.IndexOf(s.Playing.Track);
            if (idx < 0) return s;
            return s with { Focus = new FocusState(s.Playing.Track, idx, ctx, f.Zoom, null) };
        }
        return s with { Focus = f with { Track = s.Playing.Track, Index = idx } };
    }

    private static AppState DoSetArrangement(AppState s, Arrangement arrangement)
    {
        s = s with { Arrangement = arrangement };
        if (s.Layer != Layer.Navigate || s.Focus is not { } f) return s;

        var ctx = s.Library.Arrange(arrangement);
        var keep = f.Index >= 0 ? f.Track : s.Playing?.Track;
        int idx = keep is { } k ? ctx.IndexOf(k) : -1;
        if (idx < 0 && s.Playing is not null) idx = ctx.IndexOf(s.Playing.Track);
        if (idx < 0) idx = ctx.IsEmpty ? -1 : 0;
        var track = idx >= 0 ? ctx.Order[idx] : f.Track;
        return s with { Focus = new FocusState(track, idx, ctx, f.Zoom, null) };
    }

    private static AppState DoSetQuery(AppState s, string text, ImmutableArray<Effect>.Builder fx)
    {
        if (string.IsNullOrWhiteSpace(text)) return DoClearQuery(s);
        if (s.Layer == Layer.Info) return s;
        if (s.Layer == Layer.Listen) s = DoEnterNavigate(s);
        if (s.Focus is not { } f || s.Layer != Layer.Navigate) return s;

        var before = f.ContextBeforeSearch ?? f.BrowseContext;
        fx.Add(new RunSearch(text));
        return s with { Focus = f with { Query = text, ContextBeforeSearch = before } };
    }

    private static AppState DoSearchResults(AppState s, string query, ImmutableArray<TrackId> results)
    {
        if (s.Layer != Layer.Navigate || s.Focus is not { } f || f.Query != query) return s;

        results = results.OrEmpty();
        var ctx = new PlaybackContext(
            s.Arrangement,
            ScopeKey.Search(query),
            results,
            results.IsEmpty ? [] : [new ContextGroup("search", "搜索结果", 0, results.Length)]);

        int idx = ctx.IndexOf(f.Track);
        if (idx < 0) idx = ctx.IsEmpty ? -1 : 0;
        return s with { Focus = f with { BrowseContext = ctx, Index = idx, Track = idx >= 0 ? ctx.Order[idx] : f.Track } };
    }

    private static AppState DoClearQuery(AppState s)
    {
        if (s.Layer != Layer.Navigate || s.Focus is not { } f || f.Query is null) return s;
        var ctx = f.ContextBeforeSearch ?? s.Library.Arrange(s.Arrangement);

        int idx = f.Index >= 0 ? ctx.IndexOf(f.Track) : -1;
        if (idx < 0 && s.Playing is not null) idx = ctx.IndexOf(s.Playing.Track);
        if (idx < 0) idx = ctx.IsEmpty ? -1 : 0;
        var track = idx >= 0 ? ctx.Order[idx] : f.Track;
        return s with { Focus = new FocusState(track, idx, ctx, f.Zoom, null) };
    }

    private static AppState DoRevealTrack(AppState s, TrackId track)
    {
        if (s.Library.Find(track) is null) return s;
        var folders = s.Library.Arrange(Arrangement.Folder);
        int idx = folders.IndexOf(track);
        if (idx < 0) return s;
        int g = folders.GroupIndexAt(idx);
        var ctx = g >= 0 ? folders.SliceGroup(g) : folders;
        int local = g >= 0 ? idx - folders.Groups[g].Start : idx;

        // 以文件所在文件夹作为临时 BrowseContext；是否播放由用户确认。
        var focus = new FocusState(track, local, ctx with { Scope = ScopeKey.All }, ZoomLevel.Track, null);
        return s with { Layer = Layer.Navigate, Focus = focus };
    }

    /// <summary>
    /// 外部来源作为 BrowseContext：焦点放在 <paramref name="focusIndex"/>，否则放在正在播放的歌（如果在里面）或第一首；
    /// 是否播放由用户确认。不在曲库中的编号直接丢弃（逐个分组重新计算范围，空分组去掉）。
    /// </summary>
    private static AppState DoShowContext(AppState s, PlaybackContext ctx, int focusIndex)
    {
        ctx = KnownOnly(s.Library, ctx, ref focusIndex);
        if (ctx.IsEmpty) return s;

        int idx = focusIndex >= 0 && focusIndex < ctx.Count ? focusIndex : s.Playing is { } p ? ctx.IndexOf(p.Track) : -1;
        if (idx < 0) idx = 0;
        return s with { Layer = Layer.Navigate, Focus = new FocusState(ctx.Order[idx], idx, ctx, ZoomLevel.Track, null) };
    }

    private static PlaybackContext KnownOnly(LibraryIndex library, PlaybackContext ctx, ref int focusIndex)
    {
        var order = ctx.Order.OrEmpty();
        if (order.All(id => library.Find(id) is not null)) return ctx;

        var kept = ImmutableArray.CreateBuilder<TrackId>();
        var groups = ImmutableArray.CreateBuilder<ContextGroup>();
        int focus = focusIndex, newFocus = -1;
        void Keep(int i)
        {
            if (library.Find(order[i]) is null) return;
            if (i == focus || (newFocus < 0 && i > focus && focus >= 0)) newFocus = kept.Count;
            kept.Add(order[i]);
        }

        if (ctx.Groups.IsDefaultOrEmpty)
        {
            for (int i = 0; i < order.Length; i++) Keep(i);
        }
        else
        {
            foreach (var g in ctx.Groups)
            {
                int start = kept.Count;
                for (int i = g.Start; i < g.End && i < order.Length; i++) Keep(i);
                if (kept.Count > start) groups.Add(g with { Start = start, Count = kept.Count - start });
            }
        }
        focusIndex = newFocus;
        return ctx with { Order = kept.ToImmutable(), Groups = groups.ToImmutable() };
    }

    // ───────────────────────── 个人痕迹 ─────────────────────────

    private static AppState DoAddMarker(AppState s, TimeSpan position, string? label, ImmutableArray<Effect>.Builder fx)
    {
        if (s.Playing is null) return s;
        if (s.Playing.Track.IsRemote) return Emit(s, fx, new ShowToast("在线歌曲不能打标记"));
        fx.Add(new SaveMarker(s.Playing.Track, Clamp(s, s.Playing.Track, position), label));
        return s;
    }

    private static AppState DoToggleFavorite(AppState s, TrackId track, ImmutableArray<Effect>.Builder fx)
    {
        if (s.Library.Find(track) is null) return s;
        if (track.IsRemote) return Emit(s, fx, new ShowToast("在线歌曲不能收藏，请在网易云音乐中收藏"));
        var user = s.Library.User(track);
        var updated = user with { Favorite = !user.Favorite };
        fx.Add(new SaveFavorite(track, updated.Favorite));
        return s with { Library = s.Library.WithUserData(track, updated) };
    }

    // ───────────────────────── 曲库 ─────────────────────────

    private static AppState DoLibraryLoaded(AppState s, LibraryIndex library, SessionSnapshot? session, ImmutableArray<Effect>.Builder fx)
    {
        s = s with { Library = library, Playing = null, Focus = null, Layer = Layer.Listen };
        if (session is not null) s = s with { Arrangement = session.Arrangement };

        // 启动恢复上次的 Playing、快照和位置，但不自动播放。
        if (session is not null && library.IsAvailable(session.Track) && session.ContextSnapshot.IndexOf(session.Track, session.IndexInContext) is >= 0 and var idx)
        {
            return PlayTrackIn(s, session.ContextSnapshot, idx, session.Position, fx, paused: true, repeat: session.Repeat);
        }

        var arranged = library.Arrange(s.Arrangement);
        for (int g = 0; g < arranged.Groups.Length; g++)
        {
            var slice = arranged.SliceGroup(g);
            for (int i = 0; i < slice.Count; i++)
            {
                if (library.IsAvailable(slice.Order[i])) return PlayTrackIn(s, slice, i, TimeSpan.Zero, fx, paused: true);
            }
        }
        return s;
    }

    private static AppState DoLibraryUpdated(AppState s, LibraryIndex library, ImmutableArray<Effect>.Builder fx)
    {
        if (s.Playing is null) return DoLibraryLoaded(s, library, null, fx);

        // 正在播放的歌已从曲库移除（例如移除了它所在的曲库文件夹）：停止，并像启动时一样准备好第一首。
        if (library.Find(s.Playing.Track) is null)
        {
            fx.Add(new StopAudio());
            return DoLibraryLoaded(s, library, null, fx);
        }
        return WithSuccessor(s with { Library = library }, fx);
    }

    // ───────────────────────── 工具 ─────────────────────────

    private static AppState Emit(AppState s, ImmutableArray<Effect>.Builder fx, Effect e)
    {
        fx.Add(e);
        return s;
    }

    private static TimeSpan Clamp(AppState s, TrackId id, TimeSpan position)
    {
        if (position < TimeSpan.Zero) return TimeSpan.Zero;
        var duration = s.Library.Find(id)?.Meta.Duration ?? TimeSpan.Zero;
        return duration > TimeSpan.Zero && position > duration ? duration : position;
    }

    private static bool IsAtEnd(AppState s, PlayingState p)
    {
        var duration = s.Library.Find(p.Track)?.Meta.Duration ?? TimeSpan.Zero;
        return duration > TimeSpan.Zero && p.Position >= duration;
    }

    public static SessionSnapshot ToSession(AppState s)
    {
        var p = s.Playing ?? throw new InvalidOperationException("Nothing is playing");
        return new SessionSnapshot(p.Track, p.ContextSnapshot, p.IndexInContext, p.Position, p.Repeat, s.Arrangement);
    }
}
