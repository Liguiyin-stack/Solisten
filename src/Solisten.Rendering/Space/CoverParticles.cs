using System.Numerics;
using Solisten.Core.Model;

namespace Solisten.Rendering.Space;

/// <summary>
/// 由光粒子拼成的封面（纯计算，绘制在 <see cref="SoundSpace"/> 中完成）。
/// 封面被采样成 N×N 的网格，每颗粒子带着对应位置的颜色，有自己的"家"（静止时所在的位置）。
/// 静止时粒子排成清晰的封面，没有边框，边缘自然消散；播放时：
/// <list type="bullet">
/// <item>低频：整体向外膨胀，离中心越远位移越大，像在呼吸；</item>
/// <item>中频：流动的波浪位移，像表面被音乐吹动；</item>
/// <item>高频：少量粒子短暂变亮闪烁（偏向边缘），不影响整体；</item>
/// <item>明显重音：粒子向外炸开，再被弹簧慢慢拉回；</item>
/// <item>能量（高潮）：粒子之间拉开，封面整体变"疏"、更亮；</item>
/// <item>暂停：目标位移随运动量归零，粒子逐渐回到原位，重新拼成清晰的封面后停止。</item>
/// </list>
/// 坐标单位是封面半边长（家在 [-1, 1]²），由绘制时换算成像素。
/// </summary>
public sealed class CoverParticles
{
    /// <summary>弹簧刚度与阻尼：接近临界阻尼，回位平滑、不来回弹。</summary>
    private const float Stiffness = 38f;
    private const float Damping = 11f;
    private const float SettleEpsilon = 0.0015f;

    private Vector2[] _home = [];
    private Vector2[] _offset = [];
    private Vector2[] _velocity = [];
    private Vector4[] _color = [];
    private float[] _seed = [];
    private float[] _edge = [];
    private float[] _twinkle = [];
    private int _grid;
    private int _lastOnset = -1;
    private readonly Random _rng = new(11);
    private float[] _waveX = [], _waveY = [];

    public int Grid => _grid;
    public int Count => _home.Length;
    public bool IsSettled { get; private set; } = true;

    /// <summary>每档模式的网格边长：越密越清晰、越重。</summary>
    public static int GridFor(SpaceMode mode) => mode switch
    {
        SpaceMode.Quiet => 64,
        SpaceMode.Immersive => 104,
        _ => 84,
    };

    /// <summary>
    /// 用封面像素建立粒子。pixels 是 grid×grid 的 sRGB 颜色（行优先）；为 null 时用调色板生成渐变占位。
    /// </summary>
    public void Build(int grid, ReadOnlySpan<Rgb> pixels, Palette? fallback = null)
    {
        _grid = grid;
        int n = grid * grid;
        _home = new Vector2[n];
        _offset = new Vector2[n];
        _velocity = new Vector2[n];
        _color = new Vector4[n];
        _seed = new float[n];
        _edge = new float[n];
        _twinkle = new float[n];
        _waveX = new float[grid];
        _waveY = new float[grid];
        _lastOnset = -1;

        var pal = fallback ?? Palette.Neutral;
        for (int y = 0; y < grid; y++)
        {
            for (int x = 0; x < grid; x++)
            {
                int i = y * grid + x;
                var home = new Vector2((x + 0.5f) / grid * 2 - 1, (y + 0.5f) / grid * 2 - 1);
                _home[i] = home;
                Rgb c = pixels.Length == n ? pixels[i] : Mix(pal.Base, pal.Accent, (home.Y + 1) / 2);
                _color[i] = new Vector4(c.R / 255f, c.G / 255f, c.B / 255f, 1);
                _seed[i] = (float)_rng.NextDouble();
                // 边缘系数：0 在中心区域，1 在最外圈；用于无边框的柔和消散
                float d = MathF.Max(MathF.Abs(home.X), MathF.Abs(home.Y));
                _edge[i] = Math.Clamp((d - 0.82f) / 0.18f, 0, 1);
            }
        }
        IsSettled = true;
    }

    /// <summary>
    /// 换歌时把粒子打散：新封面的粒子从四周飞回原位重组。amount 是平均散开距离（封面半边长的倍数）。
    /// </summary>
    public void Scatter(float amount)
    {
        for (int i = 0; i < _home.Length; i++)
        {
            float a = (float)(_rng.NextDouble() * Math.PI * 2);
            float d = amount * (0.4f + (float)_rng.NextDouble() * 1.2f);
            _offset[i] = new Vector2(MathF.Cos(a), MathF.Sin(a)) * d + _home[i] * amount * 0.6f;
            _velocity[i] = Vector2.Zero;
        }
        IsSettled = _home.Length == 0;
    }

    public Vector2 Home(int i) => _home[i];
    public Vector2 Offset(int i) => _offset[i];
    public Vector2 Position(int i) => _home[i] + _offset[i];
    public Vector4 Color(int i) => _color[i];
    public float Edge(int i) => _edge[i];
    public float Twinkle(int i) => _twinkle[i];

    /// <summary>
    /// 不透明度：中心区域完全不透明（保证封面清晰），最外圈渐隐，形成无边框的消散边缘；
    /// 被吹离原位越远越淡。
    /// </summary>
    public float Alpha(int i)
    {
        float e = _edge[i];
        float fray = 0.55f + 0.45f * MathF.Sin(_seed[i] * 40f); // 边缘参差，不是一条整齐的线
        float edgeA = 1 - e * e * (0.55f + 0.45f * fray);
        float drift = _offset[i].Length();
        return Math.Clamp(edgeA * (1 - Math.Clamp(drift * 0.6f, 0, 0.5f)), 0, 1);
    }

    /// <param name="dt">真实时间增量（弹簧回位总是进行，保证暂停后会重新拼好封面）。</param>
    public void Update(in SpaceFrame f, float dt, in ModeProfile profile)
    {
        if (_home.Length == 0) return;
        dt = Math.Clamp(dt, 0, 1 / 20f);

        // 新的明显重音：向外炸开（边缘粒子飞得更远），强度随模式
        bool burst = false;
        float kick = 0;
        if (f.StrongOnset != _lastOnset)
        {
            burst = f.StrongOnset > _lastOnset && f.Pulse > 0.05f;
            _lastOnset = f.StrongOnset;
            kick = 3.6f * Math.Clamp(f.Pulse, 0, 1) * profile.Depth;
        }

        float t = f.Time;
        float breath = f.Breath * 0.10f;
        float bend = f.Bend * 0.10f;
        float spread = f.Energy * 0.11f;
        float sway = f.Sway * 0.07f;
        float drift = 0.012f * f.Motion;
        float sparkle = f.Sparkle;
        float twinkleDecay = MathF.Exp(-dt / 0.18f);
        float damp = MathF.Exp(-Damping * dt);
        bool settled = true;

        // 中频波浪只取决于行/列：每帧按行、按列各算一次，而不是每颗粒子算一次
        for (int k = 0; k < _grid; k++)
        {
            float coord = (k + 0.5f) / _grid * 2 - 1;
            _waveX[k] = MathF.Sin(coord * 3.1f + t * 1.3f) * bend; // 按行（h.Y）
            _waveY[k] = MathF.Cos(coord * 2.7f - t * 1.1f) * bend; // 按列（h.X）
        }
        float driftCore = drift * 0.3f;
        float driftEdge = drift * 6f + f.Bend * 0.22f + f.Energy * 0.18f;
        var dc = new Vector2(MathF.Sin(t * 0.7f), MathF.Cos(t * 0.6f)) * driftCore;

        for (int i = 0; i < _home.Length; i++)
        {
            var h = _home[i];
            float r = h.Length();
            var radial = r > 1e-4f ? h / r : Vector2.Zero;
            float s = _seed[i];

            // 目标位移：都已乘过运动量（frame 中的值），暂停时自然归零
            // 中心区域整片起伏（相位只取决于位置，相邻粒子一起动，不会裂开）；
            // 越靠近边缘越"散"：加上各自独立的漂移，像光粒子在蒸发
            float e = _edge[i];
            float loose = e * e;
            // 呼吸与能量是整体缩放（位移与离中心的距离成正比），图像只会放大，不会从中间裂开
            int gx = i % _grid, gy = i / _grid;
            var target = h * (breath * 2.4f + spread)
                + new Vector2(_waveX[gy], _waveY[gx]) * (0.4f + 0.6f * r)
                + dc + new Vector2(sway, 0);
            if (loose > 0)
                target += new Vector2(MathF.Sin(t * 0.7f + s * 6.28f), MathF.Cos(t * 0.6f + s * 6.28f)) * (loose * driftEdge);

            if (burst)
            {
                float angle = (s - 0.5f) * 0.9f;
                var dir = radial == Vector2.Zero ? new Vector2(MathF.Cos(s * 6.28f), MathF.Sin(s * 6.28f)) : radial;
                dir = new Vector2(dir.X * MathF.Cos(angle) - dir.Y * MathF.Sin(angle), dir.X * MathF.Sin(angle) + dir.Y * MathF.Cos(angle));
                _velocity[i] += dir * kick * (0.35f + 0.65f * r) * (0.6f + 0.8f * s);
            }

            // 弹簧拉向目标（半隐式欧拉 + 指数阻尼，稳定）
            var accel = (target - _offset[i]) * Stiffness;
            _velocity[i] = (_velocity[i] + accel * dt) * damp;
            _offset[i] += _velocity[i] * dt;

            // 高频闪烁：少量粒子短暂变亮，边缘更容易被点亮
            float tw = _twinkle[i] * twinkleDecay;
            if (sparkle > 0.02f && _rng.NextDouble() < sparkle * dt * (0.08f + 0.9f * _edge[i]) * profile.MaxParticles / 300f)
                tw = 1f;
            _twinkle[i] = tw < 0.01f ? 0 : tw;

            if (settled && (_offset[i].LengthSquared() > SettleEpsilon * SettleEpsilon
                || _velocity[i].LengthSquared() > SettleEpsilon * SettleEpsilon || _twinkle[i] > 0
                || (target - Vector2.Zero).LengthSquared() > SettleEpsilon * SettleEpsilon))
                settled = false;
        }

        if (settled)
        {
            Array.Clear(_offset);
            Array.Clear(_velocity);
        }
        IsSettled = settled;
    }

    private static Rgb Mix(Rgb a, Rgb b, float t) => new(
        (byte)(a.R + (b.R - a.R) * t), (byte)(a.G + (b.G - a.G) * t), (byte)(a.B + (b.B - a.B) * t));
}
