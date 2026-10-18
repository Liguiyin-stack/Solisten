namespace Solisten.Core.Model;

/// <summary>
/// 波形包络。Overview 是整曲 2048 个 bin（L2）；Detail 是固定时间分辨率的高密度包络（L3 当前聆听窗口）。
/// 所有值都已对数压缩并量化到 0..65535。
/// </summary>
public sealed class WaveformData
{
    public const int OverviewBins = 2048;
    public const int CurrentVersion = 1;

    public WaveformData(TimeSpan duration, ushort[] overviewRms, ushort[] overviewPeak, int detailRate, ushort[] detailRms, ushort[] detailPeak)
    {
        if (overviewRms.Length != overviewPeak.Length || detailRms.Length != detailPeak.Length)
            throw new ArgumentException("RMS and peak arrays must have the same length");
        Duration = duration;
        OverviewRms = overviewRms;
        OverviewPeak = overviewPeak;
        DetailRate = detailRate;
        DetailRms = detailRms;
        DetailPeak = detailPeak;
    }

    public TimeSpan Duration { get; }
    public ushort[] OverviewRms { get; }
    public ushort[] OverviewPeak { get; }

    /// <summary>Detail 每秒的点数。</summary>
    public int DetailRate { get; }
    public ushort[] DetailRms { get; }
    public ushort[] DetailPeak { get; }

    public static float ToUnit(ushort v) => v / 65535f;

    public void Write(Stream stream)
    {
        using var w = new BinaryWriter(stream, System.Text.Encoding.UTF8, leaveOpen: true);
        w.Write("SLWF"u8);
        w.Write(CurrentVersion);
        w.Write(Duration.Ticks);
        w.Write(OverviewRms.Length);
        w.Write(DetailRate);
        w.Write(DetailRms.Length);
        foreach (var a in new[] { OverviewRms, OverviewPeak, DetailRms, DetailPeak })
            w.Write(System.Runtime.InteropServices.MemoryMarshal.AsBytes(a.AsSpan()));
    }

    public static WaveformData? Read(Stream stream)
    {
        using var r = new BinaryReader(stream, System.Text.Encoding.UTF8, leaveOpen: true);
        if (!r.ReadBytes(4).AsSpan().SequenceEqual("SLWF"u8)) return null;
        if (r.ReadInt32() != CurrentVersion) return null;
        var duration = TimeSpan.FromTicks(r.ReadInt64());
        int overview = r.ReadInt32();
        int rate = r.ReadInt32();
        int detail = r.ReadInt32();
        if (overview < 0 || detail < 0 || overview > 1 << 16 || detail > 1 << 24) return null;

        ushort[] ReadArray(int n)
        {
            var a = new ushort[n];
            var bytes = System.Runtime.InteropServices.MemoryMarshal.AsBytes(a.AsSpan());
            if (r.Read(bytes) != bytes.Length) throw new EndOfStreamException();
            return a;
        }
        return new WaveformData(duration, ReadArray(overview), ReadArray(overview), rate, ReadArray(detail), ReadArray(detail));
    }
}

/// <summary>sRGB 颜色（0..255）。</summary>
public readonly record struct Rgb(byte R, byte G, byte B)
{
    public string ToHex() => $"#{R:X2}{G:X2}{B:X2}";

    /// <summary>WCAG 相对亮度。</summary>
    public double RelativeLuminance
    {
        get
        {
            static double Lin(byte v)
            {
                double s = v / 255.0;
                return s <= 0.04045 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
            }
            return 0.2126 * Lin(R) + 0.7152 * Lin(G) + 0.0722 * Lin(B);
        }
    }

    /// <summary>WCAG 对比度（1..21）。</summary>
    public static double Contrast(Rgb a, Rgb b)
    {
        double la = a.RelativeLuminance, lb = b.RelativeLuminance;
        if (la < lb) (la, lb) = (lb, la);
        return (la + 0.05) / (lb + 0.05);
    }

    public static Rgb FromHex(string hex)
    {
        var s = hex.TrimStart('#');
        return new Rgb(Convert.ToByte(s[..2], 16), Convert.ToByte(s[2..4], 16), Convert.ToByte(s[4..6], 16));
    }
}

/// <summary>封面中最有代表性的 1–4 个颜色（值类型，保证 Palette 的相等比较按值进行）。</summary>
public readonly record struct SwatchSet(Rgb A, Rgb B, Rgb C, Rgb D, int Count)
{
    public static SwatchSet Of(IReadOnlyList<Rgb> colors)
    {
        int n = Math.Min(4, colors.Count);
        Rgb At(int i) => i < n ? colors[i] : default;
        return new SwatchSet(At(0), At(1), At(2), At(3), n);
    }

    public Rgb this[int i] => i switch { 0 => A, 1 => B, 2 => C, 3 => D, _ => throw new ArgumentOutOfRangeException(nameof(i)) };

    public IReadOnlyList<Rgb> ToList()
    {
        var list = new Rgb[Count];
        for (int i = 0; i < Count; i++) list[i] = this[i];
        return list;
    }
}

/// <summary>§6.3 主色：Base 是环境，Accent 是已播放区，Dim 是未播放区/遮罩，TextOnBase 保证 4.5:1 对比度。
/// Swatches 是给聆听空间上色的 2–4 个颜色（雾、光晕、粒子）。</summary>
public sealed record Palette(Rgb Base, Rgb Accent, Rgb Dim, Rgb TextOnBase, SwatchSet Swatches = default)
{
    /// <summary>空间用色：有色样时用色样；旧缓存没有色样时由 Base / Accent / Dim 推出。</summary>
    public IReadOnlyList<Rgb> SpaceColors => Swatches.Count >= 2 ? Swatches.ToList() : [Base, Accent, Dim];

    /// <summary>强调色在 Base 上至少要达到的对比度（WCAG 非文字图形 3:1）。</summary>
    public const double MinAccentContrast = 3.0;

    /// <summary>
    /// 画在 Base 上的"已播放"颜色：Accent 在 Base 上看得清时用 Accent，否则退回 TextOnBase。
    /// 旧版本分析出的主色只保证了 Accent 对 Dim 的对比度，这里在绘制时兜底。
    /// </summary>
    public Rgb AccentOnBase => Rgb.Contrast(Accent, Base) >= MinAccentContrast ? Accent : TextOnBase;

    public static readonly Palette Neutral = new(new Rgb(0x2B, 0x2D, 0x33), new Rgb(0xC9, 0xD1, 0xDE), new Rgb(0x16, 0x17, 0x1A), new Rgb(0xFF, 0xFF, 0xFF));
}
