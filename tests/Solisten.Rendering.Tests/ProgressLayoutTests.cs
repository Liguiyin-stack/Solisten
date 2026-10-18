using Solisten.Rendering.Progress;

namespace Solisten.Rendering.Tests;

public class ProgressLayoutTests
{
    [Fact]
    public void Span_IsPaddedAndCappedAndCentered()
    {
        Assert.Equal((56f, 900 - 112f), ProgressLayout.Span(900));
        Assert.Equal((110f, 960f), ProgressLayout.Span(1180)); // 宽于 960 时居中
        var (left, width) = ProgressLayout.Span(2560);
        Assert.Equal(960f, width);
        Assert.Equal((2560 - 960) / 2f, left);
        Assert.Equal(32f, ProgressLayout.Span(400).Left);
    }

    [Fact]
    public void Thickness_GrowsOnHover()
    {
        Assert.Equal(ProgressLayout.Thickness, ProgressLayout.ThicknessAt(0));
        Assert.Equal(ProgressLayout.HoverThickness, ProgressLayout.ThicknessAt(1));
        Assert.Equal(ProgressLayout.HoverThickness, ProgressLayout.ThicknessAt(5));
        Assert.True(ProgressLayout.Thickness <= 3f);
    }

    [Fact]
    public void TimeAndX_RoundTripAndClamp()
    {
        var d = TimeSpan.FromMinutes(4);
        Assert.Equal(100f, ProgressLayout.XAt(TimeSpan.Zero, d, 100, 800));
        Assert.Equal(500f, ProgressLayout.XAt(TimeSpan.FromMinutes(2), d, 100, 800));
        Assert.Equal(TimeSpan.FromMinutes(2), ProgressLayout.TimeAt(500, d, 100, 800));
        Assert.Equal(d, ProgressLayout.TimeAt(5000, d, 100, 800));
        Assert.Equal(TimeSpan.Zero, ProgressLayout.TimeAt(-5, d, 100, 800));
    }

    [Theory]
    [InlineData(0, "00:00")]
    [InlineData(192, "03:12")]
    [InlineData(3725, "1:02:05")]
    public void FormatTime(int seconds, string expected) => Assert.Equal(expected, ProgressLayout.FormatTime(TimeSpan.FromSeconds(seconds)));
}
