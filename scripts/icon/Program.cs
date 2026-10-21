// 用法：dotnet run --project scripts/icon -- src/Solisten.App/Assets
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

// Solisten 图标："一首歌，一个空间"。
// 深色的聆听空间（圆角方块）中央是一张发着暖光的唱片，两圈声波向外扩散，外圈上点缀几颗光粒子。
// 每个尺寸单独绘制：小尺寸省略外圈和粒子，线条加粗，保证 16px 也清楚。

var output = args.Length > 0 ? args[0] : ".";
Directory.CreateDirectory(output);
int[] sizes = [16, 20, 24, 32, 40, 48, 64, 96, 128, 256];

var pngs = new List<(int Size, byte[] Png)>();
foreach (var s in sizes) pngs.Add((s, Render(s)));
WriteIco(Path.Combine(output, "Solisten.ico"), pngs);
File.WriteAllBytes(Path.Combine(output, "Solisten-512.png"), Render(512));
// 第二个参数为 preview 时，另外输出各尺寸的 PNG 方便检查小图标
if (args.Length > 1 && args[1] == "preview")
    foreach (var (s, png) in pngs) File.WriteAllBytes(Path.Combine(output, $"preview-{s}.png"), png);
Console.WriteLine($"wrote {Path.GetFullPath(Path.Combine(output, "Solisten.ico"))}");

static Color C(string hex, byte a = 255)
{
    var c = (Color)ColorConverter.ConvertFromString(hex);
    c.A = a;
    return c;
}

static byte[] Render(int size)
{
    double s = size;
    bool tiny = size <= 24, small = size <= 40;
    var visual = new DrawingVisual();
    using (var dc = visual.RenderOpen())
    {
        // 聆听空间：圆角方块，左上深靛蓝 → 右下紫。
        double inset = tiny ? 0 : s * 0.03;
        var box = new Rect(inset, inset, s - inset * 2, s - inset * 2);
        double radius = box.Width * 0.24;
        var bg = new LinearGradientBrush(C("#141833"), C("#3D2470"), new Point(0, 0), new Point(1, 1));
        dc.DrawRoundedRectangle(bg, null, box, radius, radius);
        dc.PushClip(new RectangleGeometry(box, radius, radius));

        var center = new Point(s / 2, s * 0.5);
        // 暖色光晕：音乐在空间里亮起来
        var glow = new RadialGradientBrush(C("#FF8F7A", 150), C("#FF8F7A", 0)) { GradientOrigin = new Point(0.5, 0.5) };
        glow.GradientStops.Insert(1, new GradientStop(C("#E2507A", 70), 0.55));
        dc.DrawEllipse(glow, null, center, s * 0.46, s * 0.46);

        // 声波：两圈向外扩散、逐渐变淡
        double ring1 = s * (tiny ? 0.33 : 0.31), ring2 = s * 0.405;
        var white = Colors.White;
        dc.DrawEllipse(null, new Pen(new SolidColorBrush(Color.FromArgb((byte)(tiny ? 200 : 150), 255, 255, 255)), Math.Max(1.2, s * (tiny ? 0.06 : 0.026))), center, ring1, ring1);
        if (!small)
        {
            dc.DrawEllipse(null, new Pen(new SolidColorBrush(Color.FromArgb(70, 255, 255, 255)), s * 0.018), center, ring2, ring2);
            // 光粒子：落在外圈上，大小不一
            foreach (var (deg, r, a) in new[] { (-38.0, 0.022, 230), (148.0, 0.016, 190), (205.0, 0.011, 150), (64.0, 0.012, 160) })
            {
                double t = deg * Math.PI / 180;
                var p = new Point(center.X + Math.Cos(t) * ring2, center.Y + Math.Sin(t) * ring2);
                var dot = new RadialGradientBrush(Color.FromArgb((byte)a, 255, 255, 255), Color.FromArgb(0, 255, 255, 255));
                dc.DrawEllipse(dot, null, p, s * r * 2.2, s * r * 2.2);
                dc.DrawEllipse(new SolidColorBrush(Color.FromArgb((byte)a, 255, 255, 255)), null, p, s * r, s * r);
            }
        }

        // 唱片：暖白色的圆，中心一点深色
        double disc = s * (tiny ? 0.2 : 0.185);
        var discBrush = new LinearGradientBrush(C("#FFFFFF"), C("#FFD9C8"), new Point(0.2, 0.1), new Point(0.85, 0.95));
        dc.DrawEllipse(discBrush, null, center, disc, disc);
        if (!tiny)
        {
            dc.DrawEllipse(null, new Pen(new SolidColorBrush(Color.FromArgb(40, 61, 36, 112)), Math.Max(0.8, s * 0.008)), center, disc * 0.62, disc * 0.62);
            dc.DrawEllipse(new SolidColorBrush(C("#2B1D52")), null, center, disc * 0.2, disc * 0.2);
        }
        dc.Pop();

        // 边缘的一道细光（大尺寸才画），像程序里的柔光玻璃
        if (!small)
        {
            var edge = new LinearGradientBrush(Color.FromArgb(70, 255, 255, 255), Color.FromArgb(0, 255, 255, 255), new Point(0, 0), new Point(0.6, 0.6));
            dc.DrawRoundedRectangle(null, new Pen(edge, s * 0.008), new Rect(box.X + s * 0.004, box.Y + s * 0.004, box.Width - s * 0.008, box.Height - s * 0.008), radius, radius);
        }
    }

    var bmp = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
    bmp.Render(visual);
    var enc = new PngBitmapEncoder();
    enc.Frames.Add(BitmapFrame.Create(bmp));
    using var ms = new MemoryStream();
    enc.Save(ms);
    return ms.ToArray();
}

// ICO：每个尺寸一张 PNG（Windows Vista 起支持）。
static void WriteIco(string path, List<(int Size, byte[] Png)> images)
{
    using var w = new BinaryWriter(File.Create(path));
    w.Write((short)0); w.Write((short)1); w.Write((short)images.Count);
    int offset = 6 + 16 * images.Count;
    foreach (var (size, png) in images)
    {
        w.Write((byte)(size >= 256 ? 0 : size)); w.Write((byte)(size >= 256 ? 0 : size));
        w.Write((byte)0); w.Write((byte)0);
        w.Write((short)1); w.Write((short)32);
        w.Write(png.Length); w.Write(offset);
        offset += png.Length;
    }
    foreach (var (_, png) in images) w.Write(png);
}
