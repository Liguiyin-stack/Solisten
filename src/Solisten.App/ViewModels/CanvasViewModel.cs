using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Dispatching;
using Solisten.App.Services;
using Solisten.Core;
using Solisten.Core.Model;
using Solisten.Library;
using Solisten.Library.Analysis;
using Solisten.Rendering.Progress;

namespace Solisten.App.ViewModels;

/// <summary>一次切歌事务需要的全部内容：背景、封面、整曲波形和标题一起切换（§7.3）。</summary>
public sealed record TrackVisuals(Track Track, Palette Palette, string? CoverPath, SoundFeatures? Features, IReadOnlyList<TimeSpan> Markers);

/// <summary>
/// 聆听画布的视图模型：订阅 Store，只读状态、只发 Action。
/// Store 的变化可能来自任意线程，这里合并后在 UI 线程上应用最新状态。
/// </summary>
public sealed partial class CanvasViewModel : ObservableObject
{
    private readonly AppHost _host;
    private readonly DispatcherQueue _ui;
    private AppState? _pending;
    private int _scheduled;
    private AppState? _applied;
    private TrackVisuals? _visuals;
    private long _visualsGeneration;
    private PlaybackContext? _ribbonContext;
    private Dictionary<TrackId, string> _coverHashes = [];
    private LibraryIndex? _coverLibrary;

    public CanvasViewModel(AppHost host, DispatcherQueue ui)
    {
        _host = host;
        _ui = ui;
        host.Store.StateChanged += (_, e) => Schedule(e.Current);
        host.AnalysisReady += id => _ui.Run(() => OnAnalysisReady(id));
        host.MarkersChanged += id => _ui.Run(() => { if (id == _visuals?.Track.Id) ReloadVisuals(id, transition: false); });
        Schedule(host.Store.State);
    }

    public AppHost Host => _host;
    public AppState State => _applied ?? _host.Store.State;

    /// <summary>播放锚点换歌，视图需要做 A/B 交叉淡化。</summary>
    public event Action<TrackVisuals, bool>? VisualsChanged;

    /// <summary>导航层焦点移动，视图需要把观察框滚动到可见位置。</summary>
    public event Action<int>? FocusIndexChanged;

    /// <summary>层发生变化（聆听 / 导航 / 信息）。</summary>
    public event Action<Layer>? LayerChanged;

    [ObservableProperty] public partial string Title { get; set; } = "Solisten";
    [ObservableProperty] public partial string Subtitle { get; set; } = "";
    [ObservableProperty] public partial string PathHint { get; set; } = "";
    [ObservableProperty] public partial bool IsPaused { get; set; } = true;
    [ObservableProperty] public partial bool HasTrack { get; set; }
    [ObservableProperty] public partial bool IsLibraryEmpty { get; set; } = true;
    [ObservableProperty] public partial bool IsRepeatOne { get; set; }
    [ObservableProperty] public partial Layer Layer { get; set; }
    [ObservableProperty] public partial string TimeText { get; set; } = "00:00 / 00:00";
    [ObservableProperty] public partial string? Query { get; set; }
    [ObservableProperty] public partial string RibbonStatus { get; set; } = "";
    [ObservableProperty] public partial bool IsScanning { get; set; }

    public ObservableCollection<RibbonItem> RibbonItems { get; } = [];

    public TrackVisuals? Visuals => _visuals;

    public void Dispatch(AppAction action) => _host.Store.Dispatch(action);

    private void Schedule(AppState state)
    {
        Volatile.Write(ref _pending, state);
        if (Interlocked.Exchange(ref _scheduled, 1) == 0)
        {
            _ui.Run(() =>
            {
                Interlocked.Exchange(ref _scheduled, 0);
                var s = Volatile.Read(ref _pending);
                if (s is not null) Apply(s);
            });
        }
    }

    private void Apply(AppState s)
    {
        var prev = _applied;
        _applied = s;

        IsLibraryEmpty = s.Library.IsEmpty;
        HasTrack = s.Playing is not null;
        IsPaused = s.Playing?.IsPaused ?? true;
        IsRepeatOne = s.Playing?.Repeat == RepeatMode.One;
        AppHost.PreventSleep(s.Playing is { IsPaused: false });

        bool anchorChanged = prev?.Canvas.PlayingAnchor != s.Canvas.PlayingAnchor;
        bool metadataChanged = !ReferenceEquals(prev?.Library, s.Library) && _visuals is { } v && v.Track.Id == s.Canvas.PlayingAnchor && v.Track != s.PlayingTrack;
        if (anchorChanged || metadataChanged)
        {
            if (s.Canvas.PlayingAnchor is { } id) ReloadVisuals(id, transition: anchorChanged);
            else ClearVisuals();
        }

        if (prev?.Layer != s.Layer)
        {
            Layer = s.Layer;
            LayerChanged?.Invoke(s.Layer);
        }

        if (s.Layer == Layer.Navigate && s.Focus is { } f)
        {
            Query = f.Query;
            if (!ReferenceEquals(_ribbonContext, f.BrowseContext) || !ReferenceEquals(prev?.Library, s.Library)) RebuildRibbon(s, f.BrowseContext);
            UpdateRibbonFlags(s, prev);
            if (prev?.Focus?.Index != f.Index || prev?.Layer != Layer.Navigate) FocusIndexChanged?.Invoke(f.Index);
        }
        else if (prev?.Layer == Layer.Navigate)
        {
            Query = null;
        }

        RefreshTime();
    }

    /// <summary>位置计时器调用：读取音频时钟，更新时间文字，并把位置报告给 Store（播放计数、"上一首"规则）。</summary>
    public void Tick()
    {
        var s = _host.Store.State;
        if (s.Playing is { IsPaused: false } p)
        {
            var (pos, running) = _host.PositionOf(p.Track);
            if (running) Dispatch(new PositionChanged(pos));
        }
        RefreshTime();
    }

    private void RefreshTime()
    {
        var s = _applied;
        if (s?.Playing is not { } p) { TimeText = "00:00 / 00:00"; return; }
        var (pos, _) = _host.PositionOf(p.Track);
        var duration = s.PlayingTrack?.Meta.Duration ?? TimeSpan.Zero;
        TimeText = $"{ProgressLayout.FormatTime(pos)} / {ProgressLayout.FormatTime(duration)}";
    }

    // ───────────────────────── 切歌事务 ─────────────────────────

    private void ClearVisuals()
    {
        _visuals = null;
        Title = "Solisten";
        Subtitle = "";
        PathHint = "";
    }

    private void ReloadVisuals(TrackId id, bool transition)
    {
        var track = _host.Store.State.Library.Find(id);
        if (track is null) return;
        long gen = ++_visualsGeneration;
        var paths = _host.Paths;
        var db = _host.Library.Database;

        _ = Task.Run(() =>
        {
            Palette palette = Palette.Neutral;
            string? cover = null;
            SoundFeatures? features = null;
            IReadOnlyList<TimeSpan> markers = [];
            try
            {
                var analysis = db.GetAnalysis(id);
                if (analysis is { Palette: { } p }) palette = p;
                if (db.GetCoverHash(id) is { } hash) cover = paths.FindCover(hash);
                if (analysis is { FeaturesVersion: SoundFeatures.CurrentVersion }) features = AnalysisScheduler.LoadFeatures(paths, id);
                markers = db.GetMarkers(id).Select(m => m.Position).ToList();
            }
            catch (Exception e) { _host.Log.Write($"load visuals failed {id}: {e.Message}"); }

            var visuals = new TrackVisuals(track, palette, cover, features, markers);
            _ui.Run(() =>
            {
                if (gen != _visualsGeneration) return;
                _visuals = visuals;
                Title = track.Meta.Title;
                Subtitle = string.Join(" · ", new[] { track.Meta.Artist, track.Meta.Album }.Where(x => !string.IsNullOrWhiteSpace(x)));
                PathHint = track.Path;
                VisualsChanged?.Invoke(visuals, transition);
            });
        });
    }

    private void OnAnalysisReady(TrackId id)
    {
        // 正在播放的歌分析完成：波形淡入、主色更新（不做切歌动画）。
        if (id == _applied?.Canvas.PlayingAnchor) ReloadVisuals(id, transition: false);
        foreach (var item in RibbonItems)
        {
            if (item.Track == id) item.ThumbPath = ThumbFor(id);
        }
    }

    // ───────────────────────── 单曲上下文带 ─────────────────────────

    private void RebuildRibbon(AppState s, PlaybackContext ctx)
    {
        _ribbonContext = ctx;
        if (!ReferenceEquals(_coverLibrary, s.Library))
        {
            _coverHashes = _host.Library.Database.GetAllCoverHashes();
            _coverLibrary = s.Library;
        }
        RibbonItems.Clear();
        for (int i = 0; i < ctx.Count; i++)
        {
            var id = ctx.Order[i];
            var t = s.Library.Find(id);
            var g = ctx.GroupAt(i);
            bool groupStart = g is not null && g.Start == i;
            RibbonItems.Add(new RibbonItem(i, id, t?.Meta.Title ?? "?", t?.Meta.Artist ?? "", t?.Available ?? false,
                groupStart ? g!.DisplayName : null, groupStart && i > 0, ThumbFor(id)));
        }
        RibbonStatus = ctx.Scope.Kind == ScopeKind.Search
            ? (ctx.IsEmpty ? "没有匹配的歌" : $"{ctx.Count} 首匹配")
            : $"{ctx.Groups.Length} 组 · {ctx.Count} 首";
    }

    private string? ThumbFor(TrackId id)
    {
        if (!_coverHashes.TryGetValue(id, out var hash)) return null;
        var p = _host.Paths.Thumb(hash, 256);
        return File.Exists(p) ? p : _host.Paths.FindCover(hash);
    }

    private void UpdateRibbonFlags(AppState s, AppState? prev)
    {
        var playing = s.Canvas.PlayingAnchor;
        int focus = s.Focus?.Index ?? -1;
        int prevFocus = prev?.Focus?.Index ?? -1;
        bool full = prev?.Canvas.PlayingAnchor != playing || prev?.Layer != Layer.Navigate || !ReferenceEquals(prev?.Focus?.BrowseContext, s.Focus?.BrowseContext);
        if (full)
        {
            foreach (var item in RibbonItems)
            {
                item.IsPlaying = item.Track == playing;
                item.IsFocused = item.Index == focus;
            }
            return;
        }
        if (prevFocus >= 0 && prevFocus < RibbonItems.Count) RibbonItems[prevFocus].IsFocused = false;
        if (focus >= 0 && focus < RibbonItems.Count) RibbonItems[focus].IsFocused = true;
    }
}

/// <summary>上下文带中的一张卡片。◉ 只出现在正在播放的歌上；观察框只出现在浏览焦点上。</summary>
public sealed partial class RibbonItem : ObservableObject
{
    public RibbonItem(int index, TrackId track, string title, string artist, bool available, string? groupLabel, bool groupBreak, string? thumb)
    {
        Index = index;
        Track = track;
        Title = title;
        Artist = available ? artist : "文件不可用";
        Available = available;
        GroupLabel = groupLabel;
        GroupBreak = groupBreak;
        ThumbPath = thumb;
    }

    public int Index { get; }
    public TrackId Track { get; }
    public string Title { get; }
    public string Artist { get; }
    public bool Available { get; }
    public string? GroupLabel { get; }

    /// <summary>不同分组之间的空间断层。</summary>
    public bool GroupBreak { get; }

    [ObservableProperty] public partial bool IsPlaying { get; set; }
    [ObservableProperty] public partial bool IsFocused { get; set; }
    [ObservableProperty] public partial string? ThumbPath { get; set; }

}
