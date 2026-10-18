using System.Numerics;
using Solisten.Core.Model;
using Solisten.Rendering.Space;

namespace Solisten.Rendering.Tests;

public class CoverParticlesTests
{
    private const int Grid = 32;

    private static CoverParticles Build()
    {
        var px = new Rgb[Grid * Grid];
        for (int i = 0; i < px.Length; i++) px[i] = new Rgb((byte)(i % 256), 80, 160);
        var c = new CoverParticles();
        c.Build(Grid, px);
        return c;
    }

    private static SpaceFrame F(float time, float breath = 0, float bend = 0, float sparkle = 0, float pulse = 0, int onset = -1, float energy = 0, float motion = 1) =>
        new(breath, bend, sparkle, pulse, 0, Vector2.Zero, motion, time, onset, energy);

    private static float MeanRadius(CoverParticles c)
    {
        float s = 0;
        for (int i = 0; i < c.Count; i++) s += c.Position(i).Length();
        return s / c.Count;
    }

    private static float MaxOffset(CoverParticles c)
    {
        float m = 0;
        for (int i = 0; i < c.Count; i++) m = MathF.Max(m, c.Offset(i).Length());
        return m;
    }

    private static void Run(CoverParticles c, float seconds, Func<float, SpaceFrame> frame, ModeProfile? profile = null, float start = 0)
    {
        for (float t = 0; t < seconds; t += 1 / 60f) c.Update(frame(start + t), 1 / 60f, profile ?? ModeProfile.Immersive);
    }

    [Fact]
    public void Build_LaysOutAGridWithCoverColors()
    {
        var c = Build();
        Assert.Equal(Grid * Grid, c.Count);
        Assert.Equal(new Vector2(-1 + 1f / Grid, -1 + 1f / Grid), c.Home(0));
        Assert.Equal(new Vector4(5 / 255f, 80 / 255f, 160 / 255f, 1), c.Color(5));
        Assert.True(c.IsSettled);
    }

    [Fact]
    public void AtRest_ParticlesFormTheCover_WithASoftBorderlessEdge()
    {
        var c = Build();
        Run(c, 1, t => F(t, motion: 0));
        Assert.Equal(0f, MaxOffset(c));
        int center = Grid / 2 * Grid + Grid / 2, corner = 0;
        Assert.Equal(1f, c.Alpha(center));          // 中心完全不透明：封面清晰
        Assert.True(c.Alpha(corner) < 0.6f);        // 最外圈渐隐：没有边框
        Assert.True(c.Edge(corner) > 0.8f && c.Edge(center) == 0); // 最外圈格点在 ±(1 - 1/N)
    }

    [Fact]
    public void Breath_ExpandsTheCover()
    {
        var c = Build();
        float rest = MeanRadius(c);
        Run(c, 1.5f, t => F(t, breath: 1));
        Assert.True(MeanRadius(c) > rest * 1.06f, $"{rest} -> {MeanRadius(c)}");
    }

    [Fact]
    public void Breath_ScalesUniformly_SoTheCenterNeverTears()
    {
        var c = Build();
        Run(c, 1.5f, t => F(t, breath: 1));
        // 相邻两颗中心粒子之间的距离与边缘相邻粒子之间的距离一样（均匀缩放）
        int mid = Grid / 2;
        float center = Vector2.Distance(c.Position(mid * Grid + mid - 1), c.Position(mid * Grid + mid));
        float outer = Vector2.Distance(c.Position(mid * Grid + 4), c.Position(mid * Grid + 5));
        Assert.Equal(center, outer, 2);
    }

    [Fact]
    public void StrongOnset_BurstsOutward_ThenReturns()
    {
        var c = Build();
        float rest = MeanRadius(c);
        c.Update(F(0, pulse: 1, onset: 0), 1 / 60f, ModeProfile.Immersive);
        Run(c, 0.12f, t => F(t, onset: 0), start: 1 / 60f);
        float burst = MeanRadius(c);
        Assert.True(burst > rest * 1.04f, $"burst {rest} -> {burst}");

        Run(c, 2f, t => F(t, onset: 0), start: 0.2f);
        Assert.True(MathF.Abs(MeanRadius(c) / rest - 1) < 0.01f, $"return {MeanRadius(c) / rest}"); // 回到原位

        // 同一个重音不会重复触发
        c.Update(F(3, pulse: 1, onset: 0), 1 / 60f, ModeProfile.Immersive);
        Assert.True(MaxOffset(c) < 0.2f, $"repeat {MaxOffset(c)}"); // 只剩边缘的日常漂移；真正的炸开位移 > 1
    }

    [Fact]
    public void Pause_ReformsTheCover_AndSettles()
    {
        var c = Build();
        Run(c, 1, t => F(t, breath: 1, bend: 1, sparkle: 1, energy: 1));
        Assert.True(MaxOffset(c) > 0.05f);
        Assert.False(c.IsSettled);

        Run(c, 2, t => F(1 + t, motion: 0)); // 暂停：frame 的值都已乘以 0
        Assert.True(c.IsSettled);
        Assert.Equal(0f, MaxOffset(c));
        for (int i = 0; i < c.Count; i++) Assert.Equal(0f, c.Twinkle(i));
    }

    [Fact]
    public void Sparkle_TwinklesOnlyAFewParticles_PreferringTheEdge()
    {
        var c = Build();
        int edge = 0, center = 0, edgeN = 0, centerN = 0;
        for (int k = 0; k < 120; k++)
        {
            c.Update(F(k / 60f, sparkle: 1), 1 / 60f, ModeProfile.Immersive);
            for (int i = 0; i < c.Count; i++)
            {
                bool lit = c.Twinkle(i) > 0.5f;
                if (c.Edge(i) > 0.5f) { edgeN++; if (lit) edge++; }
                else if (c.Edge(i) == 0) { centerN++; if (lit) center++; }
            }
        }
        double edgeRate = edge / (double)edgeN, centerRate = center / (double)centerN;
        Assert.True(edgeRate > centerRate * 3, $"edge {edgeRate:P2} center {centerRate:P2}");
        Assert.True(centerRate < 0.05, $"center {centerRate:P2}");
    }

    [Fact]
    public void Scatter_ThenReassemblesIntoTheCover()
    {
        var c = Build();
        c.Scatter(1.5f);
        Assert.False(c.IsSettled);
        Assert.True(MaxOffset(c) > 1f);
        Run(c, 1.5f, t => F(t, motion: 0));
        Assert.True(c.IsSettled);
        Assert.Equal(0f, MaxOffset(c));
    }

    [Fact]
    public void GridDensity_FollowsMode()
    {
        Assert.True(CoverParticles.GridFor(SpaceMode.Quiet) < CoverParticles.GridFor(SpaceMode.Standard));
        Assert.True(CoverParticles.GridFor(SpaceMode.Standard) < CoverParticles.GridFor(SpaceMode.Immersive));
        Assert.True(CoverParticles.GridFor(SpaceMode.Immersive) * CoverParticles.GridFor(SpaceMode.Immersive) <= 16000);
    }

    [Fact]
    public void WithoutCover_UsesPaletteGradient()
    {
        var c = new CoverParticles();
        var pal = new Palette(new Rgb(0, 0, 0), new Rgb(255, 255, 255), new Rgb(0, 0, 0), new Rgb(255, 255, 255));
        c.Build(8, [], pal);
        Assert.True(c.Color(0).X < 0.2f);
        Assert.True(c.Color(63).X > 0.8f);
    }
}
