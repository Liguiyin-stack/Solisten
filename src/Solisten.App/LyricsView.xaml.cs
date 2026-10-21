using System.Diagnostics;
using System.Numerics;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Effects;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.Graphics.Canvas.UI;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Solisten.Core.Model;
using Windows.Foundation;
using Windows.UI;

namespace Solisten.App;

/// <summary>
/// 整屏歌词，视觉接近 Apple Music：
/// <list type="bullet">
/// <item>当前行清晰、不透明、略微放大，停在视口约 38% 的高度；其余行按与当前行的距离逐渐变淡、变模糊；</item>
/// <item>上下边缘按位置渐隐到完全透明，不会出现被硬生生截断的半行；</item>
/// <item>换行时下方各行依次延迟一点开始移动，形成波浪式的滚动；</item>
/// <item>滚轮翻看时去掉模糊、所有行变清楚，停手 3 秒后回到当前行；点击一行跳到那里播放。</item>
/// </list>
/// 用 Win2D 绘制（XAML 文本不能逐行模糊）。只在有动画时出帧，静止后不消耗 GPU。
/// </summary>
public sealed partial class LyricsView : UserControl
{
    // —— 版式 ——
    private const float PadX = 14;
    private const float RowGap = 26;
    private const float TranslationGap = 6;
    private const float MainFontSize = 30;
    private const float TranslationFontSize = 19;

    // —— 位置与渐隐 ——
    /// <summary>当前行中心在视口中的高度比例（从上往下）：略高于正中。</summary>
    private const float FocusRatio = 0.38f;
    /// <summary>顶部这一段（占视口高度的比例）内逐渐渐隐到透明。</summary>
    private const float TopFade = 0.24f;
    private const float BottomFade = 0.30f;

    // —— 远近 ——
    private const float InactiveScale = 0.965f;
    /// <summary>相邻一行只是轻微柔化，再往外每行加一点，最多到 <see cref="MaxBlur"/>。</summary>
    private const float FirstBlur = 0.45f;
    private const float BlurPerLine = 0.7f;
    private const float MaxBlur = 3.6f;

    // —— 动画 ——
    private const double ScrollSeconds = 0.62;
    private const double StaggerStep = 0.045;
    private const double StaggerMax = 0.40;
    private const double ManualScrollSeconds = 0.22;
    private const double ManualHoldSeconds = 3.0;
    private const double SmoothSeconds = 0.22;

    /// <summary>歌词比声音提前一点切换，读起来不拖沓。</summary>
    private static readonly TimeSpan Lead = TimeSpan.FromMilliseconds(150);

    private sealed class Row(LyricLine line)
    {
        public LyricLine Line { get; } = line;
        public CanvasTextLayout? Main, Translation;
        public float Top, Height;

        // 每一行自己的滚动动画（换行时下方的行晚一点开始，形成波浪）
        public float From, To;
        public double Start, Duration;

        // 平滑显示的外观
        public float Opacity = -1, Blur, Scale = 1;

        public CanvasCommandList? Image;
        public Color ImageColor;
        public GaussianBlurEffect? BlurEffect;

        // 上一帧画在哪里（点击和悬停用）
        public float DrawTop = float.NaN, DrawBottom = float.NaN;

        public void DisposeResources()
        {
            Main?.Dispose();
            Translation?.Dispose();
            Image?.Dispose();
            BlurEffect?.Dispose();
            Main = Translation = null;
            Image = null;
            BlurEffect = null;
        }
    }

    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly List<Row> _rows = [];
    private Lyrics? _lyrics;
    private int _active = -1;
    private int _hover = -1;
    private bool _manual;
    private double _manualUntil;
    private float _layoutWidth = -1;
    private bool _needsSnap;
    private bool _looping;
    private double _lastFrame;
    private CanvasTextFormat? _mainFormat, _translationFormat;

    public LyricsView()
    {
        InitializeComponent();
        SizeChanged += (_, _) =>
        {
            _needsSnap = true;
            Canvas.Invalidate();
        };
        Unloaded += (_, _) => StopLoop();
    }

    /// <summary>文字颜色（跟随画布的主色，由主窗口传入同一支画笔）。</summary>
    public Brush? Ink { get; set; }

    /// <summary>减少动态效果：不做滚动动画（模糊和渐隐保留，它们不是动效）。</summary>
    public bool ReduceMotion { get; set; }

    /// <summary>用户点击了某一行（同步歌词）。</summary>
    public event Action<TimeSpan>? SeekRequested;

    public Lyrics? Lyrics => _lyrics;

    private double Now => _clock.Elapsed.TotalSeconds;

    // ───────────────────────── 内容 ─────────────────────────

    public void SetLyrics(Lyrics? lyrics)
    {
        foreach (var r in _rows) r.DisposeResources();
        _rows.Clear();
        _lyrics = lyrics is { IsEmpty: false } ? lyrics : null;
        _active = -1;
        _hover = -1;
        _manual = false;
        _layoutWidth = -1;
        _needsSnap = true;
        if (_lyrics is not null)
            foreach (var line in _lyrics.Lines) _rows.Add(new Row(line));
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(Canvas, _lyrics is null ? "" : "歌词");
        Canvas.Invalidate();
    }

    /// <summary>按播放位置更新当前行（主窗口按音频时钟调用）。没有时间轴的歌词不做任何事。</summary>
    public void Update(TimeSpan position)
    {
        if (_lyrics is not { IsSynced: true } lyrics || _rows.Count == 0) return;
        int index = lyrics.IndexAt(position + Lead);
        if (index == _active) return;
        int old = _active;
        _active = index;
        if (index >= 0) Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(Canvas, lyrics.Lines[index].Text);
        // 顺着播放往下走一两行时做波浪滚动；跳转（拖进度条、点击歌词）时整体移动。
        bool stagger = old >= 0 && index > old && index - old <= 3;
        if (!_manual && _layoutWidth > 0) Retarget(stagger, ScrollSeconds);
        EnsureLoop();
    }

    // ───────────────────────── 版式 ─────────────────────────

    private void EnsureLayout(ICanvasResourceCreator creator)
    {
        float width = Math.Max(120, (float)Canvas.ActualWidth - PadX * 2);
        if (Math.Abs(width - _layoutWidth) < 0.5f && _rows.All(r => r.Main is not null)) return;

        _mainFormat ??= new CanvasTextFormat
        {
            FontFamily = "Segoe UI Variable Display",
            FontSize = MainFontSize,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            WordWrapping = CanvasWordWrapping.Wrap,
        };
        _translationFormat ??= new CanvasTextFormat
        {
            FontFamily = "Segoe UI Variable Text",
            FontSize = TranslationFontSize,
            WordWrapping = CanvasWordWrapping.Wrap,
        };

        float top = 0;
        foreach (var r in _rows)
        {
            r.DisposeResources();
            r.Main = new CanvasTextLayout(creator, r.Line.Text, _mainFormat, width, 0);
            float height = (float)r.Main.LayoutBounds.Height;
            if (!string.IsNullOrEmpty(r.Line.Translation))
            {
                r.Translation = new CanvasTextLayout(creator, r.Line.Translation, _translationFormat, width, 0);
                height += TranslationGap + (float)r.Translation.LayoutBounds.Height;
            }
            r.Top = top;
            r.Height = height;
            top += height + RowGap;
        }
        _layoutWidth = width;
        _needsSnap = true; // 行高变了，位置要重新算（不做动画）
    }

    private float FocusY => (float)Canvas.ActualHeight * FocusRatio;

    private static float Center(Row r) => r.Top + r.Height / 2;

    /// <summary>当前应该停的滚动位置：当前行（还没开始唱时是第一行）的中心对准焦点高度。</summary>
    private float TargetScroll()
    {
        if (_rows.Count == 0) return 0;
        if (_lyrics is { IsSynced: false }) return -(float)Canvas.ActualHeight * 0.2f;
        return Center(_rows[Math.Max(0, _active)]) - FocusY;
    }

    private (float Min, float Max) ScrollRange()
    {
        if (_rows.Count == 0) return (0, 0);
        float h = (float)Canvas.ActualHeight;
        if (_lyrics is { IsSynced: false })
        {
            float min = -h * 0.2f;
            return (min, Math.Max(min, _rows[^1].Top + _rows[^1].Height - h * 0.7f));
        }
        return (Center(_rows[0]) - FocusY, Center(_rows[^1]) - FocusY);
    }

    // ───────────────────────── 滚动动画 ─────────────────────────

    private static double EaseOut(double p) => 1 - Math.Pow(1 - p, 3);

    private static float ScrollOf(Row r, double now)
    {
        if (now <= r.Start) return r.From;
        if (r.Duration <= 0 || now >= r.Start + r.Duration) return r.To;
        return r.From + (r.To - r.From) * (float)EaseOut((now - r.Start) / r.Duration);
    }

    private void Retarget(bool stagger, double duration)
    {
        float target = TargetScroll();
        double now = Now;
        for (int i = 0; i < _rows.Count; i++)
        {
            var r = _rows[i];
            float current = ScrollOf(r, now);
            // 当前行和它下面的行依次晚一点出发；上面的行立即出发（它们正在离开）。
            double delay = stagger && i >= _active - 1 ? Math.Min(StaggerMax, Math.Max(0, i - _active + 1) * StaggerStep) : 0;
            r.From = current;
            r.To = target;
            r.Start = now + (ReduceMotion ? 0 : delay);
            r.Duration = ReduceMotion ? 0 : duration;
        }
    }

    private void Snap()
    {
        float target = TargetScroll();
        foreach (var r in _rows)
        {
            r.From = r.To = target;
            r.Start = r.Duration = 0;
        }
    }

    // ───────────────────────── 绘制 ─────────────────────────

    private void OnCreateResources(CanvasControl sender, CanvasCreateResourcesEventArgs args)
    {
        // 设备丢失后重建：版式和缓存的图像都属于旧设备。
        foreach (var r in _rows) r.DisposeResources();
        _layoutWidth = -1;
    }

    private Color InkColor() => (Ink as SolidColorBrush)?.Color ?? Microsoft.UI.Colors.White;

    private CanvasCommandList ImageFor(ICanvasResourceCreator creator, Row r, Color ink)
    {
        if (r.Image is not null && r.ImageColor == ink) return r.Image;
        r.Image?.Dispose();
        var list = new CanvasCommandList(creator);
        using (var ds = list.CreateDrawingSession())
        {
            ds.DrawTextLayout(r.Main!, 0, 0, ink);
            if (r.Translation is not null)
                ds.DrawTextLayout(r.Translation, 0, (float)r.Main!.LayoutBounds.Height + TranslationGap, Color.FromArgb(204, ink.R, ink.G, ink.B));
        }
        r.Image = list;
        r.ImageColor = ink;
        return list;
    }

    private static float SmoothStep(float edge0, float edge1, float x)
    {
        if (edge1 <= edge0) return x >= edge1 ? 1 : 0;
        float t = Math.Clamp((x - edge0) / (edge1 - edge0), 0, 1);
        return t * t * (3 - 2 * t);
    }

    private void OnDraw(CanvasControl sender, CanvasDrawEventArgs args)
    {
        if (_lyrics is not { } lyrics || _rows.Count == 0 || sender.ActualHeight <= 0)
        {
            StopLoop();
            return;
        }
        var ds = args.DrawingSession;
        EnsureLayout(sender);
        if (_needsSnap)
        {
            _needsSnap = false;
            Snap();
        }

        double now = Now;
        double dt = Math.Clamp(now - _lastFrame, 0, 0.1);
        _lastFrame = now;
        if (_manual && now >= _manualUntil)
        {
            _manual = false;
            Retarget(stagger: false, duration: 0.7);
        }

        float h = (float)sender.ActualHeight, w = (float)sender.ActualWidth;
        var ink = InkColor();
        float k = ReduceMotion ? 1 : 1 - (float)Math.Exp(-dt / SmoothSeconds);
        bool animating = _manual;

        for (int i = 0; i < _rows.Count; i++)
        {
            var r = _rows[i];
            float y = r.Top - ScrollOf(r, now);
            if (now < r.Start + r.Duration) animating = true;
            r.DrawTop = y;
            r.DrawBottom = y + r.Height;

            // 外观目标：离当前行越远越淡、越模糊；翻看时全部变清楚。
            int d = !lyrics.IsSynced ? 0 : _active < 0 ? i + 1 : Math.Abs(i - _active);
            float opacity, blur, scale;
            if (!lyrics.IsSynced) (opacity, blur, scale) = (0.85f, 0, 1);
            else if (_manual) (opacity, blur, scale) = (d == 0 ? 1f : 0.6f, 0, d == 0 ? 1f : InactiveScale);
            else if (d == 0) (opacity, blur, scale) = (1f, 0, 1f);
            else (opacity, blur, scale) = (Math.Max(0.2f, 0.5f * MathF.Pow(0.86f, d - 1)), Math.Min(MaxBlur, FirstBlur + (d - 1) * BlurPerLine), InactiveScale);

            if (r.Opacity < 0) (r.Opacity, r.Blur, r.Scale) = (opacity, blur, scale);
            else
            {
                r.Opacity += (opacity - r.Opacity) * k;
                r.Blur += (blur - r.Blur) * k;
                r.Scale += (scale - r.Scale) * k;
                if (Math.Abs(opacity - r.Opacity) > 0.003f || Math.Abs(blur - r.Blur) > 0.02f || Math.Abs(scale - r.Scale) > 0.0005f) animating = true;
                else (r.Opacity, r.Blur, r.Scale) = (opacity, blur, scale);
            }

            // 上下边缘渐隐：按这一行的中心在视口里的位置，越靠边越透明，到边上完全透明。
            float center = y + r.Height / 2;
            float fade = SmoothStep(0, h * TopFade, center) * SmoothStep(0, h * BottomFade, h - center);
            float alpha = r.Opacity * fade;
            if (alpha < 0.005f || r.DrawBottom < -60 || y > h + 60) continue;

            if (i == _hover && lyrics.IsSynced)
                ds.FillRoundedRectangle(new Rect(0, y - 10, w, r.Height + 20), 14, 14, Color.FromArgb((byte)(20 * fade), ink.R, ink.G, ink.B));

            var image = ImageFor(sender, r, ink);
            ICanvasImage source = image;
            if (r.Blur > 0.15f)
            {
                r.BlurEffect ??= new GaussianBlurEffect { BorderMode = EffectBorderMode.Soft, Optimization = EffectOptimization.Speed };
                r.BlurEffect.Source = image;
                r.BlurEffect.BlurAmount = r.Blur;
                source = r.BlurEffect;
            }
            ds.Transform = Matrix3x2.CreateScale(r.Scale, new Vector2(PadX, center));
            using (ds.CreateLayer(alpha)) ds.DrawImage(source, PadX, y);
            ds.Transform = Matrix3x2.Identity;
        }

        if (animating) EnsureLoop();
        else StopLoop();
    }

    // ───────────────────────── 出帧 ─────────────────────────

    private void EnsureLoop()
    {
        if (_looping) return;
        _looping = true;
        _lastFrame = Now;
        CompositionTarget.Rendering += OnRendering;
        Canvas.Invalidate();
    }

    private void StopLoop()
    {
        if (!_looping) return;
        _looping = false;
        CompositionTarget.Rendering -= OnRendering;
    }

    private void OnRendering(object? sender, object e)
    {
        if (Visibility != Visibility.Visible || Canvas.ActualWidth <= 0) { StopLoop(); return; }
        Canvas.Invalidate();
    }

    // ───────────────────────── 交互 ─────────────────────────

    private int RowAt(double y)
    {
        for (int i = 0; i < _rows.Count; i++)
            if (y >= _rows[i].DrawTop - RowGap / 2 && y <= _rows[i].DrawBottom + RowGap / 2) return i;
        return -1;
    }

    private void OnWheel(object sender, PointerRoutedEventArgs e)
    {
        if (_rows.Count == 0) return;
        int delta = e.GetCurrentPoint(Canvas).Properties.MouseWheelDelta;
        float shift = -delta * 0.6f;
        var (min, max) = ScrollRange();
        double now = Now;
        _manual = true;
        _manualUntil = now + ManualHoldSeconds;
        foreach (var r in _rows)
        {
            float current = ScrollOf(r, now);
            r.From = current;
            r.To = Math.Clamp(r.To + shift, min, max);
            r.Start = now;
            r.Duration = ReduceMotion ? 0 : ManualScrollSeconds;
        }
        e.Handled = true;
        EnsureLoop();
    }

    private void OnTapped(object sender, TappedRoutedEventArgs e)
    {
        if (_lyrics is not { IsSynced: true }) return;
        int i = RowAt(e.GetPosition(Canvas).Y);
        if (i < 0) return;
        _manual = false; // 跳过去之后立即跟随
        SeekRequested?.Invoke(_rows[i].Line.Time);
    }

    private void OnPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_lyrics is not { IsSynced: true }) return;
        int i = RowAt(e.GetCurrentPoint(Canvas).Position.Y);
        if (i == _hover) return;
        _hover = i;
        Canvas.Invalidate();
    }

    private void OnPointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (_hover < 0) return;
        _hover = -1;
        Canvas.Invalidate();
    }
}
