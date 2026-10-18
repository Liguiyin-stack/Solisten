using System.Collections.Concurrent;
using System.Diagnostics;
using System.Numerics;
using ComputeSharp;
using ComputeSharp.D2D1.WinUI;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Brushes;
using Microsoft.Graphics.Canvas.Effects;
using Microsoft.Graphics.Canvas.Geometry;
using Microsoft.Graphics.Canvas.UI;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Solisten.Core.Model;
using Windows.Foundation;
using Windows.UI;

namespace Solisten.Rendering.Space;

/// <summary>柔光玻璃贴住窗口的边。</summary>
[Flags]
public enum GlassAttach { None = 0, Left = 1, Top = 2, Right = 4, Bottom = 8 }

/// <summary>一首歌的空间素材：封面（视觉主体与颜色来源）、调色板、离线音频特征、时长（进度光环）。</summary>
public sealed record SpaceScene(Palette Palette, string? CoverPath, SoundFeatures? Features, TimeSpan Duration);

/// <summary>
/// 聆听空间：歌曲封面是视觉主体，音乐让它呼吸、起伏，并带动光晕和粒子。
/// 绘制顺序：色彩空间（大幅模糊的封面 + 主色色场 + 流动薄雾与径向光晕，半分辨率）
/// → 扩散波与粒子（在封面后面）→ 柔性封面层（全分辨率，只覆盖封面周围）→ 可读性遮罩。
/// 整个空间是窗口最底层的一块不透明画布，在独立的渲染线程上绘制；暂停后逐渐静止，静止后停止出帧。
/// </summary>
public sealed partial class SoundSpace : UserControl
{
    private const float RenderScale = 0.5f;
    private const int MaxRenderWidth = 1280;
    private const float CoverCorner = 0.07f;
    private const int DotSize = 16;
    private static readonly TimeSpan CrossfadeTime = TimeSpan.FromMilliseconds(400);
    private static readonly TimeSpan ReducedCrossfadeTime = TimeSpan.FromMilliseconds(150);

    private readonly CanvasAnimatedControl _canvas;
    private readonly ConcurrentQueue<(SceneState State, bool Transition)> _incoming = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();

    // —— UI 线程写、渲染线程读 ——
    private volatile SpaceMode _mode = SpaceMode.Standard;
    private volatile bool _reduceMotion;
    private volatile bool _highContrast;
    private volatile bool _visible = true;
    private Color _highContrastBackground = Color.FromArgb(255, 0, 0, 0);
    private Func<(TimeSpan Position, bool Playing)>? _position;
    private float _pointerX = float.NaN, _pointerY = float.NaN;
    private long _lastPointerMove = Stopwatch.GetTimestamp();
    private int _wakeGeneration;
    private SpaceScene? _lastScene;
    private float _stageX = 0.5f, _stageY = 0.47f, _coverSide = 300f;
    private volatile IReadOnlyDictionary<string, GlassPanel> _glass = new Dictionary<string, GlassPanel>();
    private readonly Lock _glassGate = new();
    // 渲染线程：舞台的当前位置（向 _stageX/_stageY 平滑移动，侧边面板打开时封面让位）
    private float _stageXNow = float.NaN, _stageYNow = float.NaN;

    /// <summary>一块柔光玻璃：可见区域（控件坐标，DIP）、圆角、贴住窗口的边（这些边不画圆角）、色调。</summary>
    private sealed record GlassPanel(Rect Rect, float Radius, GlassAttach Attach, bool Dark);

    // —— 渲染线程拥有 ——
    private readonly SpaceDynamics _dynamics = new();
    private readonly Particles _particles = new();
    private SceneState? _current, _previous;
    private double _fade = 1, _fadeSeconds = 0.4;
    private double _lastFrame;
    private float _lastSpaceTime;
    private CanvasRenderTarget? _sceneA, _sceneB, _dot;
    private PixelShaderEffect<NebulaShader>? _nebula;
    // 每帧复用的效果与画笔（每帧新建 Win2D 对象的互操作开销比着色器本身还大）
    private CropEffect? _nebulaCrop;
    private readonly CoverParticles _coverParticles = new();
    private SceneState? _particleScene;
    private int _particleGrid;
    private float _coverOpacity = 1;
    private CanvasLinearGradientBrush? _scrimTop, _scrimBottom;
    private (float W, float H, Rgb Base) _scrimKey;
    private bool _settledRequested;

    public SoundSpace()
    {
        _canvas = new CanvasAnimatedControl
        {
            ClearColor = Color.FromArgb(255, 0x16, 0x17, 0x1A),
            TargetElapsedTime = TimeSpan.FromTicks(166667),
        };
        _canvas.CreateResources += OnCreateResources;
        _canvas.Draw += OnDraw;
        Content = _canvas;
        IsHitTestVisible = false;
        IsTabStop = false;
        Unloaded += (_, _) => _canvas.RemoveFromVisualTree();
    }


    /// <summary>音频时钟：当前位置与是否正在发声。</summary>
    public Func<(TimeSpan Position, bool Playing)>? PositionSource
    {
        get => _position;
        set => _position = value;
    }

    /// <summary>封面的位置与大小（中心为相对窗口的比例，边长为 DIP），由界面布局决定。</summary>
    public void SetStage(float centerX, float centerY, float coverSide)
    {
        Volatile.Write(ref _stageX, centerX);
        Volatile.Write(ref _stageY, centerY);
        Volatile.Write(ref _coverSide, Math.Max(40, coverSide));
        Wake();
    }

    public SpaceMode Mode
    {
        get => _mode;
        set { _mode = value; Wake(); }
    }

    public bool ReduceMotion
    {
        get => _reduceMotion;
        set { _reduceMotion = value; Wake(); }
    }

    public bool HighContrast
    {
        get => _highContrast;
        set { _highContrast = value; Wake(); }
    }

    public Color HighContrastBackground
    {
        get => _highContrastBackground;
        set { _highContrastBackground = value; Wake(); }
    }

    /// <summary>
    /// 在空间里绘制一块柔光玻璃（导航带、设置、资料等面板的背景）：把它后面的色彩空间大幅模糊，叠一层淡色调和光泽。
    /// attach 指出面板贴住窗口的边，这些边不画圆角。rect 为 null 时移除这块玻璃。dark 为 true 时是深色玻璃（用于浅色文字）。
    /// </summary>
    public void SetGlass(string key, Rect? rect, float radius, GlassAttach attach, bool dark)
    {
        lock (_glassGate)
        {
            var next = new Dictionary<string, GlassPanel>(_glass);
            if (rect is { } r && r.Width > 0 && r.Height > 0) next[key] = new GlassPanel(r, radius, attach, dark);
            else if (!next.Remove(key)) return;
            _glass = next;
        }
        Wake();
    }

    /// <summary>窗口可见性（最小化或被完全遮挡时停止渲染）。</summary>
    public bool IsWindowVisible
    {
        get => _visible;
        set { _visible = value; Wake(); }
    }

    /// <summary>鼠标位置（控件坐标）；null 表示鼠标离开窗口。</summary>
    public void SetPointer(Point? p)
    {
        if (p is { } pt && ActualWidth > 0 && ActualHeight > 0)
        {
            Volatile.Write(ref _pointerX, (float)(pt.X / ActualWidth * 2 - 1));
            Volatile.Write(ref _pointerY, (float)(pt.Y / ActualHeight * 2 - 1));
        }
        else
        {
            Volatile.Write(ref _pointerX, float.NaN);
            Volatile.Write(ref _pointerY, float.NaN);
        }
        Interlocked.Exchange(ref _lastPointerMove, Stopwatch.GetTimestamp());
        // 暂停时空间保持静止，鼠标不再唤醒它（只在播放时响应视差）
        if (_position?.Invoke().Playing ?? false) Wake();
    }

    /// <summary>切换到一首歌的空间。transition 为 false 时（例如分析完成后更新颜色）快速过渡。</summary>
    public void ShowScene(SpaceScene scene, bool transition)
    {
        _lastScene = scene;
        var device = _canvas.Device;
        if (device is null) return; // CreateResources 时会重新加载
        _ = LoadAsync(device, scene, transition);
    }

    /// <summary>播放状态或位置变化：恢复出帧，由空间自己再次静止。</summary>
    public void Wake()
    {
        Interlocked.Increment(ref _wakeGeneration);
        _settledRequested = false;
        if (DispatcherQueue is null) return;
        if (DispatcherQueue.HasThreadAccess) ApplyPaused();
        else DispatcherQueue.TryEnqueue(ApplyPaused);
    }

    private void ApplyPaused()
    {
        bool still = !_visible || _highContrast || _reduceMotion;
        _canvas.Paused = still;
        _canvas.TargetElapsedTime = TimeSpan.FromSeconds(1.0 / ModeProfile.Of(_mode).Fps);
        if (_visible) _canvas.Invalidate(); // 静止模式下也要画出当前画面
    }

    // ───────────────────────── 资源 ─────────────────────────

    private void OnCreateResources(CanvasAnimatedControl sender, CanvasCreateResourcesEventArgs args)
    {
        _nebula = new PixelShaderEffect<NebulaShader>();
        _nebulaCrop = new CropEffect { Source = _nebula };
        _scrimTop?.Dispose();
        _scrimBottom?.Dispose();
        _scrimTop = _scrimBottom = null;
        foreach (var t in new[] { _sceneA, _sceneB, _dot }) t?.Dispose();
        _sceneA = _sceneB = null;
        _dot = CreateDot(sender);
        _particleScene = null;
        _current?.Dispose();
        _previous?.Dispose();
        _current = _previous = null;
        if (_lastScene is { } scene) args.TrackAsyncAction(LoadAsync(sender.Device, scene, transition: false).AsAsyncAction());
    }

    private async Task LoadAsync(CanvasDevice device, SpaceScene scene, bool transition)
    {
        CanvasBitmap? cover = null;
        if (scene.CoverPath is { } path)
        {
            try { cover = await CanvasBitmap.LoadAsync(device, path, 96); }
            catch (Exception e) when (e is IOException or ArgumentException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException) { }
        }
        _incoming.Enqueue((new SceneState(scene, cover), transition));
        Wake();
    }

    // ───────────────────────── 绘制（渲染线程） ─────────────────────────

    private void OnDraw(ICanvasAnimatedControl sender, CanvasAnimatedDrawEventArgs args)
    {
        var ds = args.DrawingSession;
        var size = sender.Size;
        float w = (float)size.Width, h = (float)size.Height;
        if (w < 2 || h < 2) return;

        double now = _clock.Elapsed.TotalSeconds;
        double dt = _lastFrame == 0 ? 1 / 60.0 : now - _lastFrame;
        _lastFrame = now;

        AcceptIncoming();
        // 舞台平滑移动到目标位置（约 0.25 秒）：侧边面板打开或关闭时封面让位，不会跳动
        float tx = w * Volatile.Read(ref _stageX), ty = h * Volatile.Read(ref _stageY);
        if (float.IsNaN(_stageXNow) || _reduceMotion) { _stageXNow = tx; _stageYNow = ty; }
        else
        {
            float k = 1 - MathF.Exp(-(float)dt / 0.08f);
            _stageXNow += (tx - _stageXNow) * k;
            _stageYNow += (ty - _stageYNow) * k;
            if (MathF.Abs(tx - _stageXNow) < 0.3f && MathF.Abs(ty - _stageYNow) < 0.3f) { _stageXNow = tx; _stageYNow = ty; }
        }
        bool stageMoving = _stageXNow != tx || _stageYNow != ty;
        var stage = new Vector2(_stageXNow, _stageYNow);
        float half = Volatile.Read(ref _coverSide) / 2;

        if (_highContrast)
        {
            DrawHighContrast(ds, stage, half);
            return;
        }
        if (_current is null)
        {
            ds.Clear(Color.FromArgb(255, 0x16, 0x17, 0x1A));
            return;
        }
        if (_fade < 1) _fade = Math.Min(1, _fade + dt / _fadeSeconds);
        if (_fade >= 1 && _previous is not null)
        {
            _previous.Dispose();
            _previous = null;
        }

        // —— 动力学 ——
        var profile = ModeProfile.Of(_mode);
        var (pos, playing) = _position?.Invoke() ?? (TimeSpan.Zero, false);
        float pxr = Volatile.Read(ref _pointerX), pyr = Volatile.Read(ref _pointerY);
        var idle = Stopwatch.GetElapsedTime(Interlocked.Read(ref _lastPointerMove));
        var input = new SpaceInput(pos, playing, _mode, _reduceMotion, float.IsNaN(pxr) ? null : new Vector2(pxr, pyr), idle);
        _dynamics.Features = _current.Scene.Features;
        var frame = _dynamics.Step(input, dt);
        float spaceDt = frame.Time - _lastSpaceTime;
        _lastSpaceTime = frame.Time;

        var mood = _fade < 0.5 && _previous is not null ? _previous.Mood : _current.Mood;
        var colors = BlendColors();

        // —— 色彩空间（半分辨率）——
        float dpiScale = sender.Dpi / 96f;
        float scale = Math.Min(RenderScale, MaxRenderWidth / Math.Max(1, w * dpiScale)) * dpiScale; // 场景像素 / DIP
        int rw = Math.Max(2, (int)(w * scale)), rh = Math.Max(2, (int)(h * scale));
        EnsureSceneTargets(sender.Device, rw, rh);

        using (var s = _sceneA!.CreateDrawingSession())
        {
            s.Clear(ToColor(colors.Dim));
            DrawBackdrop(s, _previous, rw, rh, frame, 1f);
            DrawBackdrop(s, _current, rw, rh, frame, (float)_fade);
        }
        var flow = mood.FlowDirection * mood.FlowSpeed * 0.035f;
        _nebula!.Sources[0] = _sceneA;
        _nebula.ConstantBuffer = new NebulaShader(
            frame.Time, new float2(rw, rh), new float2(stage.X * scale, stage.Y * scale), half * scale * 1.6f * (1 + 0.5f * frame.Energy),
            frame.Breath, frame.Bend, frame.Pulse,
            new float2(flow.X, flow.Y), new float2(frame.Parallax.X, frame.Parallax.Y), profile.Fog,
            F3(colors.S0), F3(colors.S1), F3(colors.S2), F3(colors.S0));
        using (var s = _sceneB!.CreateDrawingSession())
        {
            _nebulaCrop!.SourceRectangle = new Rect(0, 0, rw, rh);
            s.DrawImage(_nebulaCrop);
        }
        ds.DrawImage(_sceneB, new Rect(0, 0, w, h), new Rect(0, 0, rw, rh), 1f, CanvasImageInterpolation.Linear);

        // —— 封面后面：扩散波与粒子 ——
        _particles.Update(frame, spaceDt, profile, stage, half, mood.FlowDirection, mood.FlowSpeed, colors.Count);
        DrawRipples(ds, frame, stage, half, colors.S0);
        DrawParticles(ds, colors, mood.ParticleWhiteness, h);

        // —— 柔性封面层（全分辨率，只覆盖封面周围）——
        EnsureCoverParticles(sender.Device, profile);
        _coverParticles.Update(frame, (float)dt, profile);
        DrawCoverParticles(ds, frame, profile, stage, half, dpiScale);

        // —— 可读性遮罩 ——
        DrawScrims(ds, w, h, colors.Base);

        // —— 柔光玻璃（导航带）——
        foreach (var (key, glass) in _glass) DrawGlass(ds, key, glass, scale);

        // 完全静止后停止出帧（播放、鼠标移动、切歌时 Wake 会重新开始）
        bool settled = _dynamics.IsSettled && _fade >= 1 && _incoming.IsEmpty && _coverParticles.IsSettled && !stageMoving;
        if (settled && !_settledRequested)
        {
            _settledRequested = true;
            int gen = Volatile.Read(ref _wakeGeneration);
            DispatcherQueue?.TryEnqueue(() =>
            {
                if (gen == Volatile.Read(ref _wakeGeneration) && !(_position?.Invoke().Playing ?? false)) _canvas.Paused = true;
            });
        }
    }

    /// <summary>
    /// 光粒子封面：每颗粒子是一个柔和的光点，用封面对应位置的颜色着色；
    /// 光点略微重叠，静止时拼成清晰的封面；闪烁的粒子再以加法混合叠一层光。
    /// </summary>
    private void DrawCoverParticles(CanvasDrawingSession ds, in SpaceFrame frame, in ModeProfile profile, Vector2 stage, float half, float dpiScale)
    {
        var c = _coverParticles;
        if (c.Count == 0 || _dot is null) return;

        // 景深：视差和左右声道让整个封面轻微平移
        var center = stage + new Vector2(frame.Parallax.X, frame.Parallax.Y) * half * 6f * profile.Depth;
        float spacing = half * 2 / c.Grid;
        float baseSize = spacing * 1.7f;
        var src = new Rect(0, 0, DotSize, DotSize);

        using (var batch = ds.CreateSpriteBatch(CanvasSpriteSortMode.None, CanvasImageInterpolation.Linear, CanvasSpriteOptions.None))
        {
            for (int i = 0; i < c.Count; i++)
            {
                float a = c.Alpha(i) * _coverOpacity;
                if (a <= 0.01f) continue;
                var off = c.Offset(i);
                var p = center + c.Position(i) * half;
                // 被吹离原位的粒子看起来离观众更近：稍大
                float size = baseSize * (1 + Math.Min(off.Length() * 1.4f, 1.2f));
                var col = c.Color(i);
                batch.DrawFromSpriteSheet(_dot, new Rect(p.X - size / 2, p.Y - size / 2, size, size), src,
                    new Vector4(col.X * a, col.Y * a, col.Z * a, a));
            }
        }

        // 光：每颗粒子再以加法混合叠一个更大、更淡的光晕，整体像由光组成；随呼吸与能量变亮
        var old = ds.Blend;
        ds.Blend = CanvasBlend.Add;
        float glow = (0.10f + 0.10f * frame.Breath + 0.12f * frame.Energy + 0.10f * frame.Pulse) * _coverOpacity;
        using (var batch = ds.CreateSpriteBatch(CanvasSpriteSortMode.None, CanvasImageInterpolation.Linear, CanvasSpriteOptions.None))
        {
            float size = baseSize * 3.4f;
            for (int i = 0; i < c.Count; i += 4) // 四分之一的粒子（更大的光斑）就足够形成连续的光
            {
                // 只给成片的内部粒子加柔光；边缘和被吹散的粒子单独发光时会变成一个个圆形光斑
                float a = c.Alpha(i) * (1 - c.Edge(i)) * MathF.Max(0, 1 - c.Offset(i).Length() * 2.5f);
                if (a <= 0.05f) continue;
                var p = center + c.Position(i) * half;
                var col = c.Color(i) * (glow * 1.7f * a);
                batch.DrawFromSpriteSheet(_dot, new Rect(p.X - size / 2, p.Y - size / 2, size, size), src, col with { W = col.W });
            }
        }

        // 高频闪烁：加法混合的亮点
        using (var batch = ds.CreateSpriteBatch(CanvasSpriteSortMode.None, CanvasImageInterpolation.Linear, CanvasSpriteOptions.None))
        {
            for (int i = 0; i < c.Count; i++)
            {
                float tw = c.Twinkle(i);
                if (tw <= 0.02f) continue;
                var p = center + c.Position(i) * half;
                float size = baseSize * (1.3f + 0.5f * tw);
                var col = Vector4.Lerp(c.Color(i), Vector4.One, 0.2f) * (0.45f * tw * _coverOpacity);
                batch.DrawFromSpriteSheet(_dot, new Rect(p.X - size / 2, p.Y - size / 2, size, size), src, col with { W = col.W });
            }
        }
        ds.Blend = old;
    }

    private GaussianBlurEffect? _glassBlur;
    private readonly Dictionary<string, (GlassPanel Panel, CanvasGeometry Clip, CanvasLinearGradientBrush Sheen)> _glassCache = [];

    /// <summary>柔光玻璃：模糊后面的色彩空间 + 淡色调 + 光泽。贴住窗口的边把圆角推到窗口外，所以那些边是直的。</summary>
    private void DrawGlass(CanvasDrawingSession ds, string key, GlassPanel g, float sceneScale)
    {
        var r = g.Rect;
        if (!_glassCache.TryGetValue(key, out var cache) || cache.Panel != g)
        {
            if (_glassCache.TryGetValue(key, out var old))
            {
                old.Clip.Dispose();
                old.Sheen.Dispose();
            }
            float e = g.Radius * 2;
            double x0 = r.X - (g.Attach.HasFlag(GlassAttach.Left) ? e : 0);
            double y0 = r.Y - (g.Attach.HasFlag(GlassAttach.Top) ? e : 0);
            double x1 = r.X + r.Width + (g.Attach.HasFlag(GlassAttach.Right) ? e : 0);
            double y1 = r.Y + r.Height + (g.Attach.HasFlag(GlassAttach.Bottom) ? e : 0);
            var clip = CanvasGeometry.CreateRoundedRectangle(ds, new Rect(x0, y0, x1 - x0, y1 - y0), g.Radius, g.Radius);
            var sheen = new CanvasLinearGradientBrush(ds,
            [
                new CanvasGradientStop { Position = 0.0f, Color = Color.FromArgb(g.Dark ? (byte)34 : (byte)70, 255, 255, 255) },
                new CanvasGradientStop { Position = 0.35f, Color = Color.FromArgb(g.Dark ? (byte)12 : (byte)30, 255, 255, 255) },
                new CanvasGradientStop { Position = 1.0f, Color = Color.FromArgb(0, 255, 255, 255) },
            ])
            { StartPoint = new Vector2(0, (float)r.Y), EndPoint = new Vector2(0, (float)(r.Y + Math.Min(r.Height, 320))) };
            cache = (g, clip, sheen);
            _glassCache[key] = cache;
        }
        _glassBlur ??= new GaussianBlurEffect { BorderMode = EffectBorderMode.Hard, Optimization = EffectOptimization.Speed };
        _glassBlur.Source = _sceneB;
        _glassBlur.BlurAmount = 26f * sceneScale;

        var src = new Rect(r.X * sceneScale, r.Y * sceneScale, r.Width * sceneScale, r.Height * sceneScale);
        using (ds.CreateLayer(1f, cache.Clip))
        {
            ds.DrawImage(_glassBlur, r, src, 1f, CanvasImageInterpolation.Linear);
            // 色调：深色玻璃压暗一点、浅色玻璃提亮一点，但都让后面的颜色透出来
            ds.FillRectangle(r, g.Dark ? Color.FromArgb(105, 10, 11, 14) : Color.FromArgb(120, 250, 250, 248));
            ds.FillRectangle(r, cache.Sheen);
        }
    }

    /// <summary>柔和的圆形光点（预乘透明度的白色），所有粒子共用。</summary>
    private static CanvasRenderTarget CreateDot(ICanvasResourceCreator device)
    {
        var dot = new CanvasRenderTarget(device, DotSize, DotSize, 96);
        using var s = dot.CreateDrawingSession();
        s.Clear(Color.FromArgb(0, 0, 0, 0));
        using var brush = new CanvasRadialGradientBrush(s,
        [
            new CanvasGradientStop { Position = 0.00f, Color = Color.FromArgb(255, 255, 255, 255) },
            new CanvasGradientStop { Position = 0.45f, Color = Color.FromArgb(230, 255, 255, 255) },
            new CanvasGradientStop { Position = 1.00f, Color = Color.FromArgb(0, 255, 255, 255) },
        ])
        { Center = new Vector2(DotSize / 2f), RadiusX = DotSize / 2f, RadiusY = DotSize / 2f };
        s.FillCircle(new Vector2(DotSize / 2f), DotSize / 2f, brush);
        return dot;
    }

    /// <summary>换歌或网格密度（模式）变化时，用封面像素重建粒子；换歌时粒子从四周飞回重组。</summary>
    private void EnsureCoverParticles(CanvasDevice device, in ModeProfile profile)
    {
        int grid = CoverParticles.GridFor(_mode);
        if (ReferenceEquals(_particleScene, _current) && _particleGrid == grid) return;
        bool newTrack = !ReferenceEquals(_particleScene, _current) && _particleScene is not null;
        var pixels = _current!.Pixels(device, grid);
        _coverParticles.Build(grid, pixels, _current.Scene.Palette);
        if (newTrack && !_reduceMotion) _coverParticles.Scatter(0.9f * profile.Depth);
        _particleScene = _current;
        _particleGrid = grid;
    }

    private void DrawHighContrast(CanvasDrawingSession ds, Vector2 stage, float half)
    {
        ds.Clear(_highContrastBackground);
        if (_current is null) return;
        var device = ds.Device;
        int px = Math.Max(8, (int)(half * 2));
        var image = _current.Cover(device, px);
        if (image is null) return;
        var rect = new Rect(stage.X - half, stage.Y - half, half * 2, half * 2);
        using var clip = CanvasGeometry.CreateRoundedRectangle(device, rect, half * CoverCorner, half * CoverCorner);
        using (ds.CreateLayer(1f, clip)) ds.DrawImage(image, rect, image.GetBounds(device));
    }

    private void AcceptIncoming()
    {
        while (_incoming.TryDequeue(out var next))
        {
            bool sameTrack = _current is not null && _current.Scene.CoverPath == next.State.Scene.CoverPath
                && _current.Scene.Duration == next.State.Scene.Duration;
            _previous?.Dispose();
            _previous = _current;
            _current = next.State;
            _fadeSeconds = (next.Transition && !_reduceMotion ? CrossfadeTime : ReducedCrossfadeTime).TotalSeconds;
            _fade = _previous is null ? 1 : 0;
            if (!sameTrack) _particles.Clear();
        }
    }

    private void EnsureSceneTargets(CanvasDevice device, int w, int h)
    {
        if (_sceneA is not null && _sceneA.SizeInPixels.Width == w && _sceneA.SizeInPixels.Height == h) return;
        _sceneA?.Dispose();
        _sceneB?.Dispose();
        _sceneA = new CanvasRenderTarget(device, w, h, 96);
        _sceneB = new CanvasRenderTarget(device, w, h, 96);
        _current?.InvalidateBackdrop();
        _previous?.InvalidateBackdrop();
    }

    /// <summary>色彩空间的底：大幅模糊的封面，低频带来轻微缩放和明暗，叠加视差。</summary>
    private static void DrawBackdrop(CanvasDrawingSession s, SceneState? state, int w, int h, in SpaceFrame frame, float opacity)
    {
        if (state is null || opacity <= 0) return;
        var backdrop = state.Backdrop(s.Device, w, h);
        float zoom = 1.04f + 0.02f * frame.Breath + 0.012f * frame.Pulse;
        var offset = new Vector2(-frame.Parallax.X * w * 0.6f, -frame.Parallax.Y * h * 0.6f);
        var m = Matrix3x2.CreateTranslation(-w / 2f, -h / 2f) * Matrix3x2.CreateScale(zoom) * Matrix3x2.CreateTranslation(w / 2f + offset.X, h / 2f + offset.Y);
        var old = s.Transform;
        s.Transform = m;
        s.DrawImage(backdrop, 0, 0, new Rect(0, 0, w, h), opacity);
        s.Transform = old;
    }

    private void DrawRipples(CanvasDrawingSession ds, in SpaceFrame frame, Vector2 center, float half, Rgb color)
    {
        foreach (var r in _particles.Ripples)
        {
            var (scale, alpha) = Particles.RippleShape(r, frame.Time);
            if (alpha <= 0.002f) continue;
            float rr = half * scale;
            var c = ToColor(color);
            using var brush = new CanvasRadialGradientBrush(ds,
            [
                new CanvasGradientStop { Position = 0.80f, Color = WithAlpha(c, 0) },
                new CanvasGradientStop { Position = 0.93f, Color = WithAlpha(c, alpha) },
                new CanvasGradientStop { Position = 1.00f, Color = WithAlpha(c, 0) },
            ])
            { Center = center, RadiusX = rr, RadiusY = rr };
            ds.FillCircle(center, rr, brush);
        }
    }

    /// <summary>粒子在歌名区域和底部进度/控制区域淡出，不与文字和控件重叠。</summary>
    internal static float ZoneFade(float y, float h) =>
        Smooth(h * 0.16f, h * 0.26f, y) * (1 - Smooth(h * 0.80f, h * 0.88f, y));

    private static float Smooth(float a, float b, float x)
    {
        float t = Math.Clamp((x - a) / (b - a), 0, 1);
        return t * t * (3 - 2 * t);
    }

    private void DrawParticles(CanvasDrawingSession ds, in Colors colors, float whiteness, float h)
    {
        var items = _particles.Items;
        for (int i = 0; i < items.Length; i++)
        {
            ref readonly var p = ref items[i];
            float a = Particles.Alpha(p) * 0.75f * ZoneFade(p.Position.Y, h);
            if (a <= 0.01f) continue;
            var c = Mix(colors[p.Color % Math.Max(1, colors.Count)], new Rgb(255, 255, 255), whiteness);
            ds.FillCircle(p.Position, p.Size * 2.2f, WithAlpha(ToColor(c), a * 0.18f));
            ds.FillCircle(p.Position, p.Size, WithAlpha(ToColor(c), a));
        }
    }

    /// <summary>歌名区域和底部控制区域的柔和遮罩：文字落在接近 Base 的背景上（Base 已保证 4.5:1）。画笔只在尺寸或颜色变化时重建。</summary>
    private void DrawScrims(CanvasDrawingSession ds, float w, float h, Rgb baseColor)
    {
        if (_scrimTop is null || _scrimKey != (w, h, baseColor))
        {
            _scrimTop?.Dispose();
            _scrimBottom?.Dispose();
            var c = ToColor(baseColor);
            _scrimTop = new CanvasLinearGradientBrush(ds,
                [new CanvasGradientStop { Position = 0, Color = WithAlpha(c, 0.62f) }, new CanvasGradientStop { Position = 1, Color = WithAlpha(c, 0) }])
            { StartPoint = new Vector2(0, 0), EndPoint = new Vector2(0, h * 0.30f) };
            _scrimBottom = new CanvasLinearGradientBrush(ds,
                [new CanvasGradientStop { Position = 0, Color = WithAlpha(c, 0) }, new CanvasGradientStop { Position = 1, Color = WithAlpha(c, 0.75f) }])
            { StartPoint = new Vector2(0, h * 0.75f), EndPoint = new Vector2(0, h) };
            _scrimKey = (w, h, baseColor);
        }
        ds.FillRectangle(0, 0, w, h * 0.30f, _scrimTop);
        ds.FillRectangle(0, h * 0.75f, w, h * 0.25f, _scrimBottom!);
    }

    // ───────────────────────── 颜色 ─────────────────────────

    /// <summary>交叉淡化期间混合前后两首歌的颜色。</summary>
    private readonly record struct Colors(Rgb Base, Rgb Dim, Rgb Accent, Rgb Ink, Rgb S0, Rgb S1, Rgb S2, Rgb S3, int Count)
    {
        public Rgb this[int i] => i switch { 0 => S0, 1 => S1, 2 => S2, _ => S3 };
    }

    private Colors BlendColors()
    {
        var c = ColorsOf(_current!);
        if (_previous is null || _fade >= 1) return c;
        var p = ColorsOf(_previous);
        float t = (float)_fade;
        return new Colors(Mix(p.Base, c.Base, t), Mix(p.Dim, c.Dim, t), Mix(p.Accent, c.Accent, t), Mix(p.Ink, c.Ink, t),
            Mix(p.S0, c.S0, t), Mix(p.S1, c.S1, t), Mix(p.S2, c.S2, t), Mix(p.S3, c.S3, t), c.Count);
    }

    private static Colors ColorsOf(SceneState s)
    {
        var pal = s.Scene.Palette;
        var sw = pal.SpaceColors;
        Rgb At(int i) => sw[Math.Min(i, sw.Count - 1)];
        return new Colors(pal.Base, pal.Dim, pal.AccentOnBase, pal.TextOnBase, At(0), At(1), At(2), At(3), sw.Count);
    }

    private static Rgb Mix(Rgb a, Rgb b, float t) => new(
        (byte)(a.R + (b.R - a.R) * t), (byte)(a.G + (b.G - a.G) * t), (byte)(a.B + (b.B - a.B) * t));

    private static float3 F3(Rgb c)
    {
        // sRGB → 线性近似（着色器里做加法混合时更自然）
        static float L(byte v) { float x = v / 255f; return x * x; }
        return new float3(L(c.R), L(c.G), L(c.B));
    }

    private static Color ToColor(Rgb c) => Color.FromArgb(255, c.R, c.G, c.B);

    private static Color WithAlpha(Color c, float a) => Color.FromArgb((byte)Math.Clamp(a * 255, 0, 255), c.R, c.G, c.B);

    /// <summary>一首歌在 GPU 上的素材。</summary>
    private sealed class SceneState(SpaceScene scene, CanvasBitmap? cover) : IDisposable
    {
        private CanvasRenderTarget? _backdrop;
        private CanvasRenderTarget? _cover;

        public SpaceScene Scene { get; } = scene;
        public SpaceMood Mood { get; } = SpaceMood.From(scene.Palette);

        public void InvalidateBackdrop()
        {
            _backdrop?.Dispose();
            _backdrop = null;
        }

        /// <summary>封面居中裁成正方形后缩放到 grid×grid 的颜色（行优先），给光粒子着色；没有封面时为空。</summary>
        public Rgb[] Pixels(ICanvasResourceCreator device, int grid)
        {
            if (cover is null) return [];
            using var target = new CanvasRenderTarget(device, grid, grid, 96);
            var src = cover.SizeInPixels;
            float side = Math.Min(src.Width, src.Height);
            using (var s = target.CreateDrawingSession())
            {
                s.Clear(Color.FromArgb(255, 0, 0, 0));
                s.DrawImage(cover, new Rect(0, 0, grid, grid), new Rect((src.Width - side) / 2, (src.Height - side) / 2, side, side), 1f, CanvasImageInterpolation.HighQualityCubic);
            }
            var colors = target.GetPixelColors();
            var result = new Rgb[colors.Length];
            for (int i = 0; i < colors.Length; i++) result[i] = new Rgb(colors[i].R, colors[i].G, colors[i].B);
            return result;
        }

        /// <summary>
        /// 预缩放成正方形的清晰封面（居中裁切，高质量插值），按显示尺寸缓存，
        /// 着色器以接近 1:1 的比例采样，避免大图缩小时的摩尔纹。
        /// </summary>
        public ICanvasImage? Cover(ICanvasResourceCreator device, int px)
        {
            if (cover is null) return null;
            if (_cover is not null && Math.Abs((int)_cover.SizeInPixels.Width - px) <= Math.Max(2, px / 50)) return _cover;
            _cover?.Dispose();
            _cover = new CanvasRenderTarget(device, px, px, 96);
            var src = cover.SizeInPixels;
            float side = Math.Min(src.Width, src.Height);
            var source = new Rect((src.Width - side) / 2, (src.Height - side) / 2, side, side);
            using var s = _cover.CreateDrawingSession();
            s.DrawImage(cover, new Rect(0, 0, px, px), source, 1f, CanvasImageInterpolation.HighQualityCubic);
            return _cover;
        }

        /// <summary>
        /// 封面后面的色彩空间：大幅模糊、低透明度的封面，被主色色场压住，看不出封面细节，只剩颜色与明暗。
        /// </summary>
        public CanvasRenderTarget Backdrop(ICanvasResourceCreator device, int w, int h)
        {
            if (_backdrop is not null && _backdrop.SizeInPixels.Width == w && _backdrop.SizeInPixels.Height == h) return _backdrop;
            _backdrop?.Dispose();
            _backdrop = new CanvasRenderTarget(device, w, h, 96);
            var pal = Scene.Palette;
            using var s = _backdrop.CreateDrawingSession();
            s.Clear(ToColor(pal.Dim));
            if (cover is not null)
            {
                var src = cover.SizeInPixels;
                float scale = Math.Max(w / (float)src.Width, h / (float)src.Height) * 1.3f;
                using var blur = new GaussianBlurEffect
                {
                    Source = new Transform2DEffect
                    {
                        Source = cover,
                        TransformMatrix = Matrix3x2.CreateScale(scale) * Matrix3x2.CreateTranslation((w - src.Width * scale) / 2, (h - src.Height * scale) / 2),
                    },
                    BlurAmount = Math.Max(30f, Math.Min(w, h) / 4.5f),
                    BorderMode = EffectBorderMode.Hard,
                };
                using var sat = new SaturationEffect { Source = blur, Saturation = 0.85f };
                s.DrawImage(sat, 0, 0, new Rect(0, 0, w, h), 0.55f);
            }
            using var tint = new CanvasLinearGradientBrush(s,
            [
                new CanvasGradientStop { Position = 0, Color = WithAlpha(ToColor(pal.Base), cover is null ? 1f : 0.62f) },
                new CanvasGradientStop { Position = 1, Color = WithAlpha(ToColor(pal.Dim), cover is null ? 1f : 0.78f) },
            ])
            { StartPoint = new Vector2(0, 0), EndPoint = new Vector2(0, h) };
            s.FillRectangle(0, 0, w, h, tint);
            return _backdrop;
        }

        public void Dispose()
        {
            _backdrop?.Dispose();
            _cover?.Dispose();
            cover?.Dispose();
        }
    }
}
