using System.Numerics;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Solisten.Core.Model;
using Windows.UI;

namespace Solisten.Rendering.Progress;

/// <summary>进度线要绘制的内容快照。</summary>
public sealed record ProgressModel(
    TrackId? Track,
    TimeSpan Duration,
    Palette Palette,
    IReadOnlyList<TimeSpan> Markers,
    bool HighContrast = false,
    Color? HighContrastForeground = null,
    Color? HighContrastAccent = null)
{
    public static readonly ProgressModel Empty = new(null, TimeSpan.Zero, Palette.Neutral, []);
}

/// <summary>
/// 极简播放进度：放在播放控制上方的一条 3px 细线。已播放部分用亮色，未播放部分低透明度；
/// 鼠标悬停时变粗并显示时间气泡，支持点击或拖动跳转（松手后提交）。不显示波形。
/// </summary>
public sealed partial class ProgressLine : UserControl
{
    private static readonly TimeSpan PlayingInterval = TimeSpan.FromMilliseconds(100);

    private readonly CanvasControl _canvas;
    private ProgressModel _model = ProgressModel.Empty;
    private Func<TrackId?, (TimeSpan Position, bool Running)>? _position;
    private bool _playing;
    private bool _animating;
    private TimeSpan _lastFrame;

    private bool _pointerInside;
    private float _hover;          // 0..1，悬停的过渡
    private float _hoverX = float.NaN;
    private bool _dragging;
    private float _dragX;
    private CanvasTextFormat? _bubbleFormat;

    public ProgressLine()
    {
        _canvas = new CanvasControl { ClearColor = Color.FromArgb(0, 0, 0, 0) };
        _canvas.Draw += (s, e) => OnDraw(s, e.DrawingSession);
        _canvas.SizeChanged += (_, _) => _canvas.Invalidate();
        Content = _canvas;
        IsTabStop = false;
        Background = new SolidColorBrush(Color.FromArgb(0, 0, 0, 0)); // 整块区域可点击

        PointerEntered += (_, _) => { _pointerInside = true; UpdateRendering(); };
        PointerExited += (_, _) => { _pointerInside = false; if (!_dragging) _hoverX = float.NaN; UpdateRendering(); };
        PointerMoved += OnPointerMoved;
        PointerPressed += OnPointerPressed;
        PointerReleased += OnPointerReleased;
        PointerCaptureLost += (_, _) => { _dragging = false; UpdateRendering(); };
        Loaded += (_, _) => UpdateRendering();
        Unloaded += (_, _) =>
        {
            StopRendering();
            _canvas.RemoveFromVisualTree();
        };
    }

    /// <summary>松手（或点击）后请求 seek。</summary>
    public event Action<TimeSpan>? SeekRequested;

    /// <summary>拖动过程中的预览位置（null 表示结束预览）。</summary>
    public event Action<TimeSpan?>? SeekPreview;

    public ProgressModel Model
    {
        get => _model;
        set { _model = value; _canvas.Invalidate(); }
    }

    /// <summary>音频时钟读取器：给定播放锚点，返回位置与是否在走。</summary>
    public Func<TrackId?, (TimeSpan Position, bool Running)>? PositionSource
    {
        get => _position;
        set => _position = value;
    }

    /// <summary>播放中每 100 ms 重绘一次（细线移动很慢）；暂停后只在需要时重绘。</summary>
    public void SetPlaying(bool playing, bool visible)
    {
        _playing = playing && visible;
        UpdateRendering();
        if (visible) _canvas.Invalidate();
    }

    public void Invalidate() => _canvas.Invalidate();

    private bool WantsFrames => _playing || _dragging || (_pointerInside ? _hover < 1 : _hover > 0);

    private void UpdateRendering()
    {
        if (WantsFrames && IsLoaded) StartRendering();
        _canvas.Invalidate();
    }

    private void StartRendering()
    {
        if (_animating) return;
        _animating = true;
        CompositionTarget.Rendering += OnRendering;
    }

    private void StopRendering()
    {
        if (!_animating) return;
        _animating = false;
        CompositionTarget.Rendering -= OnRendering;
    }

    private void OnRendering(object? sender, object e)
    {
        var t = ((RenderingEventArgs)e).RenderingTime;
        float dt = (float)Math.Clamp((t - _lastFrame).TotalSeconds, 0, 0.1);
        bool hoverAnimating = _pointerInside ? _hover < 1 : _hover > 0;
        if (hoverAnimating) _hover = Math.Clamp(_hover + (_pointerInside ? dt : -dt) / 0.15f, 0, 1);

        if (!WantsFrames)
        {
            StopRendering();
            _canvas.Invalidate();
            return;
        }
        // 悬停过渡和拖动跟随刷新率；单纯播放时节流
        if (!hoverAnimating && !_dragging && t - _lastFrame < PlayingInterval) return;
        _lastFrame = t;
        _canvas.Invalidate();
    }

    // ───────────────────────── 交互 ─────────────────────────

    private (float Left, float Width) Span() => ProgressLayout.Span((float)ActualWidth);

    private TimeSpan TimeAtX(float x)
    {
        var (left, width) = Span();
        return ProgressLayout.TimeAt(x, _model.Duration, left, width);
    }

    private void OnPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        var p = e.GetCurrentPoint(this).Position;
        _hoverX = (float)p.X;
        if (_dragging)
        {
            _dragX = (float)p.X;
            SeekPreview?.Invoke(TimeAtX(_dragX));
        }
        ProtectedCursor = InputSystemCursor.Create(InputSystemCursorShape.Hand);
        _canvas.Invalidate();
    }

    private void OnPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        var p = e.GetCurrentPoint(this);
        if (!p.Properties.IsLeftButtonPressed || _model.Duration <= TimeSpan.Zero) return;
        _dragging = true;
        _dragX = (float)p.Position.X;
        CapturePointer(e.Pointer);
        UpdateRendering();
        SeekPreview?.Invoke(TimeAtX(_dragX));
        e.Handled = true;
    }

    private void OnPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!_dragging) return;
        _dragging = false;
        ReleasePointerCapture(e.Pointer);
        var t = TimeAtX((float)e.GetCurrentPoint(this).Position.X);
        SeekPreview?.Invoke(null);
        SeekRequested?.Invoke(t);
        UpdateRendering();
        e.Handled = true;
    }

    // ───────────────────────── 绘制 ─────────────────────────

    private void OnDraw(CanvasControl sender, CanvasDrawingSession ds)
    {
        var m = _model;
        float w = (float)sender.Size.Width, h = (float)sender.Size.Height;
        if (w < 8 || h < 8) return;
        var (left, width) = ProgressLayout.Span(w);
        float y = ProgressLayout.BubbleRoom + (h - ProgressLayout.BubbleRoom) / 2;
        float thick = ProgressLayout.ThicknessAt(_hover);

        var (pos, _) = _position?.Invoke(m.Track) ?? (TimeSpan.Zero, false);
        if (_dragging) pos = ProgressLayout.TimeAt(_dragX, m.Duration, left, width);
        float x = ProgressLayout.XAt(pos, m.Duration, left, width);

        var fg = m.HighContrast && m.HighContrastForeground is { } hf ? hf : ToColor(m.Palette.TextOnBase);
        var accent = m.HighContrast && m.HighContrastAccent is { } ha ? ha : ToColor(m.Palette.AccentOnBase);
        var rest = WithAlpha(fg, m.HighContrast ? 0.6f : 0.22f + 0.08f * _hover);

        var style = new Microsoft.Graphics.Canvas.Geometry.CanvasStrokeStyle
        {
            StartCap = Microsoft.Graphics.Canvas.Geometry.CanvasCapStyle.Round,
            EndCap = Microsoft.Graphics.Canvas.Geometry.CanvasCapStyle.Round,
        };
        ds.DrawLine(left, y, left + width, y, rest, thick, style);
        if (x > left + 0.5f) ds.DrawLine(left, y, x, y, accent, thick, style);

        // 标记：线下方的小点
        foreach (var mk in m.Markers)
            ds.FillCircle(ProgressLayout.XAt(mk, m.Duration, left, width), y + thick / 2 + 5, 1.8f, WithAlpha(fg, 0.7f));

        // 悬停时显示播放头和时间气泡
        if (_hover > 0.01f)
        {
            ds.FillCircle(x, y, thick * 0.9f * _hover + 0.5f, accent);
            float hx = _dragging ? _dragX : _hoverX;
            if (!float.IsNaN(hx) && m.Duration > TimeSpan.Zero)
            {
                hx = Math.Clamp(hx, left, left + width);
                var t = ProgressLayout.TimeAt(hx, m.Duration, left, width);
                _bubbleFormat ??= new CanvasTextFormat { FontSize = 12, FontFamily = "Segoe UI Variable Text", HorizontalAlignment = CanvasHorizontalAlignment.Center };
                float bw = 46, bh = 20, by = y - thick / 2 - bh - 6;
                byte a = (byte)(140 * _hover);
                ds.FillRoundedRectangle(hx - bw / 2, by, bw, bh, 6, 6, Color.FromArgb(a, 0, 0, 0));
                ds.DrawText(ProgressLayout.FormatTime(t), new Vector2(hx, by + 2), Color.FromArgb((byte)(255 * _hover), 255, 255, 255), _bubbleFormat);
            }
        }
    }

    private static Color ToColor(Rgb c) => Color.FromArgb(255, c.R, c.G, c.B);

    private static Color WithAlpha(Color c, float a) => Color.FromArgb((byte)Math.Clamp(a * c.A, 0, 255), c.R, c.G, c.B);
}
