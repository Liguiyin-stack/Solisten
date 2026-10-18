using System.Numerics;
using Solisten.Core.Model;
using Solisten.Rendering.Space;

namespace Solisten.Rendering.Tests;

public class SpaceDynamicsTests
{
    /// <summary>10 秒：低频恒为 0.8，中频 0.5，高频 0.25，偏左 0.5；1 秒和 3 秒处各一个明显重音，2 秒处一个普通重音。</summary>
    private static SoundFeatures Features()
    {
        int n = 1000;
        return new SoundFeatures(TimeSpan.FromSeconds(10), 100,
            Enumerable.Repeat((ushort)(0.8 * 65535), n).ToArray(),
            Enumerable.Repeat((ushort)(0.5 * 65535), n).ToArray(),
            Enumerable.Repeat((ushort)(0.25 * 65535), n).ToArray(),
            Enumerable.Repeat((short)(0.5 * 32767), n).ToArray(),
            [new Onset(1f, 1f, true), new Onset(2f, 0.5f, false), new Onset(3f, 0.6f, true)]);
    }

    private static SpaceInput Playing(double seconds, SpaceMode mode = SpaceMode.Immersive, Vector2? pointer = null, double idle = 0) =>
        new(TimeSpan.FromSeconds(seconds), true, mode, false, pointer, TimeSpan.FromSeconds(idle));

    /// <summary>按 60fps 跑若干秒，位置随时间前进（暂停时不动）。</summary>
    private static SpaceFrame Run(SpaceDynamics d, Func<double, SpaceInput> input, double from, double seconds)
    {
        SpaceFrame f = default;
        for (double t = 0; t < seconds; t += 1 / 60.0) f = d.Step(input(from + t), 1 / 60.0);
        return f;
    }

    [Fact]
    public void Playing_MapsBandsToBreathBendSparkleAndSway()
    {
        var d = new SpaceDynamics { Features = Features() };
        var f = Run(d, t => Playing(t), 5, 1.0);
        Assert.Equal(1f, f.Motion);
        Assert.Equal(0.8f, f.Breath, 2);
        Assert.Equal(0.5f, f.Bend, 2);
        Assert.Equal(0.25f, f.Sparkle, 2);
        Assert.Equal(0.5f, f.Sway, 2);
    }

    [Fact]
    public void StrongOnset_PulsesAndDecays_WeakOnesDoNot()
    {
        var d = new SpaceDynamics { Features = Features() };
        Run(d, t => Playing(t), 0, 1.0); // 先达到全速
        var at = d.Step(Playing(1.0), 1 / 60.0);
        Assert.Equal(0, at.StrongOnset);
        Assert.Equal(1f, at.Pulse, 3);

        var later = d.Step(Playing(1.0 + SpaceDynamics.PulseDecaySeconds), 1 / 60.0);
        Assert.Equal(MathF.Exp(-1), later.Pulse, 2);

        // 2 秒处的普通重音不会触发；1 秒处的脉冲早已衰减
        var weak = d.Step(Playing(2.05), 1 / 60.0);
        Assert.Equal(0, weak.StrongOnset);
        Assert.True(weak.Pulse < 0.03f, $"pulse {weak.Pulse}"); // e^(-1.05/0.28) ≈ 0.023

        // 与上一个明显重音相隔 2 秒（≥ 1.5 秒）：不减弱
        var second = d.Step(Playing(3.0), 1 / 60.0);
        Assert.Equal(2, second.StrongOnset);
        Assert.Equal(0.6f, second.Pulse, 3);
    }

    [Fact]
    public void RegularBeats_PulseGently_IsolatedAccentsPulseFully()
    {
        // 每 0.5 秒一个同样强的明显重音，然后安静 3 秒后再来一个
        var onsets = Enumerable.Range(0, 8).Select(i => new Onset(1f + 0.5f * i, 1f, true)).Append(new Onset(7.5f, 1f, true)).ToArray();
        var f = new SoundFeatures(TimeSpan.FromSeconds(10), 100, new ushort[1000], new ushort[1000], new ushort[1000], new short[1000], onsets);
        var d = new SpaceDynamics { Features = f };
        Run(d, t => Playing(t), 0, 1.0);

        Assert.Equal(1f, d.Step(Playing(1.0), 1 / 60.0).Pulse, 3);             // 第一个：没有前一个
        Assert.Equal(SpaceDynamics.RepeatedPulseFloor, d.Step(Playing(2.0), 1 / 60.0).Pulse, 3); // 密集节拍
        Assert.Equal(1f, d.Step(Playing(7.5), 1 / 60.0).Pulse, 3);             // 孤立的重音
    }

    [Fact]
    public void Ripples_AreAtLeastTwoSecondsApart()
    {
        var p = new Particles();
        int ripples = 0;
        for (int beat = 0; beat < 8; beat++)
        {
            var frame = new SpaceFrame(0, 0, 0, 0.8f, 0, Vector2.Zero, 1, beat * 0.5f, beat);
            p.Update(frame, 0.5f, ModeProfile.Standard, Vector2.Zero, 100, Vector2.UnitX, 1, 3);
            ripples += p.Ripples.Count(r => r.Start == frame.Time);
        }
        Assert.Equal(2, ripples); // 0 秒和 2 秒
    }

    [Fact]
    public void Pause_SettlesSmoothlyWithinAbout600ms_AndFreezesTheClock()
    {
        var d = new SpaceDynamics { Features = Features() };
        Run(d, t => Playing(t), 4, 1.0);
        var paused = new SpaceInput(TimeSpan.FromSeconds(5), false, SpaceMode.Immersive, false, null, TimeSpan.Zero);

        // 缓出：前 150 ms 已经降了一大半，但还没有停
        var early = Run(d, _ => paused, 0, 0.15);
        Assert.InRange(early.Motion, 0.15f, 0.45f);
        Assert.False(d.IsSettled);

        var end = Run(d, _ => paused, 0, SpaceDynamics.SettleSeconds);
        Assert.Equal(0f, end.Motion);
        Assert.True(d.IsSettled);
        Assert.Equal(0f, end.Breath);

        // 静止后时钟不再前进：同一输入得到完全相同的帧
        var again = d.Step(paused, 1 / 60.0);
        Assert.Equal(end, again);
    }

    [Fact]
    public void Modes_AndIdlePointer_ScaleMotion()
    {
        Assert.Equal(1f, SpaceDynamics.TargetMotion(Playing(0)));
        Assert.Equal(ModeProfile.Standard.Amplitude, SpaceDynamics.TargetMotion(Playing(0, SpaceMode.Standard)));
        Assert.Equal(ModeProfile.Quiet.Amplitude, SpaceDynamics.TargetMotion(Playing(0, SpaceMode.Quiet)));
        Assert.Equal(SpaceDynamics.IdleMotion, SpaceDynamics.TargetMotion(Playing(0, idle: 5)));
        Assert.Equal(SpaceDynamics.IdleMotion * ModeProfile.Quiet.Amplitude, SpaceDynamics.TargetMotion(Playing(0, SpaceMode.Quiet, idle: 5)), 5);
        Assert.True(ModeProfile.Quiet.Amplitude < ModeProfile.Standard.Amplitude && ModeProfile.Standard.Amplitude < ModeProfile.Immersive.Amplitude);
        Assert.True(ModeProfile.Quiet.MaxParticles < ModeProfile.Standard.MaxParticles && ModeProfile.Standard.MaxParticles < ModeProfile.Immersive.MaxParticles);
        Assert.False(ModeProfile.Quiet.Ripples);

        var d = new SpaceDynamics { Features = Features() };
        var f = Run(d, t => Playing(t, SpaceMode.Quiet), 5, 2);
        Assert.Equal(0.8f * ModeProfile.Quiet.Amplitude, f.Breath, 2);
    }

    [Fact]
    public void Energy_RisesSlowly_AndHoldsWhilePaused()
    {
        var d = new SpaceDynamics { Features = Features() }; // 平均响度 (0.8 + 0.5 + 0.25) / 3
        float loud = (0.8f + 0.5f + 0.25f) / 3f;
        var one = Run(d, t => Playing(t), 0, 1);
        Assert.InRange(one.Energy, 0.15f * loud, 0.45f * loud); // 3 秒时间常数：1 秒后约 28%
        var settled = Run(d, t => Playing(t), 1, 20); // 7 个时间常数后 > 99.9%
        Assert.Equal(loud, settled.Energy, 2);

        // 暂停：Energy 输出随 Motion 归零，但内部能量保持，恢复播放后不需要重新积累
        var paused = new SpaceInput(TimeSpan.FromSeconds(9), false, SpaceMode.Immersive, false, null, TimeSpan.Zero);
        Assert.Equal(0f, Run(d, _ => paused, 0, 2).Energy);
        var resumed = Run(d, t => Playing(t), 9, 0.7);
        Assert.Equal(loud, resumed.Energy, 2);
    }

    [Fact]
    public void ReduceMotion_IsImmediatelyStill()
    {
        var d = new SpaceDynamics { Features = Features() };
        Run(d, t => Playing(t, pointer: new Vector2(1, 1)), 4, 1);
        var f = d.Step(Playing(5, pointer: new Vector2(1, 1)) with { ReduceMotion = true }, 1 / 60.0);
        Assert.Equal(0f, f.Motion);
        Assert.Equal(0f, f.Pulse);
        Run(d, t => Playing(t) with { ReduceMotion = true }, 5, 3);
        Assert.True(d.IsSettled);
    }

    [Fact]
    public void Parallax_EasesTowardsPointer_AndReturnsWhenItLeaves()
    {
        var d = new SpaceDynamics();
        var first = d.Step(Playing(0, pointer: new Vector2(1, -1)), 1 / 60.0);
        Assert.True(first.Parallax.X > 0 && first.Parallax.X < SpaceDynamics.ParallaxAmount * 0.2f);

        var settled = Run(d, _ => Playing(0, pointer: new Vector2(1, -1)), 0, 3);
        Assert.Equal(SpaceDynamics.ParallaxAmount, settled.Parallax.X, 4);
        Assert.Equal(-SpaceDynamics.ParallaxAmount, settled.Parallax.Y, 4);

        var gone = Run(d, _ => Playing(0), 0, 3);
        Assert.Equal(Vector2.Zero, gone.Parallax);
    }

    [Fact]
    public void Parallax_IsFrozenWhilePaused()
    {
        var d = new SpaceDynamics();
        var held = Run(d, _ => Playing(0, pointer: new Vector2(1, 0)), 0, 3).Parallax;
        var paused = new SpaceInput(TimeSpan.Zero, false, SpaceMode.Standard, false, new Vector2(-1, -1), TimeSpan.Zero);
        var f = Run(d, _ => paused, 0, 2);
        Assert.Equal(held, f.Parallax);
        Assert.True(d.IsSettled);
    }

    [Fact]
    public void WithoutFeatures_OnlyMotionAndParallaxChange()
    {
        var d = new SpaceDynamics();
        var f = Run(d, t => Playing(t), 0, 1);
        Assert.Equal(1f, f.Motion);
        Assert.Equal(0f, f.Breath);
        Assert.Equal(-1, f.StrongOnset);
        Assert.True(f.Time > 0.5f);
    }
}

public class SpaceMoodTests
{
    private static Palette P(params Rgb[] swatches) =>
        Palette.Neutral with { Swatches = SwatchSet.Of(swatches) };

    [Fact]
    public void BlueCover_IsCool()
    {
        var m = SpaceMood.From(P(new Rgb(20, 40, 120), new Rgb(60, 110, 200), new Rgb(10, 20, 50)));
        Assert.Equal(MoodKind.Cool, m.Kind);
        Assert.True(m.Warmth < -0.3f);
    }

    [Fact]
    public void RedOrangeCover_IsWarm()
    {
        var m = SpaceMood.From(P(new Rgb(200, 60, 30), new Rgb(240, 150, 60), new Rgb(120, 20, 20)));
        Assert.Equal(MoodKind.Warm, m.Kind);
        Assert.True(m.Warmth > 0.3f);
        Assert.True(m.FlowDirection.Y < 0); // 向上
    }

    [Fact]
    public void GreenCover_IsVerdant()
    {
        Assert.Equal(MoodKind.Verdant, SpaceMood.From(P(new Rgb(40, 110, 50), new Rgb(120, 180, 80))).Kind);
    }

    [Fact]
    public void GrayscaleCover_IsNeutral_WithHarderGlassAndWhiterParticles()
    {
        var m = SpaceMood.From(P(new Rgb(20, 20, 20), new Rgb(200, 200, 200), new Rgb(120, 118, 122)));
        Assert.Equal(MoodKind.Neutral, m.Kind);
        Assert.True(m.GlassHardness > SpaceMood.From(P(new Rgb(200, 60, 30), new Rgb(240, 150, 60))).GlassHardness);
        Assert.True(m.ParticleWhiteness > SpaceMood.From(P(new Rgb(200, 60, 30), new Rgb(240, 150, 60))).ParticleWhiteness);
    }

    [Fact]
    public void RedHues_AroundZeroDegrees_AverageCorrectly()
    {
        // 350° 和 10° 附近的两个红色：平均色相应在 0° 附近（暖），而不是 180°（冷）
        Assert.Equal(MoodKind.Warm, SpaceMood.From(P(new Rgb(220, 30, 60), new Rgb(220, 60, 30))).Kind);
    }

    [Fact]
    public void LegacyPalette_WithoutSwatches_UsesBaseAccentDim()
    {
        var legacy = new Palette(new Rgb(30, 60, 140), new Rgb(120, 170, 255), new Rgb(10, 20, 40), new Rgb(255, 255, 255));
        Assert.Equal(3, legacy.SpaceColors.Count);
        Assert.Equal(MoodKind.Cool, SpaceMood.From(legacy).Kind);
    }
}

public class ParticleTests
{
    private static readonly Vector2 Center = new(500, 400);
    private const float Half = 100;

    private static SpaceFrame Frame(float time, float energy = 0, float sparkle = 0, float pulse = 0, int onset = -1) =>
        new(0, 0, sparkle, pulse, 0, Vector2.Zero, 1, time, onset, energy);

    private static float MeanRadius(Particles p)
    {
        float sum = 0;
        foreach (var it in p.Items) sum += Vector2.Distance(it.Position, Center);
        return sum / Math.Max(1, p.Count);
    }

    private static void Run(Particles p, float seconds, float energy, ModeProfile profile, float sparkle = 0.5f, float start = 0)
    {
        for (float t = 0; t < seconds; t += 1 / 60f)
            p.Update(Frame(start + t, energy, sparkle), 1 / 60f, profile, Center, Half, Vector2.UnitX, 0, 3);
    }

    [Fact]
    public void Particles_AreReleasedAtTheCoverEdge()
    {
        var p = new Particles();
        p.Update(Frame(0, sparkle: 1), 0.2f, ModeProfile.Immersive, Center, Half, Vector2.Zero, 0, 3);
        Assert.True(p.Count > 0);
        foreach (var it in p.Items)
        {
            var d = Vector2.Abs(it.Position - Center);
            float cheb = MathF.Max(d.X, d.Y);
            Assert.InRange(cheb, Half * 0.95f, Half * 1.3f); // 在封面边缘附近
        }
    }

    [Fact]
    public void Particles_SettleIntoARingThatWidensWithEnergy()
    {
        var calm = new Particles(1);
        Run(calm, 6, energy: 0, ModeProfile.Immersive);
        var climax = new Particles(1);
        Run(climax, 6, energy: 1, ModeProfile.Immersive);

        float rc = MeanRadius(calm), rh = MeanRadius(climax);
        Assert.InRange(rc, Half * 1.3f, Half * 2.1f); // 环带在封面外，靠近封面
        Assert.True(rh > rc + Half * 0.25f, $"calm {rc} climax {rh}");
    }

    [Fact]
    public void StrongOnset_PushesParticlesOutward()
    {
        var p = new Particles(2);
        Run(p, 4, 0, ModeProfile.Standard);
        float before = MeanRadius(p);
        p.Update(Frame(4, pulse: 1, onset: 0), 1 / 60f, ModeProfile.Standard, Center, Half, Vector2.Zero, 0, 3);
        for (int i = 1; i < 15; i++) p.Update(Frame(4 + i / 60f, onset: 0), 1 / 60f, ModeProfile.Standard, Center, Half, Vector2.Zero, 0, 3);
        Assert.True(MeanRadius(p) > before * 1.1f, $"{before} -> {MeanRadius(p)}");
    }

    [Fact]
    public void ModeCapsParticleCount()
    {
        var quiet = new Particles();
        Run(quiet, 5, 0, ModeProfile.Quiet, sparkle: 1);
        Assert.True(quiet.Count <= ModeProfile.Quiet.MaxParticles);
        var immersive = new Particles();
        Run(immersive, 5, 0, ModeProfile.Immersive, sparkle: 1);
        Assert.True(immersive.Count > quiet.Count);
    }

    [Fact]
    public void ZeroTimeStep_FreezesParticles()
    {
        var p = new Particles();
        Run(p, 2, 0, ModeProfile.Standard);
        var before = p.Items.ToArray().Select(i => i.Position).ToArray();
        p.Update(Frame(2), 0, ModeProfile.Standard, Center, Half, Vector2.UnitX, 1, 3);
        Assert.Equal(before, p.Items.ToArray().Select(i => i.Position).ToArray());
    }
}

public class ZoneFadeTests
{
    [Fact]
    public void Particles_FadeOutNearTitleAndControls()
    {
        Assert.Equal(0f, SoundSpace.ZoneFade(50, 1000));   // 歌名
        Assert.Equal(1f, SoundSpace.ZoneFade(500, 1000));  // 封面周围
        Assert.Equal(0f, SoundSpace.ZoneFade(900, 1000));  // 进度线与控制
        Assert.InRange(SoundSpace.ZoneFade(210, 1000), 0.1f, 0.9f);
    }
}
