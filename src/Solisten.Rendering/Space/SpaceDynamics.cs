using System.Numerics;
using Solisten.Core.Model;

namespace Solisten.Rendering.Space;

/// <summary>数值与旧的 settings.json 兼容（0 安静、1 沉浸），标准为新增的默认档。</summary>
public enum SpaceMode { Quiet = 0, Immersive = 1, Standard = 2 }

/// <summary>
/// 三档模式的全部差异：运动幅度、粒子上限、是否有扩散波、雾的浓度、景深（倾斜与视差的强度）、帧率。
/// </summary>
public readonly record struct ModeProfile(float Amplitude, int MaxParticles, bool Ripples, float Fog, float Depth, int Fps)
{
    /// <summary>安静：呼吸幅度很低、粒子很少，只保留光晕和缓慢漂移，适合长时间使用。</summary>
    public static readonly ModeProfile Quiet = new(0.35f, 36, false, 0.55f, 0.6f, 30);

    /// <summary>标准（默认）：低频有明显起伏，粒子适度运动，重音有短暂扩散。</summary>
    public static readonly ModeProfile Standard = new(0.7f, 140, true, 0.8f, 1.0f, 60);

    /// <summary>沉浸：变形更明显、景深更强、粒子和光效更多，适合全屏欣赏。</summary>
    public static readonly ModeProfile Immersive = new(1.0f, 300, true, 1.0f, 1.4f, 60);

    public static ModeProfile Of(SpaceMode mode) => mode switch
    {
        SpaceMode.Quiet => Quiet,
        SpaceMode.Immersive => Immersive,
        _ => Standard,
    };
}

/// <summary>每帧的输入。Pointer 是相对窗口中心的位置（−1..1），鼠标不在窗口内时为 null。</summary>
public readonly record struct SpaceInput(
    TimeSpan Position,
    bool Playing,
    SpaceMode Mode,
    bool ReduceMotion,
    Vector2? Pointer,
    TimeSpan PointerIdle);

/// <summary>
/// 一帧的空间状态（都已乘以 Motion，暂停时自然回到静止）。
/// Time 是空间自己的动画时钟：只随 Motion 前进，暂停后冻结，所以静止画面是稳定的。
/// Energy 是约 3 秒的长时段能量，用来表现"高潮"：光晕范围与粒子环带随之扩大。
/// </summary>
public readonly record struct SpaceFrame(
    float Breath,
    float Bend,
    float Sparkle,
    float Pulse,
    float Sway,
    Vector2 Parallax,
    float Motion,
    float Time,
    int StrongOnset,
    float Energy = 0);

/// <summary>
/// 把离线音频特征和交互状态变成每帧的空间参数：
/// 低频 → 呼吸，中频 → 变形，高频 → 细节，明显重音 → 短暂外扩，左右声道 → 偏移，鼠标 → 视差。
/// </summary>
public sealed class SpaceDynamics
{
    /// <summary>暂停后以缓出曲线在约 0.6 秒内归于平静（先快后慢，不是瞬间冻结）；恢复播放约 0.6 秒回到全速。</summary>
    public const float SettleSeconds = 0.6f;
    private const float SettleTimeConstant = 0.12f;
    public const float WakeSeconds = 0.6f;
    public const float EnergySeconds = 3f;
    public const float PulseDecaySeconds = 0.28f;
    /// <summary>与上一个明显重音相隔不到这么久时，脉冲按比例减弱：只回应音乐，不跟着每一拍大幅震动。</summary>
    public const float PulseSpacingSeconds = 1.5f;
    public const float RepeatedPulseFloor = 0.35f;
    public const float ParallaxAmount = 0.015f;
    public const float ParallaxEaseSeconds = 0.35f;
    public static readonly TimeSpan IdleAfter = TimeSpan.FromSeconds(4);
    public const float IdleMotion = 0.6f;

    private SoundFeatures? _features;
    private float _motion;
    private Vector2 _parallax;
    private double _time;
    private float _energy;

    public SoundFeatures? Features
    {
        get => _features;
        set => _features = value;
    }

    public float Motion => _motion;

    /// <summary>空间已经完全静止（不需要继续出帧）。</summary>
    public bool IsSettled { get; private set; } = true;

    public static float TargetMotion(in SpaceInput input)
    {
        if (input.ReduceMotion || !input.Playing) return 0f;
        float m = 1f;
        if (input.PointerIdle >= IdleAfter) m *= IdleMotion;
        return m * ModeProfile.Of(input.Mode).Amplitude;
    }

    /// <summary>重音越密集，单次脉冲越弱：孤立的重音为 1，每半秒一个时约为 0.35。</summary>
    internal static float Spacing(SoundFeatures f, int strong)
    {
        int prev = strong - 1;
        while (prev >= 0 && !f.Onsets[prev].Strong) prev--;
        if (prev < 0) return 1f;
        float gap = f.Onsets[strong].Time - f.Onsets[prev].Time;
        return Math.Clamp(gap / PulseSpacingSeconds, RepeatedPulseFloor, 1f);
    }

    public SpaceFrame Step(in SpaceInput input, double dt)
    {
        dt = Math.Clamp(dt, 0, 0.25);

        // 运动量：线性趋近目标（下降慢、上升快），减少动态效果时立即为 0。
        float target = TargetMotion(input);
        if (input.ReduceMotion) _motion = 0;
        else if (_motion < target) _motion = Math.Min(target, _motion + (float)dt / WakeSeconds);
        else if (_motion > target)
        {
            // 缓出：指数趋近目标，最后一点直接落到目标，保证真正静止
            _motion = target + (_motion - target) * MathF.Exp(-(float)dt / SettleTimeConstant);
            if (_motion - target < 0.004f) _motion = target;
        }

        // 视差：缓动跟随鼠标，鼠标离开时回到中心。
        // 暂停时视差冻结在当前位置：空间整体归于平静，不再跟随鼠标
        var parallaxTarget = input.ReduceMotion ? Vector2.Zero
            : !input.Playing ? _parallax
            : input.Pointer is null ? Vector2.Zero
            : Vector2.Clamp(input.Pointer.Value, new Vector2(-1), new Vector2(1)) * ParallaxAmount;
        float k = 1 - MathF.Exp(-(float)dt / ParallaxEaseSeconds);
        _parallax += (parallaxTarget - _parallax) * k;
        if (Vector2.DistanceSquared(_parallax, parallaxTarget) < 1e-10f) _parallax = parallaxTarget;

        _time += dt * _motion;

        var sample = _features?.Sample(input.Position) ?? default;
        // 长时段能量：只在播放时随音乐变化，暂停时保持
        if (input.Playing && !input.ReduceMotion)
        {
            float loud = (sample.Low + sample.Mid + sample.High) / 3f;
            _energy += (loud - _energy) * (1 - MathF.Exp(-(float)dt / EnergySeconds));
        }
        float pulse = 0;
        int strong = -1;
        if (_features is { } f && (strong = f.LastStrongOnsetAtOrBefore(input.Position)) >= 0)
        {
            var onset = f.Onsets[strong];
            float since = (float)input.Position.TotalSeconds - onset.Time;
            pulse = since < PulseDecaySeconds * 6 ? onset.Strength * MathF.Exp(-since / PulseDecaySeconds) * Spacing(f, strong) : 0;
        }

        float m = _motion;
        IsSettled = m == 0 && _parallax == parallaxTarget;
        return new SpaceFrame(
            sample.Low * m,
            sample.Mid * m,
            sample.High * m,
            pulse * m,
            sample.Balance * m,
            _parallax,
            m,
            (float)_time,
            strong,
            _energy * m);
    }
}

/// <summary>空间气质的类别，由封面颜色决定。</summary>
public enum MoodKind
{
    /// <summary>蓝：深海、夜空、冷雾。</summary>
    Cool,
    /// <summary>红橙：暖光、流体、燃烧感。</summary>
    Warm,
    /// <summary>绿：森林、呼吸、漂浮尘埃。</summary>
    Verdant,
    /// <summary>黑白：玻璃、金属、极简粒子。</summary>
    Neutral,
}

/// <summary>
/// 从封面色样得到的空间气质：冷暖（−1 冷 .. 1 暖）、彩度、明度。
/// 只影响流向、速度和粒子颜色等"性格"，不影响音乐驱动的幅度。
/// </summary>
public readonly record struct SpaceMood(MoodKind Kind, float Warmth, float Chroma, float Brightness)
{
    public const float NeutralChroma = 0.12f;

    public static SpaceMood From(Palette palette)
    {
        var colors = palette.SpaceColors;
        double warm = 0, chroma = 0, light = 0, wsum = 0;
        double hx = 0, hy = 0;
        foreach (var c in colors)
        {
            double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0;
            double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b));
            double sat = max - min;
            double hue = Hue(r, g, b, max, sat);
            chroma += sat;
            light += c.RelativeLuminance;
            // 彩度加权的平均色相（用向量平均，避免 350° 和 10° 平均成 180°）
            hx += Math.Cos(hue * Math.PI / 180) * sat;
            hy += Math.Sin(hue * Math.PI / 180) * sat;
            wsum += sat;
            warm += (r - b) * sat;
        }
        int n = Math.Max(1, colors.Count);
        float chromaMean = (float)(chroma / n);
        float warmth = wsum > 1e-6 ? (float)Math.Clamp(warm / wsum, -1, 1) : 0;
        float brightness = (float)(light / n);

        MoodKind kind;
        if (chromaMean < NeutralChroma) kind = MoodKind.Neutral;
        else
        {
            double h = (Math.Atan2(hy, hx) * 180 / Math.PI + 360) % 360;
            kind = h switch
            {
                >= 75 and < 165 => MoodKind.Verdant,
                >= 165 and < 285 => MoodKind.Cool,
                _ => MoodKind.Warm,
            };
        }
        return new SpaceMood(kind, warmth, chromaMean, brightness);
    }

    private static double Hue(double r, double g, double b, double max, double delta)
    {
        if (delta <= 1e-9) return 0;
        double h = max == r ? (g - b) / delta % 6 : max == g ? (b - r) / delta + 2 : (r - g) / delta + 4;
        return (h * 60 + 360) % 360;
    }

    /// <summary>雾的流动方向（单位向量）：暖色向上升腾，冷色横向慢漂，绿色缓慢上浮，中性几乎不动。</summary>
    public Vector2 FlowDirection => Kind switch
    {
        MoodKind.Warm => Vector2.Normalize(new Vector2(0.25f, -1f)),
        MoodKind.Cool => Vector2.Normalize(new Vector2(1f, -0.15f)),
        MoodKind.Verdant => Vector2.Normalize(new Vector2(0.4f, -0.8f)),
        _ => Vector2.Normalize(new Vector2(1f, 0f)),
    };

    /// <summary>雾的流速倍率。</summary>
    public float FlowSpeed => Kind switch
    {
        MoodKind.Warm => 1.3f,
        MoodKind.Cool => 0.7f,
        MoodKind.Verdant => 0.9f,
        _ => 0.5f,
    };

    /// <summary>玻璃边缘的硬朗程度（菲涅尔指数）：中性封面更像玻璃和金属。</summary>
    public float GlassHardness => Kind == MoodKind.Neutral ? 5f : 3f;

    /// <summary>粒子颜色向白色混合的比例：中性封面的粒子偏白。</summary>
    public float ParticleWhiteness => Kind == MoodKind.Neutral ? 0.45f : 0.1f;
}
