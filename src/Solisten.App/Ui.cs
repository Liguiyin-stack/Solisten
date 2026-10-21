using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace Solisten.App;

/// <summary>x:Bind 函数绑定用的小工具。</summary>
public static class Ui
{
    public static Visibility Visible(bool v) => v ? Visibility.Visible : Visibility.Collapsed;
    public static Visibility Collapsed(bool v) => v ? Visibility.Collapsed : Visibility.Visible;
    public static Visibility NotEmpty(string? s) => string.IsNullOrEmpty(s) ? Visibility.Collapsed : Visibility.Visible;
    public static Visibility Empty(string? s) => string.IsNullOrEmpty(s) ? Visibility.Visible : Visibility.Collapsed;

    public static string PlayGlyph(bool paused) => paused ? "\uE768" : "\uE769"; // 播放 / 暂停

    /// <summary>分组之间用更大的间距表达空间断层。</summary>
    public static Thickness CardMargin(bool groupBreak) => new(groupBreak ? 36 : 0, 0, 0, 0);

    public static Thickness LensThickness(bool focused) => new(focused ? 2 : 0);

    /// <summary>选中但不是正在播放的卡片上显示 ▶：再点一下就播放。</summary>
    public static Visibility PlayHint(bool focused, bool playing) => focused && !playing ? Visibility.Visible : Visibility.Collapsed;

    public static double AvailableOpacity(bool available) => available ? 1 : 0.4;

    public static ImageSource? Thumb(string? path)
    {
        if (string.IsNullOrEmpty(path)) return null;
        return new BitmapImage(new Uri(path)) { DecodePixelWidth = 228, DecodePixelType = DecodePixelType.Logical };
    }
}

/// <summary>信息层中的一个时间标记。</summary>
public sealed record MarkerRow(long Id, Solisten.Core.Model.TrackId Track, TimeSpan Position, string? Label)
{
    public string TimeText => Rendering.Progress.ProgressLayout.FormatTime(Position);
    public string SeekName => $"跳到 {TimeText}";
    public string LabelName => $"{TimeText} 标记名称";
    public string DeleteName => $"删除 {TimeText} 的标记";
}
