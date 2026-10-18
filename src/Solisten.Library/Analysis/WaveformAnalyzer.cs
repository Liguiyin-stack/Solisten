using Solisten.Core.Audio;
using Solisten.Core.Model;

namespace Solisten.Library.Analysis;

/// <summary>
/// §6.3 波形算法：解码全曲并下混为单声道；按固定时间块累计能量与峰值；
/// 整曲平均分成 2048 个 bin，显示值 v = log10(1 + 9·rms/maxRms)，3 点平滑后量化为 UInt16。
/// 同时保留每秒 <see cref="DetailRate"/> 个点的高密度包络。
/// </summary>
public static class WaveformAnalyzer
{
    public const int DetailRate = 100;
    public const int AnalysisSampleRate = 22050;

    public static WaveformData Analyze(IPcmReader reader, CancellationToken ct = default)
    {
        var acc = new Accumulator(reader.SampleRate, reader.Channels, reader.Duration);
        PcmPump.Run(reader, ct, acc);
        return acc.Finish();
    }

    /// <summary>可以和其他分析器共用一次解码的增量版本。</summary>
    public sealed class Accumulator(int rate, int channels, TimeSpan durationHint) : IPcmSink
    {
        private readonly int _channels = Math.Max(1, channels);
        // 块边界用小数累计，保证每秒恰好 DetailRate 个点（22050/100 不是整数）。
        private readonly double _blockLength = rate / (double)DetailRate;
        private double _nextBoundary = rate / (double)DetailRate;
        private readonly List<double> _sumSq = new((int)Math.Max(16, durationHint.TotalSeconds * DetailRate + 16));
        private readonly List<float> _peaks = new((int)Math.Max(16, durationHint.TotalSeconds * DetailRate + 16));
        private readonly List<int> _counts = new((int)Math.Max(16, durationHint.TotalSeconds * DetailRate + 16));
        private double _accSq;
        private float _accPeak;
        private int _accCount;
        private long _totalFrames;

        public void Add(ReadOnlySpan<float> interleaved)
        {
            int channels = _channels;
            int frames = interleaved.Length / channels;
            for (int f = 0; f < frames; f++)
            {
                float mono = 0;
                for (int c = 0; c < channels; c++) mono += interleaved[f * channels + c];
                mono /= channels;

                _accSq += mono * mono;
                float a = Math.Abs(mono);
                if (a > _accPeak) _accPeak = a;
                _accCount++;
                if (_totalFrames + f + 1 >= (long)Math.Round(_nextBoundary))
                {
                    _nextBoundary += _blockLength;
                    _sumSq.Add(_accSq);
                    _peaks.Add(_accPeak);
                    _counts.Add(_accCount);
                    _accSq = 0; _accPeak = 0; _accCount = 0;
                }
            }
            _totalFrames += frames;
        }

        public WaveformData Finish()
        {
            if (_accCount > 0)
            {
                _sumSq.Add(_accSq);
                _peaks.Add(_accPeak);
                _counts.Add(_accCount);
                _accCount = 0;
            }
            return Build(_sumSq, _peaks, _counts, TimeSpan.FromSeconds(_totalFrames / (double)rate));
        }
    }

    private static WaveformData Build(List<double> sumSq, List<float> peaks, List<int> counts, TimeSpan duration)
    {
        int blocks = sumSq.Count;
        if (blocks == 0)
        {
            var zeros = new ushort[WaveformData.OverviewBins];
            return new WaveformData(duration, zeros, (ushort[])zeros.Clone(), DetailRate, [], []);
        }

        // 整曲 2048 bin
        int bins = WaveformData.OverviewBins;
        var binRms = new double[bins];
        var binPeak = new double[bins];
        for (int b = 0; b < bins; b++)
        {
            int start = (int)((long)b * blocks / bins);
            int end = Math.Max(start + 1, (int)((long)(b + 1) * blocks / bins));
            end = Math.Min(end, blocks);
            start = Math.Min(start, blocks - 1);
            double sq = 0; long cnt = 0; float pk = 0;
            for (int i = start; i < end; i++)
            {
                sq += sumSq[i];
                cnt += counts[i];
                if (peaks[i] > pk) pk = peaks[i];
            }
            binRms[b] = cnt > 0 ? Math.Sqrt(sq / cnt) : 0;
            binPeak[b] = pk;
        }
        var overviewRms = Smooth3(Compress(binRms));
        var overviewPeak = Smooth3(Compress(binPeak));

        // 高密度包络
        var detailRms = new double[blocks];
        var detailPeak = new double[blocks];
        for (int i = 0; i < blocks; i++)
        {
            detailRms[i] = Math.Sqrt(sumSq[i] / counts[i]);
            detailPeak[i] = peaks[i];
        }

        return new WaveformData(duration,
            Quantize(overviewRms), Quantize(overviewPeak),
            DetailRate, Quantize(Compress(detailRms)), Quantize(Compress(detailPeak)));
    }

    /// <summary>v = log10(1 + 9·x/max)，让安静段落也看得见。全静音时全部为 0。</summary>
    internal static double[] Compress(double[] values)
    {
        double max = 0;
        foreach (var v in values) if (v > max) max = v;
        var result = new double[values.Length];
        if (max <= 1e-9) return result;
        for (int i = 0; i < values.Length; i++) result[i] = Math.Log10(1 + 9 * values[i] / max);
        return result;
    }

    internal static double[] Smooth3(double[] v)
    {
        if (v.Length < 3) return v;
        var r = new double[v.Length];
        r[0] = (v[0] * 2 + v[1]) / 3;
        for (int i = 1; i < v.Length - 1; i++) r[i] = (v[i - 1] + v[i] + v[i + 1]) / 3;
        r[^1] = (v[^1] * 2 + v[^2]) / 3;
        return r;
    }

    internal static ushort[] Quantize(double[] v)
    {
        var r = new ushort[v.Length];
        for (int i = 0; i < v.Length; i++) r[i] = (ushort)Math.Round(Math.Clamp(v[i], 0, 1) * 65535);
        return r;
    }
}
