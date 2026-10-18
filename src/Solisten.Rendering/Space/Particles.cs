using System.Numerics;

namespace Solisten.Rendering.Space;

/// <summary>
/// 被封面吸引、也从封面释放的粒子，以及明显重音时的扩散波（纯计算，绘制在 <see cref="SoundSpace"/> 中完成）。
/// <list type="bullet">
/// <item>粒子从封面边缘释放，绕封面缓慢公转，被弹簧拉回封面外的一圈"环带"；</item>
/// <item>安静段落环带收紧、转得慢；高潮（长时段能量高）时环带扩大；</item>
/// <item>明显重音给所有粒子一次向外的冲量，再被慢慢拉回；</item>
/// <item>时间都用空间自己的动画时钟（随 Motion 前进），暂停后粒子逐渐减速停下。</item>
/// </list>
/// </summary>
public sealed class Particles
{
    public const int Capacity = 300;
    public const float RippleSeconds = 1.4f;
    /// <summary>两圈扩散波之间至少相隔这么久（空间时间）：节奏密集时不会每一拍都出现。</summary>
    public const float MinRippleInterval = 2.0f;
    private const float AmbientPerSecond = 6f;
    private const float SparklePerSecond = 70f;
    private const float Spring = 1.6f;
    private const float Damping = 1.1f;

    public struct Particle
    {
        public Vector2 Position;
        public Vector2 Velocity;
        public float Age;
        public float Life;
        public float Size;
        public int Color;
        /// <summary>这颗粒子偏好的环带位置（相对封面半边长的倍数，再加上能量带来的扩张）。</summary>
        public float Orbit;
        /// <summary>公转方向（+1 / −1）。</summary>
        public float Spin;
    }

    public readonly record struct Ripple(float Start, float Strength);

    private readonly Particle[] _items = new Particle[Capacity];
    private readonly List<Ripple> _ripples = [];
    private readonly Random _rng;
    private int _count;
    private float _spawnCarry;
    private int _lastOnset = -1;
    private float _lastRipple = float.NegativeInfinity;

    public Particles(int seed = 7) => _rng = new Random(seed);

    public int Count => _count;
    public ReadOnlySpan<Particle> Items => _items.AsSpan(0, _count);
    public IReadOnlyList<Ripple> Ripples => _ripples;
    public bool IsEmpty => _count == 0 && _ripples.Count == 0;

    public void Clear()
    {
        _count = 0;
        _ripples.Clear();
        _spawnCarry = 0;
        _lastOnset = -1;
        _lastRipple = float.NegativeInfinity;
    }

    /// <summary>环带半径（像素）：封面外沿之外，随能量扩大。</summary>
    public static float RingRadius(float orbit, float half, float energy) => half * (orbit + 0.45f * energy);

    /// <param name="dt">空间时间的增量（已乘以 Motion）。</param>
    /// <param name="half">封面半边长（像素）。</param>
    public void Update(in SpaceFrame frame, float dt, in ModeProfile profile, Vector2 center, float half, Vector2 flow, float flowSpeed, int colors)
    {
        // 新的明显重音：所有粒子向外扩散一次，并从封面边缘释放一小批；允许时再加一圈扩散波
        if (frame.StrongOnset != _lastOnset)
        {
            bool isNew = frame.StrongOnset > _lastOnset;
            _lastOnset = frame.StrongOnset;
            if (isNew && frame.Pulse > 0.05f)
            {
                float kick = half * 1.1f * Math.Clamp(frame.Pulse, 0, 1);
                for (int i = 0; i < _count; i++)
                {
                    var d = _items[i].Position - center;
                    float len = d.Length();
                    if (len > 1e-3f) _items[i].Velocity += d / len * kick;
                }
                int burst = Math.Max(2, profile.MaxParticles / 12);
                for (int i = 0; i < burst; i++) Spawn(center, half, colors, profile.MaxParticles, outward: kick);

                if (profile.Ripples && frame.Time - _lastRipple >= MinRippleInterval)
                {
                    _lastRipple = frame.Time;
                    _ripples.Add(new Ripple(frame.Time, Math.Clamp(frame.Pulse, 0, 1)));
                }
            }
        }

        if (dt > 0)
        {
            // 平时少量释放，高频越多释放越多
            _spawnCarry += (AmbientPerSecond * frame.Motion + frame.Sparkle * SparklePerSecond) * dt;
            while (_spawnCarry >= 1)
            {
                _spawnCarry -= 1;
                Spawn(center, half, colors, profile.MaxParticles, outward: 0);
            }

            var drift = flow * flowSpeed * 4f;
            float omega = 0.12f + 0.3f * frame.Energy + 0.2f * frame.Bend;
            float damp = MathF.Exp(-dt * Damping);
            for (int i = 0; i < _count;)
            {
                ref var p = ref _items[i];
                var d = p.Position - center;
                float r = Math.Max(1e-3f, d.Length());
                var radial = d / r;
                var tangent = new Vector2(-radial.Y, radial.X) * p.Spin;

                // 弹簧拉回环带 + 沿环带公转
                float target = RingRadius(p.Orbit, half, frame.Energy);
                var accel = -radial * (r - target) * Spring + (tangent * omega * r - p.Velocity) * 0.6f;
                p.Velocity = (p.Velocity + accel * dt) * damp;
                p.Position += (p.Velocity + drift) * dt;
                p.Age += dt;
                if (p.Age >= p.Life)
                {
                    _items[i] = _items[--_count];
                    continue;
                }
                i++;
            }
        }

        float now = frame.Time;
        _ripples.RemoveAll(r => now - r.Start >= RippleSeconds || now < r.Start);
    }

    /// <summary>在封面边缘（正方形周长上的随机一点）释放一颗粒子。</summary>
    private void Spawn(Vector2 center, float half, int colors, int max, float outward)
    {
        if (_count >= Math.Min(max, Capacity)) return;
        float s = (float)_rng.NextDouble() * 4f;
        int side = Math.Min(3, (int)s);
        float t = (s - side) * 2f - 1f;
        var local = side switch
        {
            0 => new Vector2(t, -1),
            1 => new Vector2(1, t),
            2 => new Vector2(-t, 1),
            _ => new Vector2(-1, -t),
        };
        var pos = center + local * half * 1.02f;
        var dir = Vector2.Normalize(local);
        _items[_count++] = new Particle
        {
            Position = pos,
            Velocity = dir * (outward > 0 ? outward * (0.6f + (float)_rng.NextDouble() * 0.6f) : 8f + (float)_rng.NextDouble() * 10f),
            Age = 0,
            Life = 3f + (float)_rng.NextDouble() * 3f,
            Size = 0.8f + (float)_rng.NextDouble() * 1.6f,
            Color = colors <= 0 ? 0 : _rng.Next(colors),
            Orbit = 1.5f + (float)_rng.NextDouble() * 0.45f,
            Spin = _rng.Next(2) == 0 ? 1f : -1f,
        };
    }

    /// <summary>粒子的不透明度：淡入淡出（0..1）。</summary>
    public static float Alpha(in Particle p) => MathF.Sin(MathF.PI * Math.Clamp(p.Age / p.Life, 0, 1));

    /// <summary>扩散波：半径倍数（相对封面半边长）与不透明度。</summary>
    public static (float RadiusScale, float Alpha) RippleShape(in Ripple r, float now)
    {
        float age = Math.Clamp((now - r.Start) / RippleSeconds, 0, 1);
        float fade = (1 - age) * (1 - age);
        return (1.45f + age * 2.0f, r.Strength * 0.28f * fade);
    }
}
