namespace Solisten.Rendering.Progress;

/// <summary>极简进度线的纯计算部分（不依赖 GPU，可单元测试）。</summary>
public static class ProgressLayout
{
    public const float Thickness = 3f;
    public const float HoverThickness = 6f;
    public const float BubbleRoom = 30f;
    private const float SidePadding = 56f;
    private const float MaxWidth = 960f;

    /// <summary>进度线的水平范围：两侧留边，宽窗口时居中且不超过 960 DIP。</summary>
    public static (float Left, float Width) Span(float width)
    {
        float pad = Math.Min(SidePadding, width * 0.08f);
        float w = Math.Max(1, Math.Min(MaxWidth, width - 2 * pad));
        return ((width - w) / 2, w);
    }

    /// <summary>悬停时的粗细：hover ∈ [0, 1]。</summary>
    public static float ThicknessAt(float hover) => Thickness + (HoverThickness - Thickness) * Math.Clamp(hover, 0, 1);

    public static float XAt(TimeSpan t, TimeSpan duration, float left, float width) =>
        duration <= TimeSpan.Zero ? left : left + width * (float)Math.Clamp(t / duration, 0, 1);

    public static TimeSpan TimeAt(float x, TimeSpan duration, float left, float width) =>
        width <= 0 ? TimeSpan.Zero : duration * Math.Clamp((x - left) / width, 0, 1);

    public static string FormatTime(TimeSpan t)
    {
        if (t < TimeSpan.Zero) t = TimeSpan.Zero;
        return t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"mm\:ss");
    }
}
