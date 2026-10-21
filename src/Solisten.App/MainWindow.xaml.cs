using System.ComponentModel;
using System.Diagnostics;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Solisten.App.Services;
using Solisten.App.ViewModels;
using Solisten.Audio.Engine;
using Solisten.Core;
using Solisten.Core.Model;
using Solisten.Rendering.Progress;
using Solisten.Rendering.Space;
using Windows.Graphics;
using Windows.System;
using Windows.UI;
using Windows.UI.ViewManagement;
using DispatcherQueueTimer = Microsoft.UI.Dispatching.DispatcherQueueTimer;
using FocusState = Microsoft.UI.Xaml.FocusState;

namespace Solisten.App;

/// <summary>单窗口：一张会展开的聆听画布（§8.1）。</summary>
public sealed partial class MainWindow : Window
{
    private static readonly TimeSpan ControlsIdle = TimeSpan.FromSeconds(2.5);

    private readonly AppHost _host;
    private readonly DispatcherQueueTimer _tick;
    private readonly DispatcherQueueTimer _hideControls;
    private readonly DispatcherQueueTimer _hideToast;
    private readonly DispatcherQueueTimer _searchDebounce;
    private readonly UISettings _uiSettings = new();
    private readonly AccessibilitySettings _accessibility = new();
    private bool _visible = true;
    private bool _darkGlass = true;
    private bool _suppressSearchEvent;
    private bool _suppressVolumeEvent;
    private bool _suppressArrangementEvent;

    public MainWindow(AppHost host)
    {
        _host = host;
        ViewModel = new CanvasViewModel(host, DispatcherQueue);
        InitializeComponent();

        ExtendsContentIntoTitleBar = true;
        // 任务栏、Alt+Tab 和标题栏的图标（exe 本身的图标由 csproj 的 ApplicationIcon 嵌入）
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "Solisten.ico"));
        SetTitleBar(TitleBar);
        RestoreWindowBounds();

        Progress.PositionSource = host.PositionOf;
        Space.PositionSource = () =>
        {
            var (pos, running) = host.PositionOf(host.Store.State.Canvas.PlayingAnchor);
            return (pos, running);
        };
        Space.Mode = host.Settings.Space;
        Stage.SizeChanged += (_, _) => UpdateStage();
        NavigateLayer.SizeChanged += (_, _) => UpdateGlass();
        SettingsSheet.SizeChanged += (_, _) => UpdateGlass();
        InfoLayer.SizeChanged += (_, _) => UpdateGlass();
        Root.SizeChanged += (_, _) => UpdateGlass();
        Root.SizeChanged += (_, _) => UpdateStage();
        Progress.SeekRequested += t => { ViewModel.Dispatch(new Seek(t)); Space.Wake(); };
        Progress.SeekPreview += t => { if (t is { } p) TimeLabel.Text = ProgressLayout.FormatTime(p); };

        _suppressVolumeEvent = true;
        VolumeSlider.Value = host.Settings.Volume;
        _suppressVolumeEvent = false;

        ViewModel.VisualsChanged += OnVisualsChanged;
        // 边播边分析的实时特征：只交给正在显示的那首歌（分析线程上触发，聆听空间的属性可以跨线程设置）。
        host.LiveFeaturesUpdated += (id, f) => { if (Interlocked.Read(ref _visualTrack) == id.Value) Space.LiveFeatures = f; };
        ViewModel.LayerChanged += OnLayerChanged;
        ViewModel.FocusIndexChanged += BringFocusIntoView;
        ViewModel.PropertyChanged += OnViewModelPropertyChanged;
        host.Store.StateChanged += (_, e) =>
        {
            if (e.Previous.Canvas.ControlsRevealed != e.Current.Canvas.ControlsRevealed)
                DispatcherQueue.Run(() => ApplyControlsRevealed(e.Current.Canvas.ControlsRevealed));
        };
        host.Toast += message => DispatcherQueue.Run(() => ShowToast(message));
        host.MarkersChanged += id => DispatcherQueue.Run(() =>
        {
            if (_host.Store.State.Layer == Layer.Info && _host.Store.State.Playing?.Track == id) FillMarkers(id);
        });
        host.Library.ScanProgressChanged += p => DispatcherQueue.Run(() => ScanText.Text = $"正在读取 {p.Processed} / {p.Total}");

        _tick = DispatcherQueue.CreateTimer();
        _tick.Interval = TimeSpan.FromMilliseconds(250);
        _tick.Tick += (_, _) =>
        {
            ViewModel.Tick();
            if (ReduceMotion) Progress.Invalidate();
        };
        _tick.Start();
        InitLyrics();
        InitPointer();

        _hideControls = DispatcherQueue.CreateTimer();
        _hideControls.Interval = ControlsIdle;
        _hideControls.IsRepeating = false;
        _hideControls.Tick += (_, _) =>
        {
            // 鼠标还停在进度或控制上：再等一轮
            if (PointerOverBottom()) { _hideControls.Start(); return; }
            ViewModel.Dispatch(new HideControls());
        };

        _hideToast = DispatcherQueue.CreateTimer();
        _hideToast.Interval = TimeSpan.FromSeconds(3);
        _hideToast.IsRepeating = false;
        _hideToast.Tick += (_, _) => Toast.Opacity = 0;

        _searchDebounce = DispatcherQueue.CreateTimer();
        _searchDebounce.Interval = TimeSpan.FromMilliseconds(160);
        _searchDebounce.IsRepeating = false;
        _searchDebounce.Tick += (_, _) => ViewModel.Dispatch(new SetQuery(SearchBox.Text));

        VisibilityChanged += (_, e) => { _visible = e.Visible; Space.IsWindowVisible = e.Visible; UpdateFrameMode(); };
        // 桌面应用不支持 AccessibilitySettings.HighContrastChanged；高对比度切换时 ColorValuesChanged 也会触发。
        try
        {
            _uiSettings.ColorValuesChanged += (_, _) => DispatcherQueue.Run(ApplyHighContrast);
            _uiSettings.AnimationsEnabledChanged += (_, _) => DispatcherQueue.Run(UpdateFrameMode);
        }
        catch (System.Runtime.InteropServices.COMException e) { host.Log.Write($"system settings events unavailable: {e.Message}"); }
        Root.SizeChanged += (_, e) => ApplyWidth(e.NewSize.Width);
        Closed += OnClosed;

        SettingsSheet.Attach(host, PickFolderAsync);
        SettingsSheet.CloseRequested += CloseSettings;
        SettingsSheet.CanvasSettingsChanged += () =>
        {
            Space.Mode = _host.Settings.Space;
            UpdateFrameMode();
            Progress.Invalidate();
        };

        ApplyHighContrast();
        UpdateFrameMode();
        Root.Loaded += (_, _) => { Root.Focus(FocusState.Programmatic); ApplyMinimumSize(); };
    }

    public CanvasViewModel ViewModel { get; }

    private bool ReduceMotion => _host.Settings.ReduceMotion switch
    {
        ReduceMotionMode.On => true,
        ReduceMotionMode.Off => false,
        _ => !_uiSettings.AnimationsEnabled,
    };

    // ───────────────────────── 窗口 ─────────────────────────

    private void RestoreWindowBounds()
    {
        var s = _host.Settings;
        if (s is { WindowX: { } x, WindowY: { } y, WindowWidth: { } w, WindowHeight: { } h } && w > 200 && h > 200)
        {
            var rect = new RectInt32(x, y, w, h);
            var area = DisplayArea.GetFromRect(rect, DisplayAreaFallback.Nearest);
            var work = area.WorkArea;
            // 保证窗口在某个显示器上可见
            rect.Width = Math.Min(rect.Width, work.Width);
            rect.Height = Math.Min(rect.Height, work.Height);
            rect.X = Math.Clamp(rect.X, work.X, work.X + work.Width - rect.Width);
            rect.Y = Math.Clamp(rect.Y, work.Y, work.Y + work.Height - rect.Height);
            AppWindow.MoveAndResize(rect);
        }
        else
        {
            AppWindow.Resize(new SizeInt32(1180, 760));
        }
        if (s.WindowMaximized && AppWindow.Presenter is OverlappedPresenter op) op.Maximize();
    }

    private void ApplyMinimumSize()
    {
        if (AppWindow.Presenter is OverlappedPresenter op)
        {
            double scale = Root.XamlRoot?.RasterizationScale ?? 1.0;
            op.PreferredMinimumWidth = (int)(720 * scale);
            op.PreferredMinimumHeight = (int)(480 * scale);
        }
    }

    /// <summary>另一个实例被启动时：恢复最小化的窗口并激活。</summary>
    public void BringToFront()
    {
        if (AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized } op) op.Restore();
        AppWindow.Show();
        Activate();
    }

    private void ToggleFullScreen()
    {
        AppWindow.SetPresenter(AppWindow.Presenter.Kind == AppWindowPresenterKind.FullScreen
            ? AppWindowPresenterKind.Default
            : AppWindowPresenterKind.FullScreen);
    }

    private void OnClosed(object sender, WindowEventArgs args)
    {
        _tick.Stop();
        var presenter = AppWindow.Presenter as OverlappedPresenter;
        bool maximized = presenter?.State == OverlappedPresenterState.Maximized;
        var pos = AppWindow.Position;
        var size = AppWindow.Size;
        _host.UpdateSettings(s => maximized || AppWindow.Presenter.Kind != AppWindowPresenterKind.Overlapped
            ? s with { WindowMaximized = maximized }
            : s with { WindowX = pos.X, WindowY = pos.Y, WindowWidth = size.Width, WindowHeight = size.Height, WindowMaximized = false });
        _host.SaveSessionNow();
        AppHost.PreventSleep(false);
        _host.Dispose();
    }

    /// <summary>窗口变窄时优先保留当前聆听窗口、歌名和暂停操作。</summary>
    private void ApplyWidth(double width)
    {
        bool narrow = width < 900;
        SubtitleText.FontSize = narrow ? 15 : 17;
        TitleText.FontSize = width < 820 ? 30 : 38;
        VolumeSlider.Visibility = Ui.Collapsed(narrow);
        VolumeIcon.Visibility = Ui.Collapsed(narrow);
    }

    // ───────────────────────── 切歌事务 / 外观 ─────────────────────────

    private long _visualTrack;

    private void OnVisualsChanged(TrackVisuals v, bool transition)
    {
        if (Interlocked.Exchange(ref _visualTrack, v.Track.Id.Value) != v.Track.Id.Value) Space.LiveFeatures = null;
        LoadLyricsFor(v.Track);
        ApplyInk(v.Palette.TextOnBase);
        Progress.Model = new ProgressModel(v.Track.Id, v.Track.Meta.Duration, v.Palette, v.Markers,
            _accessibility.HighContrast, SystemColor("SystemColorWindowTextColor"), SystemColor("SystemColorHighlightColor"));
        Space.ShowScene(new SpaceScene(v.Palette, v.CoverPath, v.Features, v.Track.Meta.Duration), transition);
        if (ViewModel.Layer == Layer.Info) FillInfo();
    }

    private void ApplyInk(Rgb text)
    {
        if (_accessibility.HighContrast) return;
        var c = Color.FromArgb(255, text.R, text.G, text.B);
        ((SolidColorBrush)Root.Resources["InkBrush"]).Color = c;
        ((SolidColorBrush)Root.Resources["InkSoftBrush"]).Color = c;
        bool light = text.R > 128;
        // 搜索框、下拉框等系统控件跟随薄片的明暗。
        var theme = light ? ElementTheme.Dark : ElementTheme.Light;
        NavigateLayer.RequestedTheme = theme;
        // 玻璃的色调跟随画布明暗：浅色文字（深背景）用深色玻璃，深色文字（浅背景）用浅色玻璃
        _darkGlass = light;
        UpdateGlass();
        InfoLayer.RequestedTheme = theme;
        EmptyGuide.RequestedTheme = theme;
        SettingsSheet.RequestedTheme = theme;
        CanvasLayer.RequestedTheme = theme; // 控制栏按钮的悬停/按下效果也用同一套玻璃样式
        Toast.RequestedTheme = theme;
        SettingsSheet.SheetBackground = new SolidColorBrush(Colors.Transparent);
    }

    private void ApplyHighContrast()
    {
        bool hc = _accessibility.HighContrast;
        Space.HighContrastBackground = SystemColor("SystemColorWindowColor") ?? Colors.Black;
        Space.HighContrast = hc;
        if (hc)
        {
            var fg = SystemColor("SystemColorWindowTextColor") ?? Colors.White;
            ((SolidColorBrush)Root.Resources["InkBrush"]).Color = fg;
            ((SolidColorBrush)Root.Resources["InkSoftBrush"]).Color = fg;
            ((SolidColorBrush)Root.Resources["ScrimBrush"]).Color = SystemColor("SystemColorWindowColor") ?? Colors.Black;
            NavigateLayer.Background = new SolidColorBrush(SystemColor("SystemColorWindowColor") ?? Colors.Black); // 高对比度：不用透明材质
            SettingsSheet.RequestedTheme = ElementTheme.Default;
            CanvasLayer.RequestedTheme = ElementTheme.Default;
            Toast.RequestedTheme = ElementTheme.Default;
            SettingsSheet.SheetBackground = new SolidColorBrush(SystemColor("SystemColorWindowColor") ?? Colors.Black);
        }
        else
        {
            ((SolidColorBrush)Root.Resources["ScrimBrush"]).Color = Colors.Transparent;
            NavigateLayer.Background = new SolidColorBrush(Colors.Transparent);
            if (ViewModel.Visuals is { } v) ApplyInk(v.Palette.TextOnBase);
        }
        UpdateGlass();
        if (ViewModel.Visuals is { } visuals)
            Progress.Model = Progress.Model with { HighContrast = hc, HighContrastForeground = SystemColor("SystemColorWindowTextColor"), HighContrastAccent = SystemColor("SystemColorHighlightColor") };
    }

    private static Color? SystemColor(string key) =>
        Application.Current.Resources.TryGetValue(key, out var v) && v is Color c ? c : null;

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(CanvasViewModel.IsPaused)) UpdateFrameMode();
    }

    private void UpdateFrameMode()
    {
        Progress.SetPlaying(!ViewModel.IsPaused && !ReduceMotion, _visible);
        Space.ReduceMotion = ReduceMotion;
        Space.Wake();
    }

    /// <summary>
    /// 封面放在歌名与进度线之间的舞台中央：边长约为窗口宽度的 26%（22%–30%），
    /// 同时不超过舞台高度的 62%，给呼吸、阴影和粒子环带留出空间。
    /// </summary>
    private void UpdateStage()
    {
        if (Root.ActualHeight <= 0 || Root.ActualWidth <= 0 || Stage.ActualHeight <= 0 || Stage.ActualWidth <= 0) return;
        var origin = Stage.TransformToVisual(Root).TransformPoint(new Windows.Foundation.Point(0, 0));
        // 舞台可能因侧边面板而变窄：封面以舞台（而不是整个窗口）为准居中和取大小
        // 歌词模式下舞台就是封面本身（由 LayoutLyrics 定好边长）
        double side = _lyricsMode
            ? Math.Max(80, Math.Min(Stage.ActualWidth, Stage.ActualHeight))
            : CoverSide(Stage.ActualWidth, Stage.ActualHeight);
        Space.SetStage(
            (float)((origin.X + Stage.ActualWidth / 2) / Root.ActualWidth),
            (float)((origin.Y + Stage.ActualHeight / 2) / Root.ActualHeight),
            (float)side);
    }

    internal static double CoverSide(double windowWidth, double stageHeight) =>
        Math.Max(80, Math.Min(Math.Clamp(windowWidth * 0.26, windowWidth * 0.22, windowWidth * 0.30), stageHeight * 0.62));


    // ───────────────────────── 歌词 ─────────────────────────

    /// <summary>画布至少这么宽才显示整屏歌词（否则封面和歌词都太挤，回到封面居中）。</summary>
    private const double MinLyricsCanvasWidth = 900;

    private DispatcherQueueTimer? _lyricsTick;
    private CancellationTokenSource? _lyricsLoad;
    private TrackId? _lyricsTrack;
    private bool _lyricsMode;

    private void InitLyrics()
    {
        LyricsPanel.Ink = (Microsoft.UI.Xaml.Media.Brush)Root.Resources["InkBrush"];
        LyricsPanel.SeekRequested += position => ViewModel.Dispatch(new Seek(position));
        LyricsButton.IsChecked = _host.Settings.ShowLyrics;
        StageArea.SizeChanged += (_, _) => ApplyLyricsLayout();
        _lyricsTick = DispatcherQueue.CreateTimer();
        _lyricsTick.Interval = TimeSpan.FromMilliseconds(80);
        _lyricsTick.Tick += (_, _) =>
        {
            if (_host.Store.State.Playing is { } p) LyricsPanel.Update(_host.PositionOf(p.Track).Position);
        };
    }

    /// <summary>切到另一首歌：加载它的歌词（先清空旧歌词，布局等新歌词到了再决定，避免来回跳）。</summary>
    private void LoadLyricsFor(Track track)
    {
        if (_lyricsTrack == track.Id) return;
        _lyricsTrack = track.Id;
        _lyricsLoad?.Cancel();
        _lyricsLoad?.Dispose();
        var cts = _lyricsLoad = new CancellationTokenSource();
        LyricsPanel.SetLyrics(null);
        _ = LoadLyricsAsync(track, cts.Token);
    }

    private async Task LoadLyricsAsync(Track track, CancellationToken ct)
    {
        Lyrics? lyrics = null;
        try { lyrics = await _host.Lyrics.LoadAsync(track, ct); }
        catch (OperationCanceledException) { return; }
        catch (Exception e) when (e is not OutOfMemoryException) { _host.Log.Write($"lyrics failed {track.Id}: {e.Message}"); }
        if (ct.IsCancellationRequested || _lyricsTrack != track.Id) return;
        LyricsPanel.ReduceMotion = ReduceMotion;
        LyricsPanel.SetLyrics(lyrics);
        ApplyLyricsLayout();
        if (_host.Store.State.Playing is { } p) LyricsPanel.Update(_host.PositionOf(p.Track).Position);
    }

    /// <summary>打开了歌词、有歌词且画布够宽：封面移到左栏，右栏显示歌词；否则封面居中、歌名在上方。</summary>
    private void ApplyLyricsLayout()
    {
        LyricsButton.IsChecked = _host.Settings.ShowLyrics;
        bool show = _host.Settings.ShowLyrics && LyricsPanel.Lyrics is { IsEmpty: false } && CanvasLayer.ActualWidth >= MinLyricsCanvasWidth;
        bool changed = show != _lyricsMode;
        if (changed)
        {
            _lyricsMode = show;
            LyricsPanel.Visibility = Ui.Visible(show);
            LyricsIdentity.Visibility = Ui.Visible(show);
            Identity.Visibility = Ui.Collapsed(show);
        }
        if (show) LayoutLyrics();
        else if (changed) ResetStageLayout();

        if (changed)
        {
            // 布局还没重算：先完成布局再告诉聆听空间封面的新位置
            CanvasLayer.UpdateLayout();
            UpdateStage();
        }
        // 封面组可能只是移动了位置（大小不变，舞台不会触发 SizeChanged）：布局结束后补一次
        if (show || changed) DispatcherQueue.Run(UpdateStage, Microsoft.UI.Dispatching.DispatcherQueuePriority.Low);

        if (show && LyricsPanel.Lyrics is { IsSynced: true }) _lyricsTick?.Start();
        else _lyricsTick?.Stop();
    }

    /// <summary>
    /// 歌词模式的版式（按舞台区大小计算）：
    /// <list type="bullet">
    /// <item>左右留白约为宽度的 7%，两栏之间约 6%；左栏占可用宽度的 42%，右栏是歌词；</item>
    /// <item>左栏里"封面 + 歌名"作为一组垂直居中，封面尽量大，歌名与封面左边缘对齐；</item>
    /// <item>歌词的当前行和封面中心在同一高度；字号随舞台高度缩放。</item>
    /// </list>
    /// </summary>
    private void LayoutLyrics()
    {
        double w = StageArea.ActualWidth, h = StageArea.ActualHeight;
        if (w <= 0 || h <= 0) return;
        double margin = Math.Clamp(w * 0.07, 40, 160);
        double gap = Math.Clamp(w * 0.06, 40, 120);
        double usable = Math.Max(240, w - margin * 2 - gap);
        double left = usable * 0.42;

        // 封面越大歌名越大；先按左栏宽度估一个字号量出歌名高度，再定封面边长
        double guess = Math.Min(left, h * 0.72);
        LyricsTitle.FontSize = Math.Round(Math.Clamp(guess * 0.068, 18, 30));
        LyricsSubtitle.FontSize = Math.Round(Math.Clamp(guess * 0.04, 13, 18));
        LyricsIdentity.Measure(new Windows.Foundation.Size(Math.Max(80, guess), double.PositiveInfinity));
        double identity = LyricsIdentity.DesiredSize.Height;
        double side = Math.Floor(Math.Clamp(Math.Min(left, h * 0.9 - identity), 140, 640));
        // 底部的进度和控制无操作时会淡出：按"舞台区 + 底部"的整体高度居中，看起来不头重脚轻；
        // 但不压到底部（进度出现时不重叠），并留一点间隙。
        double below = Math.Max(0, CanvasLayer.ActualHeight - StageArea.TransformToVisual(CanvasLayer).TransformPoint(new Windows.Foundation.Point(0, 0)).Y - h);
        double group = side + identity;
        double top = Math.Max(0, Math.Round(Math.Min((h + below - group) / 2, h - group - 16)));

        StageColumn.Width = new GridLength(margin + left);
        LyricsColumn.Width = new GridLength(1, GridUnitType.Star);
        StageRow.Height = new GridLength(side);
        StageGroup.HorizontalAlignment = HorizontalAlignment.Left;
        StageGroup.VerticalAlignment = VerticalAlignment.Top;
        StageGroup.Width = side;
        StageGroup.Margin = new Thickness(Math.Round(margin + (left - side) / 2), top, 0, 0);

        LyricsPanel.Margin = new Thickness(gap, 0, margin, 0);
        LyricsPanel.FocusY = top + side / 2;
        LyricsPanel.LineFontSize = Math.Clamp(h * 0.052, 22, 42);
    }

    /// <summary>回到没有歌词时的版式：舞台铺满，封面居中。</summary>
    private void ResetStageLayout()
    {
        StageColumn.Width = new GridLength(1, GridUnitType.Star);
        LyricsColumn.Width = new GridLength(0);
        StageRow.Height = new GridLength(1, GridUnitType.Star);
        StageGroup.HorizontalAlignment = HorizontalAlignment.Stretch;
        StageGroup.VerticalAlignment = VerticalAlignment.Stretch;
        StageGroup.Width = double.NaN;
        StageGroup.Margin = new Thickness(0);
        LyricsPanel.FocusY = double.NaN;
    }

    private void OnLyricsClick(object sender, RoutedEventArgs e)
    {
        // 以按钮点击后的勾选状态为准（ToggleButton 自己已经切换过了）。
        SetLyricsShown(LyricsButton.IsChecked == true);
        Root.Focus(FocusState.Programmatic);
    }

    private void ToggleLyrics() => SetLyricsShown(!_host.Settings.ShowLyrics);

    /// <summary>打开或关闭歌词并保存。打开了却显示不出来时说明原因。</summary>
    private void SetLyricsShown(bool show)
    {
        _host.UpdateSettings(s => s with { ShowLyrics = show });
        ApplyLyricsLayout();
        if (!show) ShowToast("已隐藏歌词");
        else if (LyricsPanel.Lyrics is not { IsEmpty: false }) ShowToast("这首歌没有找到歌词");
        else if (!_lyricsMode) ShowToast("窗口太窄，放大窗口后显示歌词");
        else ShowToast("已显示歌词");
    }
    /// <summary>设置卡片（460 + 右侧 12 的间隙）和资料面板中较宽的那个。</summary>
    private const double SidePanelWidth = 472;
    /// <summary>侧边面板打开后，画布至少要保留这么宽才让位；更窄时面板直接覆盖在画布上。</summary>
    private const double MinCanvasWidth = 560;

    private bool SidePanelOpen => SettingsSheet.Visibility == Visibility.Visible || InfoLayer.Visibility == Visibility.Visible;

    /// <summary>
    /// 所有柔光玻璃面板（导航带、设置、资料）：让聆听空间在它们后面绘制玻璃。
    /// 侧边面板打开时，画布向左让出面板的宽度，封面、歌名、进度和控制在剩下的区域里重新居中，不与面板重叠；
    /// 窗口太窄时面板覆盖在画布上，并隐藏底部的进度、时间和控制。
    /// </summary>
    private void UpdateGlass()
    {
        bool hc = _accessibility.HighContrast;
        SetGlassFor("navigate", NavigateLayer, GlassAttach.Left | GlassAttach.Right | GlassAttach.Bottom, hc);
        SetGlassFor("settings", SettingsSheet, GlassAttach.None, hc); // 浮在右侧的卡片：四角圆角，和窗口边缘留出间隙
        SetGlassFor("info", InfoLayer, GlassAttach.Right | GlassAttach.Bottom, hc);

        bool roomy = Root.ActualWidth - SidePanelWidth >= MinCanvasWidth;
        bool side = SidePanelOpen;
        var margin = new Thickness(0, 40, side && roomy ? SidePanelWidth : 0, 0);
        if (CanvasLayer.Margin != margin) CanvasLayer.Margin = margin;

        _bottomBlocked = _host.Store.State.Layer == Layer.Navigate || (side && !roomy);
        ApplyControlsRevealed(_host.Store.State.Canvas.ControlsRevealed);
    }

    private void SetGlassFor(string key, FrameworkElement panel, GlassAttach attach, bool highContrast)
    {
        if (panel.Visibility != Visibility.Visible || highContrast || panel.ActualHeight <= 0 || panel.ActualWidth <= 0)
        {
            Space.SetGlass(key, null, 0, attach, _darkGlass);
            return;
        }
        var o = panel.TransformToVisual(Root).TransformPoint(new Windows.Foundation.Point(0, 0));
        double right = attach.HasFlag(GlassAttach.Right) ? Root.ActualWidth : o.X + panel.ActualWidth;
        double bottom = attach.HasFlag(GlassAttach.Bottom) ? Root.ActualHeight : o.Y + panel.ActualHeight;
        double left = attach.HasFlag(GlassAttach.Left) ? 0 : o.X;
        Space.SetGlass(key, new Windows.Foundation.Rect(left, o.Y, right - left, bottom - o.Y), 22, attach, _darkGlass);
    }

    /// <summary>I：在安静与沉浸之间切换。</summary>
    private void ToggleSpaceMode()
    {
        // 安静 → 标准 → 沉浸 → 安静
        var mode = _host.Settings.Space switch
        {
            SpaceMode.Quiet => SpaceMode.Standard,
            SpaceMode.Standard => SpaceMode.Immersive,
            _ => SpaceMode.Quiet,
        };
        _host.UpdateSettings(s => s with { Space = mode });
        Space.Mode = mode;
        ShowToast(mode switch { SpaceMode.Quiet => "安静模式", SpaceMode.Standard => "标准模式", _ => "沉浸模式" });
    }

    // ───────────────────────── 操作层显隐 ─────────────────────────

    private void Reveal()
    {
        if (!_host.Store.State.Canvas.ControlsRevealed) ViewModel.Dispatch(new RevealControls());
        _hideControls.Stop();
        _hideControls.Start();
    }

    /// <summary>导航带打开，或侧边面板在窄窗口里盖住了画布：底部的进度、时间和控制不显示。</summary>
    private bool _bottomBlocked;
    private Windows.Foundation.Point? _pointer;

    /// <summary>
    /// 底部一组（进度线、时间、控制按钮）：有操作时淡入，无操作 2.5 秒后一起淡出；
    /// 高对比度下进度和时间常驻（控制按钮仍然跟随操作）。
    /// </summary>
    private void ApplyControlsRevealed(bool revealed)
    {
        bool info = !_bottomBlocked && (revealed || _accessibility.HighContrast);
        bool controls = !_bottomBlocked && revealed && _host.Store.State.Layer != Layer.Navigate;
        foreach (var e in new UIElement[] { Progress, TimeLabel })
        {
            e.Opacity = info ? 1 : 0;
            e.IsHitTestVisible = info;
        }
        RevealBar.Opacity = controls ? 1 : 0;
        RevealBar.IsHitTestVisible = controls;
    }

    /// <summary>鼠标停在底部一组上（例如正要拖动进度、调音量）：不要在它下面淡出。</summary>
    private bool PointerOverBottom()
    {
        if (_pointer is not { } p) return false;
        foreach (var e in new FrameworkElement[] { Progress, TimeLabel, RevealBar })
        {
            if (e.ActualWidth <= 0) continue;
            var r = e.TransformToVisual(Root).TransformBounds(new Windows.Foundation.Rect(0, 0, e.ActualWidth, e.ActualHeight));
            if (r.Contains(p)) return true;
        }
        return false;
    }

    private void OnRootPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        Reveal();
        var p = e.GetCurrentPoint(Root).Position;
        _pointer = p;
        Space.SetPointer(p);
    }

    private void OnRootPointerExited(object sender, PointerRoutedEventArgs e)
    {
        _pointer = null;
        Space.SetPointer(null);
    }

    // ───────────────────────── 鼠标：面板的打开与关闭 ─────────────────────────
    //
    // 规则：
    // · 目录（导航带）、资料、设置三个面板同一时间只开一个，打开一个会关掉其他的；
    // · Esc、鼠标侧键"后退"、点画面空白处，都关闭当前打开的面板；
    // · 点按钮、进度条、歌词等控件时执行它们自己的操作，不顺带关闭面板；
    // · 没有面板时，双击画面空白处切换全屏。

    private long _dismissedAt;

    private void InitPointer()
    {
        Root.Tapped += OnRootTapped;
        Root.DoubleTapped += OnRootDoubleTapped;
        // 侧键：按钮等控件会把 PointerPressed 标为已处理，所以要连已处理的事件一起接收。
        Root.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler(OnRootPointerPressed), handledEventsToo: true);
        // 卡片按下时的双击判断：卡片里的元素可能已经处理了按下事件，所以连已处理的一起接收。
        Ribbon.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler(OnRibbonPointerPressed), handledEventsToo: true);
    }

    /// <summary>当前有面板打开。</summary>
    private bool SurfaceOpen => SettingsSheet.Visibility == Visibility.Visible || _host.Store.State.Layer != Layer.Listen;

    /// <summary>关闭最上面的面板；没有面板时返回 false。</summary>
    private bool CloseTopSurface()
    {
        if (SettingsSheet.Visibility == Visibility.Visible) CloseSettings();
        else if (_host.Store.State.Layer == Layer.Navigate) ViewModel.Dispatch(new CancelNavigate());
        else if (_host.Store.State.Layer == Layer.Info) ViewModel.Dispatch(new ExitLayer());
        else return false;
        _dismissedAt = Environment.TickCount64;
        Root.Focus(FocusState.Programmatic);
        return true;
    }

    /// <summary>这次点击落在面板或可操作的控件上（而不是画面空白处）。</summary>
    private bool IsOnInteractive(object? source)
    {
        UIElement[] interactive = [SettingsSheet, InfoLayer, NavigateLayer, RevealBar, Progress, LyricsPanel, TitleBar, EmptyGuide];
        for (var d = source as DependencyObject; d is not null; d = VisualTreeHelper.GetParent(d))
        {
            if (d is ButtonBase or TextBox or ComboBox or Slider) return true;
            if (d is UIElement u && Array.IndexOf(interactive, u) >= 0) return true;
        }
        return false;
    }

    private void OnRootTapped(object sender, TappedRoutedEventArgs e)
    {
        if (e.Handled || IsOnInteractive(e.OriginalSource)) return;
        if (CloseTopSurface()) e.Handled = true;
    }

    private void OnRootDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (e.Handled || IsOnInteractive(e.OriginalSource) || SurfaceOpen) return;
        // 第一下刚关掉了面板：这不是"双击全屏"。
        if (Environment.TickCount64 - _dismissedAt < 600) return;
        ToggleFullScreen();
        e.Handled = true;
    }

    private void OnRootPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        var props = e.GetCurrentPoint(Root).Properties;
        if (!props.IsXButton1Pressed) return;
        // 侧键"后退"：关闭当前面板；没有面板时不做任何事（不切歌，避免误触）。
        if (CloseTopSurface()) e.Handled = true;
    }

    // ───────────────────────── 输入映射（§8.5） ─────────────────────────

    private void OnRootPreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        bool ctrl = IsDown(VirtualKey.Control);
        if (SettingsSheet.Visibility == Visibility.Visible)
        {
            // 设置打开时按键交给设置里的控件；Esc 和 Ctrl+, 关闭（下拉框展开时由下拉框自己处理 Esc）。
            if ((e.Key == VirtualKey.Escape || (ctrl && e.Key == (VirtualKey)188)) && !e.Handled)
            {
                CloseSettings();
                e.Handled = true;
            }
            return;
        }
        if (ctrl && e.Key == (VirtualKey)188) // Ctrl+,
        {
            OpenSettings();
            e.Handled = true;
            return;
        }

        var state = _host.Store.State;
        var layer = state.Layer;
        bool shift = IsDown(VirtualKey.Shift);
        bool inText = FocusedElement() is TextBox or AutoSuggestBox
            || (FocusedElement() as DependencyObject)?.GetType().Name == "TextBox";

        // 文本框获得焦点时，方向键、空格和字母交还给文本框；Esc / Enter / ↑↓ 仍用于导航。
        if (inText)
        {
            switch (e.Key)
            {
                case VirtualKey.Escape when layer == Layer.Navigate:
                    ViewModel.Dispatch(new CancelNavigate());
                    e.Handled = true;
                    break;
                case VirtualKey.Escape:
                    Root.Focus(FocusState.Programmatic);
                    e.Handled = true;
                    break;
                case VirtualKey.Enter when layer == Layer.Navigate:
                    ViewModel.Dispatch(new ConfirmFocus());
                    e.Handled = true;
                    break;
                case VirtualKey.Enter when layer == Layer.Info && FocusedElement() is TextBox { AcceptsReturn: false }:
                    Root.Focus(FocusState.Programmatic); // 失去焦点即保存
                    e.Handled = true;
                    break;
                case VirtualKey.Down when layer == Layer.Navigate:
                case VirtualKey.Up when layer == Layer.Navigate:
                    Root.Focus(FocusState.Programmatic);
                    e.Handled = true;
                    break;
            }
            return;
        }

        Reveal();
        AppAction? action = (e.Key, layer) switch
        {
            (VirtualKey.Space, _) => new TogglePause(),
            (VirtualKey.Left, _) when ctrl => new Previous(),
            (VirtualKey.Right, _) when ctrl => new Next(),
            (VirtualKey.Left, Layer.Listen) when shift => new SeekBy(TimeSpan.FromSeconds(-5)),
            (VirtualKey.Right, Layer.Listen) when shift => new SeekBy(TimeSpan.FromSeconds(5)),
            (VirtualKey.Left, Layer.Listen or Layer.Navigate) => new MoveFocus(-1),
            (VirtualKey.Right, Layer.Listen or Layer.Navigate) => new MoveFocus(+1),
            (VirtualKey.Up, Layer.Listen) => new EnterNavigate(),
            (VirtualKey.Up, Layer.Navigate) => new MoveFocusToGroup(-1),
            (VirtualKey.Up, Layer.Info) => new ExitLayer(),
            (VirtualKey.Down, Layer.Listen) => new EnterInfo(),
            (VirtualKey.Down, Layer.Navigate) => new MoveFocusToGroup(+1),
            (VirtualKey.Enter, Layer.Navigate) => new ConfirmFocus(),
            (VirtualKey.Escape, Layer.Navigate) => new CancelNavigate(),
            (VirtualKey.Escape, Layer.Info) => new ExitLayer(),
            ((VirtualKey)190, Layer.Navigate) => new ReturnFocusToPlaying(), // '.'
            (VirtualKey.R, _) when !ctrl => new ToggleRepeatOne(),
            (VirtualKey.M, Layer.Listen) => new AddMarker(_host.PositionOf(state.Playing?.Track).Position),
            _ => null,
        };

        if (action is not null)
        {
            ViewModel.Dispatch(action);
            e.Handled = true;
            return;
        }

        if (e.Key == VirtualKey.I && !ctrl && layer != Layer.Navigate)
        {
            ToggleSpaceMode();
            e.Handled = true;
            return;
        }

        if (e.Key == VirtualKey.L && !ctrl && layer == Layer.Listen)
        {
            ToggleLyrics();
            e.Handled = true;
            return;
        }

        if (e.Key == VirtualKey.S && !ctrl && layer == Layer.Listen)
        {
            Shuffle();
            e.Handled = true;
            return;
        }

        if ((e.Key == VirtualKey.F && ctrl) || e.Key == (VirtualKey)191) // Ctrl+F 或 '/'
        {
            if (layer == Layer.Info) return;
            if (layer == Layer.Listen) ViewModel.Dispatch(new EnterNavigate());
            DispatcherQueue.Run(() => SearchBox.Focus(FocusState.Keyboard));
            e.Handled = true;
        }
        else if (e.Key == VirtualKey.F11)
        {
            ToggleFullScreen();
            e.Handled = true;
        }
    }

    private object? FocusedElement() => Content.XamlRoot is { } root ? FocusManager.GetFocusedElement(root) : null;

    private static bool IsDown(VirtualKey key) =>
        InputKeyboardSource.GetKeyStateForCurrentThread(key).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);

    // ───────────────────────── 层 ─────────────────────────

    private void OnLayerChanged(Layer layer)
    {
        // 同一时间只开一个面板：进入目录或资料时关掉设置。
        if (layer != Layer.Listen && SettingsSheet.Visibility == Visibility.Visible) CloseSettings();
        NavigateLayer.Visibility = Ui.Visible(layer == Layer.Navigate);
        InfoLayer.Visibility = Ui.Visible(layer == Layer.Info);
        // 导航带打开时隐藏底部的进度、时间和控制（透明玻璃下面不要透出来）；侧边面板打开时画布让位
        UpdateGlass();
        // 进入导航：聆听画布保持可辨认，降低对比度。
        CanvasLayer.Opacity = layer == Layer.Navigate ? 0.45 : 1;

        if (layer == Layer.Navigate)
        {
            _suppressArrangementEvent = true;
            // 有网易云歌单（已同步）时才显示这个排列。
            PlaylistsItem.Visibility = Ui.Visible(!_host.Store.State.Library.Playlists.IsEmpty || _host.Store.State.Arrangement == Arrangement.Playlists);
            ArrangementBox.SelectedIndex = (int)_host.Store.State.Arrangement;
            _suppressArrangementEvent = false;
        }
        else
        {
            _suppressSearchEvent = true;
            SearchBox.Text = "";
            _suppressSearchEvent = false;
            if (FocusedElement() is not Button) Root.Focus(FocusState.Programmatic);
        }
        if (layer == Layer.Info) FillInfo();
    }

    /// <summary>上一次用鼠标选中卡片的时刻（0 表示没有）。</summary>
    private long _pointerSelectedAt;

    private void BringFocusIntoView(int index)
    {
        if (index < 0 || index >= ViewModel.RibbonItems.Count) return;
        // 用鼠标点选的卡片本来就在鼠标下面：不滚动，否则双击的第二下会落到别的卡片上。
        if (_pointerSelectedAt != 0 && Environment.TickCount64 - _pointerSelectedAt < 400) { _pointerSelectedAt = 0; return; }
        DispatcherQueue.Run(() =>
        {
            var el = Ribbon.GetOrCreateElement(index);
            el.UpdateLayout();
            el.StartBringIntoView(new BringIntoViewOptions { HorizontalAlignmentRatio = 0.5, AnimationDesired = !ReduceMotion });
        });
    }

    private void OnRibbonWheel(object sender, PointerRoutedEventArgs e)
    {
        // 滚轮只移动浏览焦点，不切歌。
        int delta = e.GetCurrentPoint(RibbonScroller).Properties.MouseWheelDelta;
        if (delta == 0) return;
        ViewModel.Dispatch(new MoveFocus(delta > 0 ? -1 : 1));
        e.Handled = true;
    }

    private RibbonItem? ItemFrom(object? source) => CardFrom(source).Item;

    /// <summary>
    /// 点到的是哪张卡片（卡片元素和它的数据）。卡片模板用 x:Bind，ItemsRepeater 不会给卡片设置 DataContext，
    /// 所以往上找到 ItemsRepeater 的直接子元素，再按它在列表中的位置取数据。
    /// </summary>
    private (FrameworkElement? Element, RibbonItem? Item) CardFrom(object? source)
    {
        for (var d = source as DependencyObject; d is not null; d = VisualTreeHelper.GetParent(d))
        {
            if (VisualTreeHelper.GetParent(d) is not { } parent || !ReferenceEquals(parent, Ribbon)) continue;
            if (d is not UIElement card) break;
            int index = Ribbon.GetElementIndex(card);
            return index >= 0 && index < ViewModel.RibbonItems.Count ? (card as FrameworkElement, ViewModel.RibbonItems[index]) : (null, null);
        }
        return (null, null);
    }

    // ───────────────────────── 目录带：鼠标点击 ─────────────────────────
    //
    // 双击不依赖 WinUI 的 DoubleTapped 手势（会被卡片里的元素和焦点变化打断），
    // 而是在按下鼠标时自己判断：同一张卡片、系统双击时间内、位置几乎不变的第二次按下就是双击。

    /// <summary>上一次在卡片上按下左键：哪一首、第几张、什么时候、在哪里。</summary>
    private (TrackId Track, int Index, long At, Windows.Foundation.Point Position)? _lastPress;
    /// <summary>双击已经处理过：在这个时刻之前到来的 Tapped / DoubleTapped 忽略。</summary>
    private long _ignoreTapsUntil;

    [System.Runtime.InteropServices.LibraryImport("user32.dll")]
    private static partial uint GetDoubleClickTime();

    /// <summary>用鼠标选中一张卡片（不滚动目录带）。</summary>
    private void SelectByPointer(RibbonItem item)
    {
        if (_host.Store.State.Focus is not { } f || item.Index == f.Index) return;
        _pointerSelectedAt = Environment.TickCount64;
        ViewModel.Dispatch(new MoveFocus(item.Index - f.Index));
    }

    private void PlayByPointer(RibbonItem item)
    {
        _dismissedAt = Environment.TickCount64;
        SelectByPointer(item);
        ViewModel.Dispatch(new ConfirmFocus());
    }

    private void OnRibbonPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        var point = e.GetCurrentPoint(Ribbon);
        if (!point.Properties.IsLeftButtonPressed || _host.Store.State.Layer != Layer.Navigate) return;
        if (ItemFrom(e.OriginalSource) is not { } item) { _lastPress = null; return; }

        long now = Environment.TickCount64;
        if (_lastPress is { } last && last.Track == item.Track && last.Index == item.Index
            && now - last.At <= GetDoubleClickTime()
            && Math.Abs(point.Position.X - last.Position.X) <= 8 && Math.Abs(point.Position.Y - last.Position.Y) <= 8)
        {
            _lastPress = null;
            _ignoreTapsUntil = now + 600;
            PlayByPointer(item);
            e.Handled = true;
            return;
        }
        _lastPress = (item.Track, item.Index, now, point.Position);
    }

    /// <summary>单击：选中这张卡片；单击已经选中的卡片：播放它（选中的卡片上有 ▶ 提示）。</summary>
    private void OnRibbonTapped(object sender, TappedRoutedEventArgs e)
    {
        e.Handled = true;
        if (Environment.TickCount64 < _ignoreTapsUntil) return;
        if (_host.Store.State.Layer != Layer.Navigate || ItemFrom(e.OriginalSource) is not { } item || _host.Store.State.Focus is not { } focus) return;
        if (item.Index == focus.Index)
        {
            _dismissedAt = Environment.TickCount64; // 目录会关闭：紧接着的第二下不要当成"双击空白处全屏"
            ViewModel.Dispatch(new ConfirmFocus());
        }
        else SelectByPointer(item);
        Root.Focus(FocusState.Programmatic);
    }

    /// <summary>双击手势：按下时已经处理过了；只在按下判断没赶上时（例如触摸屏）作为后备。</summary>
    private void OnRibbonDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        e.Handled = true;
        if (Environment.TickCount64 < _ignoreTapsUntil) return;
        if (_host.Store.State.Layer != Layer.Navigate || ItemFrom(e.OriginalSource) is not { } item) return;
        _ignoreTapsUntil = Environment.TickCount64 + 600;
        PlayByPointer(item);
    }


    /// <summary>右键：选中这张卡片并弹出菜单（播放、收藏、打开文件位置）。</summary>
    private void OnRibbonRightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        var (element, item) = CardFrom(e.OriginalSource);
        if (element is null || item is null || _host.Store.State.Focus is not { } focus) return;
        e.Handled = true;
        SelectByPointer(item);

        var track = _host.Store.State.Library.Find(item.Track);
        var menu = new MenuFlyout();
        void Add(string text, string glyph, Action action, bool enabled = true)
        {
            var mi = new MenuFlyoutItem { Text = text, Icon = new FontIcon { Glyph = glyph }, IsEnabled = enabled };
            mi.Click += (_, _) => action();
            menu.Items.Add(mi);
        }

        bool isPlaying = _host.Store.State.Playing?.Track == item.Track;
        Add(isPlaying ? "正在播放" : "播放", "\uE768", () => ViewModel.Dispatch(new ConfirmFocus()), enabled: item.Available && !isPlaying);
        if (track is not null && !track.Id.IsRemote)
        {
            bool favorite = _host.Store.State.Library.User(track.Id).Favorite;
            Add(favorite ? "取消收藏" : "收藏", favorite ? "\uE735" : "\uE734", () => ViewModel.Dispatch(new ToggleFavorite(track.Id)));
            menu.Items.Add(new MenuFlyoutSeparator());
            Add("在资源管理器中显示", "\uE838", () => Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{track.Path}\"") { UseShellExecute = false }),
                enabled: track.Available);
        }
        else if (track is not null)
        {
            menu.Items.Add(new MenuFlyoutSeparator());
            Add("在网易云音乐中打开", "\uE774", () => Process.Start(new ProcessStartInfo($"https://music.163.com/song?id={-track.Id.Value}") { UseShellExecute = true }));
        }
        menu.ShowAt(element, e.GetPosition(element));
    }

    private void OnSearchTextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        if (_suppressSearchEvent || args.Reason != AutoSuggestionBoxTextChangeReason.UserInput) return;
        _searchDebounce.Stop();
        _searchDebounce.Start();
    }

    private void OnArrangementChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressArrangementEvent || ArrangementBox.SelectedItem is not ComboBoxItem { Tag: string tag }) return;
        ViewModel.Dispatch(new SetArrangement(Enum.Parse<Arrangement>(tag)));
        Root.Focus(FocusState.Programmatic);
    }

    // ───────────────────────── 信息层 ─────────────────────────

    private void FillInfo()
    {
        var s = _host.Store.State;
        if (s.PlayingTrack is not { } t) return;
        var db = _host.Library.Database;
        if (t.Id.IsRemote)
        {
            // 在线曲目：没有本地文件、标签、备注和标记。
            InfoPath.Text = $"网易云音乐 · 在线播放（{(_host.Settings.NetEaseQuality switch
            {
                Solisten.Integrations.NetEase.Api.StreamQuality.Standard => "标准",
                Solisten.Integrations.NetEase.Api.StreamQuality.Higher => "较高",
                Solisten.Integrations.NetEase.Api.StreamQuality.Lossless => "无损",
                _ => "极高",
            })}音质，按账号权限）";
            InfoFormat.Text = $"{Rendering.Progress.ProgressLayout.FormatTime(t.Meta.Duration)} · {t.Meta.Album}";
            InfoTags.Text = "";
            InfoNote.Text = "";
            FillMarkers(t.Id);
            FavoriteButton.IsChecked = false;
            return;
        }
        InfoPath.Text = t.Path;
        long size = 0;
        try { size = new FileInfo(t.Path).Length; } catch (IOException) { }
        var f = t.Format;
        var user = s.Library.User(t.Id);
        InfoFormat.Text = $"{f.Codec.ToUpperInvariant()} · {f.SampleRate / 1000.0:0.#} kHz · {(f.BitDepth > 0 ? $"{f.BitDepth} bit · " : "")}{f.Channels} 声道\n"
            + $"{Rendering.Progress.ProgressLayout.FormatTime(t.Meta.Duration)} · {size / 1048576.0:0.0} MB · 播放 {user.PlayCount} 次";
        InfoTags.Text = string.Join(", ", db.GetTags(t.Id));
        InfoNote.Text = db.GetNote(t.Id) ?? "";
        FillMarkers(t.Id);
        FavoriteButton.IsChecked = user.Favorite;
    }

    private void FillMarkers(TrackId track)
    {
        var markers = _host.Library.Database.GetMarkers(track);
        InfoMarkersHint.Text = markers.Count == 0 ? "按 M 在当前时间打标记" : "时间标记";
        InfoMarkerList.ItemsSource = markers.Select(m => new MarkerRow(m.Id, m.Track, m.Position, m.Label)).ToList();
    }

    private void OnMarkerSeekClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is MarkerRow m && _host.Store.State.Playing?.Track == m.Track)
            ViewModel.Dispatch(new Seek(m.Position));
    }

    private void OnMarkerLabelLostFocus(object sender, RoutedEventArgs e)
    {
        if (sender is TextBox { DataContext: MarkerRow m } box && box.Text.Trim() != (m.Label ?? ""))
            ViewModel.Dispatch(new RenameMarker(m.Track, m.Id, box.Text));
    }

    private void OnMarkerDeleteClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is MarkerRow m) ViewModel.Dispatch(new DeleteMarker(m.Track, m.Id));
    }

    private void OnShuffleClick(object sender, RoutedEventArgs e)
    {
        Shuffle();
        Root.Focus(FocusState.Programmatic);
    }

    /// <summary>§3.2 范围内随机：打乱当前快照（当前歌排在最前），之后按新顺序续播。</summary>
    private void Shuffle()
    {
        if (_host.Store.State.Playing is not { } p) return;
        if (p.ContextSnapshot.Count < 2)
        {
            ShowToast("当前范围只有一首歌");
            return;
        }
        ViewModel.Dispatch(new ShuffleSnapshot(Random.Shared.Next()));
        ShowToast($"已在「{p.ContextSnapshot.Groups.FirstOrDefault()?.DisplayName ?? "当前范围"}」内随机排列 {p.ContextSnapshot.Count} 首");
    }

    private void OnTagsLostFocus(object sender, RoutedEventArgs e)
    {
        if (_host.Store.State.Playing?.Track is not { } id) return;
        var tags = InfoTags.Text.Split([',', '，', ';', '；'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        ViewModel.Dispatch(new SetTags(id, [.. tags]));
    }

    private void OnNoteLostFocus(object sender, RoutedEventArgs e)
    {
        if (_host.Store.State.Playing?.Track is { } id) ViewModel.Dispatch(new SetNote(id, InfoNote.Text));
    }

    private void OnRevealInExplorer(object sender, RoutedEventArgs e)
    {
        if (_host.Store.State.PlayingTrack is not { } t) return;
        if (t.Id.IsRemote)
        {
            // 在线曲目没有本地文件：在浏览器中打开网易云的歌曲页。
            Process.Start(new ProcessStartInfo($"https://music.163.com/song?id={-t.Id.Value}") { UseShellExecute = true });
            return;
        }
        Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{t.Path}\"") { UseShellExecute = false });
    }

    private void OnFavoriteClick(object sender, RoutedEventArgs e)
    {
        if (_host.Store.State.Playing?.Track is { } id) ViewModel.Dispatch(new ToggleFavorite(id));
    }

    private void OnIdentityTapped(object sender, TappedRoutedEventArgs e) => Reveal();

    // ───────────────────────── 按钮 ─────────────────────────

    private void OnPlayClick(object sender, RoutedEventArgs e) { ViewModel.Dispatch(new TogglePause()); Root.Focus(FocusState.Programmatic); }
    private void OnPreviousClick(object sender, RoutedEventArgs e) { ViewModel.Dispatch(new Previous()); Root.Focus(FocusState.Programmatic); }
    private void OnNextClick(object sender, RoutedEventArgs e) { ViewModel.Dispatch(new Next()); Root.Focus(FocusState.Programmatic); }
    private void OnRepeatClick(object sender, RoutedEventArgs e) { ViewModel.Dispatch(new ToggleRepeatOne()); Root.Focus(FocusState.Programmatic); }
    private void OnNavigateClick(object sender, RoutedEventArgs e)
    {
        ViewModel.Dispatch(_host.Store.State.Layer == Layer.Navigate ? new CancelNavigate() : new EnterNavigate());
        Root.Focus(FocusState.Programmatic);
    }

    private void OnNavigateCloseClick(object sender, RoutedEventArgs e) { ViewModel.Dispatch(new CancelNavigate()); Root.Focus(FocusState.Programmatic); }
    private void OnReturnToPlayingClick(object sender, RoutedEventArgs e) { ViewModel.Dispatch(new ReturnFocusToPlaying()); Root.Focus(FocusState.Programmatic); }
    private void OnInfoClick(object sender, RoutedEventArgs e) => ViewModel.Dispatch(_host.Store.State.Layer == Layer.Info ? new ExitLayer() : new EnterInfo());
    private void OnInfoCloseClick(object sender, RoutedEventArgs e) { ViewModel.Dispatch(new ExitLayer()); Root.Focus(FocusState.Programmatic); }

    private void OnVolumeChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (_suppressVolumeEvent) return;
        _host.Engine.Volume = VolumeCurve.FromSlider(e.NewValue);
        _host.UpdateSettings(s => s with { Volume = e.NewValue });
    }

    private async Task<string?> PickFolderAsync()
    {
        var picker = new Windows.Storage.Pickers.FolderPicker();
        picker.FileTypeFilter.Add("*");
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
        var folder = await picker.PickSingleFolderAsync();
        return folder?.Path;
    }

    private async void OnFolderClick(object sender, RoutedEventArgs e)
    {
        if (await PickFolderAsync() is not { } path) return;
        ScanText.Text = "正在读取…";
        ShowToast($"正在导入 {path}");
        await _host.AddFolderAsync(path);
        ScanText.Text = "";
        Root.Focus(FocusState.Programmatic);
    }

    // ───────────────────────── 设置 ─────────────────────────

    /// <summary>设置按钮：开着就关，关着就开。</summary>
    private void OnSettingsClick(object sender, RoutedEventArgs e)
    {
        if (SettingsSheet.Visibility == Visibility.Visible) CloseSettings();
        else OpenSettings();
    }

    private void OpenSettings()
    {
        if (_host.Store.State.Layer != Layer.Listen) ViewModel.Dispatch(new ExitLayer());
        SettingsSheet.Refresh();
        SettingsSheet.Visibility = Visibility.Visible;
        UpdateGlass();
        DispatcherQueue.Run(SettingsSheet.FocusFirst);
    }

    private void CloseSettings()
    {
        SettingsSheet.Visibility = Visibility.Collapsed;
        UpdateGlass();
        Root.Focus(FocusState.Programmatic);
    }

    private void ShowToast(string message)
    {
        ToastText.Text = message;
        Toast.Opacity = 1;
        _hideToast.Stop();
        _hideToast.Start();
    }
}
