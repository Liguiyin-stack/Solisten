using Solisten.Core.Audio;
using Solisten.Core.Model;

namespace Solisten.Library.Analysis;

/// <summary>
/// 离线计算驱动聆听空间的音频特征：
/// <list type="bullet">
/// <item>三个频段（低通 150 Hz、带通 300 Hz–2 kHz、高通 4 kHz）每 10 ms 的 RMS，按第 95 百分位归一化，
///   再用不同的起落时间平滑：低频重而慢，高频轻而快；</item>
/// <item>重音：对数能量的正向差分超过 0.5 秒滑动中位数时记一次，最小间隔 120 ms；
///   强度排在前 20% 且距上一个明显重音至少 400 ms 的标为"明显重音"；</item>
/// <item>左右声道平衡 (L² − R²) / (L² + R²)，静音时为 0。</item>
/// </list>
/// </summary>
public static class SoundFeatureAnalyzer
{
    public const int Rate = 100;

    public static SoundFeatures Analyze(IPcmReader reader, CancellationToken ct = default)
    {
        var acc = new Accumulator(reader.SampleRate, reader.Channels, reader.Duration);
        PcmPump.Run(reader, ct, acc);
        return acc.Finish();
    }

    // 平滑参数（秒）：起、落
    internal static readonly (double Attack, double Release) LowSmoothing = (0.080, 0.450);
    internal static readonly (double Attack, double Release) MidSmoothing = (0.040, 0.250);
    internal static readonly (double Attack, double Release) HighSmoothing = (0.010, 0.120);
    internal const double BalanceSmoothing = 0.200;

    private const double OnsetMedianWindow = 0.5;
    private const double OnsetMinInterval = 0.120;
    private const double StrongMinInterval = 0.400;
    private const double StrongPercentile = 0.80;
    /// <summary>对数能量（log10）至少上升这么多才算重音（约 3 dB）。</summary>
    private const double OnsetMinRise = 0.15;
    /// <summary>低于整曲最大能量 −50 dB 的块视为静音。</summary>
    private const double SilenceFloorLog = -5.0;

    public sealed class Accumulator : IPcmSink
    {
        private readonly int _rate;
        private readonly int _channels;
        private readonly double _blockLength;
        private double _nextBoundary;
        private long _totalFrames;

        private readonly Biquad _low1, _low2;
        private readonly Biquad _midHp, _midLp;
        private readonly Biquad _high1, _high2;

        private double _sLow, _sMid, _sHigh, _sAll, _sL, _sR;
        private int _count;
        private readonly List<(double Low, double Mid, double High, double All, double L, double R, int N)> _blocks;

        public Accumulator(int rate, int channels, TimeSpan durationHint)
        {
            _rate = rate;
            _channels = Math.Max(1, channels);
            _blockLength = rate / (double)Rate;
            _nextBoundary = _blockLength;
            _low1 = Biquad.LowPass(rate, 150);
            _low2 = Biquad.LowPass(rate, 150);
            _midHp = Biquad.HighPass(rate, 300);
            _midLp = Biquad.LowPass(rate, 2000);
            _high1 = Biquad.HighPass(rate, Math.Min(4000, rate * 0.45));
            _high2 = Biquad.HighPass(rate, Math.Min(4000, rate * 0.45));
            _blocks = new((int)Math.Max(16, durationHint.TotalSeconds * Rate + 16));
        }

        public void Add(ReadOnlySpan<float> interleaved)
        {
            int ch = _channels;
            int frames = interleaved.Length / ch;
            for (int f = 0; f < frames; f++)
            {
                double l = interleaved[f * ch];
                double r = ch > 1 ? interleaved[f * ch + 1] : l;
                double mono = 0;
                for (int c = 0; c < ch; c++) mono += interleaved[f * ch + c];
                mono /= ch;

                double lo = _low2.Process(_low1.Process(mono));
                double mi = _midLp.Process(_midHp.Process(mono));
                double hi = _high2.Process(_high1.Process(mono));

                _sLow += lo * lo;
                _sMid += mi * mi;
                _sHigh += hi * hi;
                _sAll += mono * mono;
                _sL += l * l;
                _sR += r * r;
                _count++;

                if (_totalFrames + f + 1 >= (long)Math.Round(_nextBoundary))
                {
                    _nextBoundary += _blockLength;
                    Flush();
                }
            }
            _totalFrames += frames;
        }

        private void Flush()
        {
            if (_count == 0) return;
            _blocks.Add((_sLow, _sMid, _sHigh, _sAll, _sL, _sR, _count));
            _sLow = _sMid = _sHigh = _sAll = _sL = _sR = 0;
            _count = 0;
        }

        /// <summary>已经送入的数据在时间轴上的位置（帧）。</summary>
        public long Position => _totalFrames;

        /// <summary>有一段没有数据（跳转跳过了，或从中途开始）：只适合实时显示，不能当作完整的离线特征保存。</summary>
        public bool HasGaps => _padFrom >= 0;

        private int _padFrom = -1;

        /// <summary>
        /// 跳到时间轴上的另一个位置（播放中跳转）。向前跳过的部分补成静音块并记为空缺；
        /// 向后跳回时丢弃之后的块，从那里重新累积（重新播放过的空缺不再算空缺）。
        /// </summary>
        public void JumpTo(long frame)
        {
            if (frame == _totalFrames) return;
            _sLow = _sMid = _sHigh = _sAll = _sL = _sR = 0;
            _count = 0;
            int target = (int)Math.Floor(Math.Max(0, frame) / _blockLength);
            if (target < _blocks.Count)
            {
                _blocks.RemoveRange(target, _blocks.Count - target);
                if (_padFrom >= target) _padFrom = -1;
            }
            else if (target > _blocks.Count)
            {
                if (_padFrom < 0) _padFrom = _blocks.Count;
                int n = Math.Max(1, (int)Math.Round(_blockLength));
                while (_blocks.Count < target) _blocks.Add((0, 0, 0, 0, 0, 0, n));
            }
            _totalFrames = Math.Max(0, frame);
            _nextBoundary = (target + 1) * _blockLength;
        }

        /// <summary>按目前已有的完整块计算特征（不改变累积状态，可以继续 Add）。用于边播边分析。</summary>
        public SoundFeatures Snapshot() => Build(_blocks.Count, TimeSpan.FromSeconds(_blocks.Count * _blockLength / _rate));

        public SoundFeatures Finish()
        {
            Flush();
            return Build(_blocks.Count, TimeSpan.FromSeconds(_totalFrames / (double)_rate));
        }

        private SoundFeatures Build(int n, TimeSpan duration)
        {
            var low = new double[n];
            var mid = new double[n];
            var high = new double[n];
            var all = new double[n];
            var bal = new double[n];

            for (int i = 0; i < n; i++)
            {
                var b = _blocks[i];
                low[i] = Math.Sqrt(b.Low / b.N);
                mid[i] = Math.Sqrt(b.Mid / b.N);
                high[i] = Math.Sqrt(b.High / b.N);
                all[i] = b.All / b.N;
                double total = b.L + b.R;
                bal[i] = total > 1e-9 * b.N ? (b.L - b.R) / total : 0;
            }

            var onsets = DetectOnsets(all);

            return new SoundFeatures(duration, Rate,
                Quantize(Smooth(Normalize(low), LowSmoothing)),
                Quantize(Smooth(Normalize(mid), MidSmoothing)),
                Quantize(Smooth(Normalize(high), HighSmoothing)),
                QuantizeSigned(SmoothSymmetric(bal, BalanceSmoothing)),
                onsets);
        }
    }

    /// <summary>除以第 95 百分位并截断到 0..1；全静音时全为 0。</summary>
    internal static double[] Normalize(double[] v)
    {
        var result = new double[v.Length];
        if (v.Length == 0) return result;
        var sorted = (double[])v.Clone();
        Array.Sort(sorted);
        double p95 = sorted[(int)Math.Min(sorted.Length - 1, Math.Floor(sorted.Length * 0.95))];
        if (p95 <= 1e-6) p95 = sorted[^1];
        if (p95 <= 1e-6) return result;
        for (int i = 0; i < v.Length; i++) result[i] = Math.Min(1, v[i] / p95);
        return result;
    }

    /// <summary>单极点包络跟随：上升用 attack，下降用 release。</summary>
    internal static double[] Smooth(double[] v, (double Attack, double Release) t)
    {
        var r = new double[v.Length];
        double a = Math.Exp(-1.0 / (t.Attack * Rate));
        double d = Math.Exp(-1.0 / (t.Release * Rate));
        double y = 0;
        for (int i = 0; i < v.Length; i++)
        {
            double k = v[i] > y ? a : d;
            y = k * y + (1 - k) * v[i];
            r[i] = y;
        }
        return r;
    }

    internal static double[] SmoothSymmetric(double[] v, double seconds) => Smooth(v, (seconds, seconds));

    internal static Onset[] DetectOnsets(double[] energy)
    {
        int n = energy.Length;
        if (n < 3) return [];

        double max = 0;
        foreach (var e in energy) if (e > max) max = e;
        if (max <= 1e-12) return [];

        // 对数能量（相对整曲最大值），静音块钳到下限
        var log = new double[n];
        for (int i = 0; i < n; i++) log[i] = Math.Max(SilenceFloorLog, Math.Log10(energy[i] / max + 1e-12));

        // 正向差分（与前两块中较大者比较，抵抗块边界把一次上升拆成两半）
        var flux = new double[n];
        for (int i = 1; i < n; i++)
        {
            double prev = i >= 2 ? Math.Max(log[i - 1], log[i - 2]) : log[i - 1];
            flux[i] = Math.Max(0, log[i] - prev);
        }

        int half = (int)(OnsetMedianWindow * Rate / 2);
        int minGap = (int)Math.Round(OnsetMinInterval * Rate);
        var window = new List<double>(half * 2 + 1);
        var found = new List<(int Index, double Strength)>();
        int last = -minGap;
        for (int i = 1; i < n; i++)
        {
            if (flux[i] < OnsetMinRise || log[i] <= SilenceFloorLog + 0.5) continue;
            // 局部最大
            if (i + 1 < n && flux[i + 1] > flux[i]) continue;
            if (flux[i - 1] > flux[i]) continue;

            window.Clear();
            for (int j = Math.Max(0, i - half); j <= Math.Min(n - 1, i + half); j++) window.Add(flux[j]);
            window.Sort();
            double threshold = window[window.Count / 2] + OnsetMinRise;
            if (flux[i] < threshold) continue;
            if (i - last < minGap) continue;

            // 重音时刻取能量开始上升的那一块
            int at = i;
            while (at > 0 && at > i - 2 && log[at - 1] < log[at] - 0.05) at--;
            found.Add((at, flux[i] - threshold + OnsetMinRise));
            last = i;
        }
        if (found.Count == 0) return [];

        var strengths = found.Select(f => f.Strength).Order().ToArray();
        double strongCut = strengths[(int)Math.Min(strengths.Length - 1, Math.Floor(strengths.Length * StrongPercentile))];
        double maxStrength = strengths[^1];
        int strongGap = (int)Math.Round(StrongMinInterval * Rate);
        int lastStrong = -strongGap;

        var result = new Onset[found.Count];
        for (int k = 0; k < found.Count; k++)
        {
            var (idx, strength) = found[k];
            bool strong = strength >= strongCut && idx - lastStrong >= strongGap;
            if (strong) lastStrong = idx;
            result[k] = new Onset(idx / (float)Rate, (float)(strength / maxStrength), strong);
        }
        return result;
    }

    private static ushort[] Quantize(double[] v)
    {
        var r = new ushort[v.Length];
        for (int i = 0; i < v.Length; i++) r[i] = (ushort)Math.Round(Math.Clamp(v[i], 0, 1) * 65535);
        return r;
    }

    private static short[] QuantizeSigned(double[] v)
    {
        var r = new short[v.Length];
        for (int i = 0; i < v.Length; i++) r[i] = (short)Math.Round(Math.Clamp(v[i], -1, 1) * 32767);
        return r;
    }

    /// <summary>RBJ 双二阶滤波器（Q = 1/√2）。</summary>
    internal sealed class Biquad
    {
        private readonly double _b0, _b1, _b2, _a1, _a2;
        private double _x1, _x2, _y1, _y2;

        private Biquad(double b0, double b1, double b2, double a0, double a1, double a2)
        {
            _b0 = b0 / a0; _b1 = b1 / a0; _b2 = b2 / a0; _a1 = a1 / a0; _a2 = a2 / a0;
        }

        public static Biquad LowPass(double rate, double freq)
        {
            var (cos, alpha) = Coefficients(rate, freq);
            return new Biquad((1 - cos) / 2, 1 - cos, (1 - cos) / 2, 1 + alpha, -2 * cos, 1 - alpha);
        }

        public static Biquad HighPass(double rate, double freq)
        {
            var (cos, alpha) = Coefficients(rate, freq);
            return new Biquad((1 + cos) / 2, -(1 + cos), (1 + cos) / 2, 1 + alpha, -2 * cos, 1 - alpha);
        }

        private static (double Cos, double Alpha) Coefficients(double rate, double freq)
        {
            double w = 2 * Math.PI * freq / rate;
            return (Math.Cos(w), Math.Sin(w) / (2 * Math.Sqrt(0.5)));
        }

        public double Process(double x)
        {
            double y = _b0 * x + _b1 * _x1 + _b2 * _x2 - _a1 * _y1 - _a2 * _y2;
            _x2 = _x1; _x1 = x;
            _y2 = _y1; _y1 = y;
            return y;
        }
    }
}
