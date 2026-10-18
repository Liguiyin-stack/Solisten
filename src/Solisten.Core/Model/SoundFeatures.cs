namespace Solisten.Core.Model;

/// <summary>一次重音。Strong 表示"明显重音"：只有它会触发扩散波、外扩等较大的反馈。</summary>
public readonly record struct Onset(float Time, float Strength, bool Strong);

/// <summary>某一时刻的音频特征（都已平滑并归一化）。</summary>
public readonly record struct FeatureSample(float Low, float Mid, float High, float Balance);

/// <summary>
/// 离线预计算的音频特征，驱动聆听空间：低频（呼吸）、中频（变形）、高频（细节）、
/// 左右声道平衡（偏移）以及重音时刻。每秒 <see cref="Rate"/> 个点，播放时按音频时钟读取。
/// </summary>
public sealed class SoundFeatures
{
    public const int CurrentVersion = 1;

    public SoundFeatures(TimeSpan duration, int rate, ushort[] low, ushort[] mid, ushort[] high, short[] balance, Onset[] onsets)
    {
        if (mid.Length != low.Length || high.Length != low.Length || balance.Length != low.Length)
            throw new ArgumentException("Band arrays must have the same length");
        Duration = duration;
        Rate = rate;
        Low = low;
        Mid = mid;
        High = high;
        Balance = balance;
        Onsets = onsets;
    }

    public TimeSpan Duration { get; }
    public int Rate { get; }

    /// <summary>0..65535 → 0..1。</summary>
    public ushort[] Low { get; }
    public ushort[] Mid { get; }
    public ushort[] High { get; }

    /// <summary>−32767（右）..32767（左）→ −1..1。</summary>
    public short[] Balance { get; }

    /// <summary>按时间升序。</summary>
    public Onset[] Onsets { get; }

    public int Length => Low.Length;

    /// <summary>线性插值取样；超出范围时取端点，没有数据时为 0。</summary>
    public FeatureSample Sample(TimeSpan t)
    {
        if (Length == 0) return default;
        double x = Math.Clamp(t.TotalSeconds * Rate, 0, Length - 1);
        int i = (int)x;
        int j = Math.Min(i + 1, Length - 1);
        float f = (float)(x - i);
        static float U(ushort a, ushort b, float f) => (a + (b - a) * f) / 65535f;
        return new FeatureSample(
            U(Low[i], Low[j], f),
            U(Mid[i], Mid[j], f),
            U(High[i], High[j], f),
            (Balance[i] + (Balance[j] - Balance[i]) * f) / 32767f);
    }

    /// <summary>不晚于 t 的最后一个明显重音的下标（二分查找），没有时为 −1。</summary>
    public int LastStrongOnsetAtOrBefore(TimeSpan t)
    {
        float s = (float)t.TotalSeconds;
        int lo = 0, hi = Onsets.Length - 1, found = -1;
        while (lo <= hi)
        {
            int mid = (lo + hi) >>> 1;
            if (Onsets[mid].Time <= s) { found = mid; lo = mid + 1; }
            else hi = mid - 1;
        }
        while (found >= 0 && !Onsets[found].Strong) found--;
        return found;
    }

    public void Write(Stream stream)
    {
        using var w = new BinaryWriter(stream, System.Text.Encoding.UTF8, leaveOpen: true);
        w.Write("SLSF"u8);
        w.Write(CurrentVersion);
        w.Write(Duration.Ticks);
        w.Write(Rate);
        w.Write(Length);
        foreach (var a in new[] { Low, Mid, High })
            w.Write(System.Runtime.InteropServices.MemoryMarshal.AsBytes(a.AsSpan()));
        w.Write(System.Runtime.InteropServices.MemoryMarshal.AsBytes(Balance.AsSpan()));
        w.Write(Onsets.Length);
        foreach (var o in Onsets)
        {
            w.Write(o.Time);
            w.Write(o.Strength);
            w.Write(o.Strong);
        }
    }

    public static SoundFeatures? Read(Stream stream)
    {
        using var r = new BinaryReader(stream, System.Text.Encoding.UTF8, leaveOpen: true);
        if (!r.ReadBytes(4).AsSpan().SequenceEqual("SLSF"u8)) return null;
        if (r.ReadInt32() != CurrentVersion) return null;
        var duration = TimeSpan.FromTicks(r.ReadInt64());
        int rate = r.ReadInt32();
        int n = r.ReadInt32();
        if (rate <= 0 || n < 0 || n > 1 << 24) return null;

        T[] ReadArray<T>(int count) where T : unmanaged
        {
            var a = new T[count];
            var bytes = System.Runtime.InteropServices.MemoryMarshal.AsBytes(a.AsSpan());
            if (r.Read(bytes) != bytes.Length) throw new EndOfStreamException();
            return a;
        }

        var low = ReadArray<ushort>(n);
        var mid = ReadArray<ushort>(n);
        var high = ReadArray<ushort>(n);
        var balance = ReadArray<short>(n);
        int count = r.ReadInt32();
        if (count < 0 || count > 1 << 20) return null;
        var onsets = new Onset[count];
        for (int i = 0; i < count; i++) onsets[i] = new Onset(r.ReadSingle(), r.ReadSingle(), r.ReadBoolean());
        return new SoundFeatures(duration, rate, low, mid, high, balance, onsets);
    }
}
