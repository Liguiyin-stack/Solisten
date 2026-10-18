using System.ComponentModel;
using System.Diagnostics;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
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

        _hideControls = DispatcherQueue.CreateTimer();
        _hideControls.Interval = ControlsIdle;
        _hideControls.IsRepeating = false;
        _hideControls.Tick += (_, _) => ViewModel.Dispatch(new HideControls());

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

    private void OnVisualsChanged(TrackVisuals v, bool transition)
    {
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
        double side = CoverSide(Stage.ActualWidth, Stage.ActualHeight);
        Space.SetStage(
            (float)((origin.X + Stage.ActualWidth / 2) / Root.ActualWidth),
            (float)((origin.Y + Stage.ActualHeight / 2) / Root.ActualHeight),
            (float)side);
    }

    internal static double CoverSide(double windowWidth, double stageHeight) =>
        Math.Max(80, Math.Min(Math.Clamp(windowWidth * 0.26, windowWidth * 0.22, windowWidth * 0.30), stageHeight * 0.62));

    private const double SidePanelWidth = 420;
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
        SetGlassFor("settings", SettingsSheet, GlassAttach.Right | GlassAttach.Bottom, hc);
        SetGlassFor("info", InfoLayer, GlassAttach.Right | GlassAttach.Bottom, hc);

        bool roomy = Root.ActualWidth - SidePanelWidth >= MinCanvasWidth;
        bool side = SidePanelOpen;
        var margin = new Thickness(0, 40, side && roomy ? SidePanelWidth : 0, 0);
        if (CanvasLayer.Margin != margin) CanvasLayer.Margin = margin;

        bool hideBottom = _host.Store.State.Layer == Layer.Navigate || (side && !roomy);
        foreach (var e in new UIElement[] { Progress, TimeLabel })
        {
            e.Opacity = hideBottom ? 0 : 1;
            e.IsHitTestVisible = !hideBottom;
        }
        if (hideBottom)
        {
            RevealBar.Opacity = 0;
            RevealBar.IsHitTestVisible = false;
        }
        else
        {
            ApplyControlsRevealed(_host.Store.State.Canvas.ControlsRevealed);
        }
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

    private void ApplyControlsRevealed(bool revealed)
    {
        if (_host.Store.State.Layer == Layer.Navigate) revealed = false;
        double o = revealed ? 1 : 0;
        RevealBar.Opacity = o;
        RevealBar.IsHitTestVisible = revealed;
    }

    private void OnRootPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        Reveal();
        Space.SetPointer(e.GetCurrentPoint(Root).Position);
    }

    private void OnRootPointerExited(object sender, PointerRoutedEventArgs e) => Space.SetPointer(null);

    // ───────────────────────── 输入映射（§8.5） ─────────────────────────

    private void OnRootPreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        bool ctrl = IsDown(VirtualKey.Control);
        if (SettingsSheet.Visibility == Visibility.Visible)
        {
            // 设置打开时按键交给设置里的控件；Esc 关闭（下拉框展开时由下拉框自己处理 Esc）。
            if (e.Key == VirtualKey.Escape && !e.Handled)
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
        NavigateLayer.Visibility = Ui.Visible(layer == Layer.Navigate);
        InfoLayer.Visibility = Ui.Visible(layer == Layer.Info);
        // 导航带打开时隐藏底部的进度、时间和控制（透明玻璃下面不要透出来）；侧边面板打开时画布让位
        UpdateGlass();
        // 进入导航：聆听画布保持可辨认，降低对比度。
        CanvasLayer.Opacity = layer == Layer.Navigate ? 0.45 : 1;

        if (layer == Layer.Navigate)
        {
            _suppressArrangementEvent = true;
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

    private void BringFocusIntoView(int index)
    {
        if (index < 0 || index >= ViewModel.RibbonItems.Count) return;
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

    private RibbonItem? ItemFrom(object? source)
    {
        for (var d = source as DependencyObject; d is not null; d = VisualTreeHelper.GetParent(d))
        {
            if (d is FrameworkElement { DataContext: RibbonItem item }) return item;
            if (ReferenceEquals(d, Ribbon)) break;
        }
        return null;
    }

    private void OnRibbonTapped(object sender, TappedRoutedEventArgs e)
    {
        if (ItemFrom(e.OriginalSource) is not { } item) return;
        var focus = _host.Store.State.Focus;
        if (focus is not null) ViewModel.Dispatch(new MoveFocus(item.Index - focus.Index));
        Root.Focus(FocusState.Programmatic);
    }

    private void OnRibbonDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (ItemFrom(e.OriginalSource) is null) return;
        ViewModel.Dispatch(new ConfirmFocus());
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
        if (_host.Store.State.PlayingTrack is { } t)
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
    private void OnNavigateClick(object sender, RoutedEventArgs e) { ViewModel.Dispatch(new EnterNavigate()); Root.Focus(FocusState.Programmatic); }
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

    private void OnSettingsClick(object sender, RoutedEventArgs e) => OpenSettings();

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
