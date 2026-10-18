using Solisten.Core.Model;

namespace Solisten.Library.Analysis;

/// <summary>
/// §6.3 主色算法：封面缩放到 64×64，转到 OKLab，k-means 聚成 5 类，按（像素占比 × 彩度）排序，
/// 输出 Base / Accent / Dim / TextOnBase，文字颜色按 WCAG 对比度选择。
/// </summary>
public static class PaletteExtractor
{
    public const int SampleSize = 64;
    public const int Clusters = 5;
    public const double MinTextContrast = 4.5;

    /// <param name="rgba">straight alpha 的 RGBA8 像素。</param>
    public static Palette Extract(ReadOnlySpan<byte> rgba)
    {
        var pixels = new List<OkLab>(rgba.Length / 4);
        for (int i = 0; i + 3 < rgba.Length; i += 4)
        {
            if (rgba[i + 3] < 128) continue;
            pixels.Add(OkLab.FromRgb(new Rgb(rgba[i], rgba[i + 1], rgba[i + 2])));
        }
        if (pixels.Count == 0) return Palette.Neutral;

        var clusters = KMeans(pixels, Math.Min(Clusters, pixels.Count));
        // 按（占比 × 彩度）排序；加一个小常数让灰阶封面也能选出主色。
        var ranked = clusters.OrderByDescending(c => c.Share * (c.Center.Chroma + 0.03)).ToList();

        var baseLab = ranked[0].Center;
        baseLab = baseLab with { L = Math.Clamp(baseLab.L, 0.18, 0.62) };

        var accentCluster = clusters
            .Where(c => c != ranked[0] || clusters.Count == 1)
            .OrderByDescending(c => c.Center.Chroma * Math.Sqrt(c.Share))
            .FirstOrDefault() ?? ranked[0];
        var accentLab = accentCluster.Center;

        var dimLab = new OkLab(Math.Min(baseLab.L * 0.45, 0.22), baseLab.A * 0.7, baseLab.B * 0.7);
        var dim = dimLab.ToRgb();

        // 已播放区要能从未播放区（Dim 上的 Base）中分辨：保证 Accent 对 Dim 至少 3:1。
        accentLab = accentLab with { L = Math.Max(accentLab.L, 0.55) };
        for (int i = 0; i < 20 && Contrast(accentLab.ToRgb(), dim) < 3.0; i++)
            accentLab = accentLab with { L = Math.Min(1, accentLab.L + 0.03) };

        // 文字对比度是硬性规则：白字/深色字都不到 4.5:1 时，像柔和遮罩一样把 Base 压暗或提亮。
        var baseRgb = baseLab.ToRgb();
        var text = TextColorFor(baseRgb);
        bool whiteText = text.R == 255;
        for (int i = 0; i < 40 && Contrast(text, baseRgb) < MinTextContrast; i++)
        {
            baseLab = baseLab with { L = Math.Clamp(baseLab.L + (whiteText ? -0.01 : 0.01), 0, 1) };
            baseRgb = baseLab.ToRgb();
        }
        // Accent 用来画"已播放"，它直接落在 Base 上：在 Base 上也要至少 3:1（白字背景偏暗→提亮，深色字背景偏亮→压暗）。
        for (int i = 0; i < 40 && Contrast(accentLab.ToRgb(), baseRgb) < Palette.MinAccentContrast; i++)
            accentLab = accentLab with { L = Math.Clamp(accentLab.L + (whiteText ? 0.02 : -0.02), 0, 1) };
        // 色样：按（占比 × 彩度）排序的聚类中心，去掉彼此太接近的，最多 4 个。
        var swatches = new List<OkLab>();
        foreach (var c in ranked)
        {
            if (swatches.All(s => s.DistanceSq(c.Center) > 0.004)) swatches.Add(c.Center);
            if (swatches.Count == 4) break;
        }
        if (swatches.Count < 2) swatches.Add(accentLab);
        return new Palette(baseRgb, accentLab.ToRgb(), dim, text, SwatchSet.Of([.. swatches.Select(s => s.ToRgb())]));
    }

    /// <summary>白色或近黑色中对比度更高者。</summary>
    public static Rgb TextColorFor(Rgb background)
    {
        var white = new Rgb(255, 255, 255);
        var ink = new Rgb(17, 17, 17);
        return Contrast(white, background) >= Contrast(ink, background) ? white : ink;
    }

    public static double RelativeLuminance(Rgb c) => c.RelativeLuminance;

    public static double Contrast(Rgb a, Rgb b) => Rgb.Contrast(a, b);

    internal sealed record Cluster(OkLab Center, double Share);

    internal static List<Cluster> KMeans(List<OkLab> pixels, int k, int iterations = 16)
    {
        var rng = new Random(20261018);
        var centers = new List<OkLab> { pixels[rng.Next(pixels.Count)] };

        // k-means++ 初始化（固定种子，结果可复现）
        var dist = new double[pixels.Count];
        while (centers.Count < k)
        {
            double total = 0;
            for (int i = 0; i < pixels.Count; i++)
            {
                double best = double.MaxValue;
                foreach (var c in centers) best = Math.Min(best, pixels[i].DistanceSq(c));
                dist[i] = best;
                total += best;
            }
            if (total <= 1e-12) break;
            double pick = rng.NextDouble() * total;
            int chosen = 0;
            for (double acc = 0; chosen < pixels.Count - 1; chosen++)
            {
                acc += dist[chosen];
                if (acc >= pick) break;
            }
            centers.Add(pixels[chosen]);
        }

        var assign = new int[pixels.Count];
        for (int it = 0; it < iterations; it++)
        {
            bool changed = false;
            for (int i = 0; i < pixels.Count; i++)
            {
                int best = 0;
                double bestD = double.MaxValue;
                for (int c = 0; c < centers.Count; c++)
                {
                    double d = pixels[i].DistanceSq(centers[c]);
                    if (d < bestD) { bestD = d; best = c; }
                }
                changed |= assign[i] != best;
                assign[i] = best;
            }

            var sums = new (double L, double A, double B, int N)[centers.Count];
            for (int i = 0; i < pixels.Count; i++)
            {
                ref var s = ref sums[assign[i]];
                s.L += pixels[i].L; s.A += pixels[i].A; s.B += pixels[i].B; s.N++;
            }
            for (int c = 0; c < centers.Count; c++)
            {
                if (sums[c].N > 0) centers[c] = new OkLab(sums[c].L / sums[c].N, sums[c].A / sums[c].N, sums[c].B / sums[c].N);
            }
            if (!changed && it > 0) break;
        }

        var counts = new int[centers.Count];
        foreach (var a in assign) counts[a]++;
        return centers.Select((c, i) => new Cluster(c, counts[i] / (double)pixels.Count)).Where(c => c.Share > 0).ToList();
    }
}

/// <summary>OKLab 色彩空间（Björn Ottosson, 2020）。</summary>
public readonly record struct OkLab(double L, double A, double B)
{
    public double Chroma => Math.Sqrt(A * A + B * B);

    public double DistanceSq(OkLab o)
    {
        double dl = L - o.L, da = A - o.A, db = B - o.B;
        return dl * dl + da * da + db * db;
    }

    public static OkLab FromRgb(Rgb c)
    {
        double r = ToLinear(c.R), g = ToLinear(c.G), b = ToLinear(c.B);
        double l = Math.Cbrt(0.4122214708 * r + 0.5363325363 * g + 0.0514459929 * b);
        double m = Math.Cbrt(0.2119034982 * r + 0.6806995451 * g + 0.1073969566 * b);
        double s = Math.Cbrt(0.0883024619 * r + 0.2817188376 * g + 0.6299787005 * b);
        return new OkLab(
            0.2104542553 * l + 0.7936177850 * m - 0.0040720468 * s,
            1.9779984951 * l - 2.4285922050 * m + 0.4505937099 * s,
            0.0259040371 * l + 0.7827717662 * m - 0.8086757660 * s);
    }

    public Rgb ToRgb()
    {
        double l = L + 0.3963377774 * A + 0.2158037573 * B;
        double m = L - 0.1055613458 * A - 0.0638541728 * B;
        double s = L - 0.0894841775 * A - 1.2914855480 * B;
        l *= l * l; m *= m * m; s *= s * s;
        double r = 4.0767416621 * l - 3.3077115913 * m + 0.2309699292 * s;
        double g = -1.2684380046 * l + 2.6097574011 * m - 0.3413193965 * s;
        double b = -0.0041960863 * l - 0.7034186147 * m + 1.7076147010 * s;
        return new Rgb(FromLinear(r), FromLinear(g), FromLinear(b));
    }

    private static double ToLinear(byte v)
    {
        double x = v / 255.0;
        return x <= 0.04045 ? x / 12.92 : Math.Pow((x + 0.055) / 1.055, 2.4);
    }

    private static byte FromLinear(double x)
    {
        x = Math.Clamp(x, 0, 1);
        double s = x <= 0.0031308 ? 12.92 * x : 1.055 * Math.Pow(x, 1 / 2.4) - 0.055;
        return (byte)Math.Round(Math.Clamp(s, 0, 1) * 255);
    }
}
