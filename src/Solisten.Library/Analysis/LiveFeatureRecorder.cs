using System.Buffers;
using System.Collections.Concurrent;
using Solisten.Core.Audio;
using Solisten.Core.Model;

namespace Solisten.Library.Analysis;

/// <summary>
/// 边播边分析：接收播放引擎解码出的 PCM（解码比播放领先约半秒），在独立线程上用与离线分析相同的算法累积音频特征。
/// <list type="bullet">
/// <item>实时：约每秒用"目前为止"的数据算一次特征（归一化、重音阈值都按已有部分计算），发出 <see cref="LiveUpdated"/>，
///   让还没有离线特征的歌一开始播放就有动画；</item>
/// <item>记录：一首歌从头到尾连续解码完（没有被跳过的空缺）时，结果与离线分析一致，发出 <see cref="Completed"/> 供保存，
///   之后再播放就直接使用缓存。</item>
/// </list>
/// 只保留特征，不保留音频。同一时间只记录正在解码的那一首。
/// </summary>
public sealed class LiveFeatureRecorder : IDecodeTap, IDisposable
{
    /// <summary>两次实时快照之间至少间隔多少秒的音频。</summary>
    public const double SnapshotSeconds = 1.0;

    private readonly Func<TrackId, bool> _accept;
    private readonly ConcurrentQueue<Item> _queue = new();
    private readonly AutoResetEvent _signal = new(false);
    private readonly Thread _worker;
    private volatile bool _disposed;
    private Session? _session;

    /// <param name="accept">哪些曲目需要边播边分析（在引擎线程上调用，必须很快）。</param>
    public LiveFeatureRecorder(Func<TrackId, bool> accept)
    {
        _accept = accept;
        _worker = new Thread(Work) { IsBackground = true, Name = "Solisten live features", Priority = ThreadPriority.BelowNormal };
        _worker.Start();
    }

    /// <summary>一首歌的实时特征有更新（在分析线程上触发）。</summary>
    public event Action<TrackId, SoundFeatures>? LiveUpdated;

    /// <summary>一首歌完整、连续地解码完，特征可以作为离线结果保存（在分析线程上触发）。</summary>
    public event Action<TrackId, SoundFeatures>? Completed;

    /// <summary>分析出错（在分析线程上触发），不影响播放。</summary>
    public event Action<TrackId, Exception>? Failed;

    public void Decoded(TrackId track, long startFrame, ReadOnlySpan<float> interleaved, int sampleRate, int channels)
    {
        if (_disposed || interleaved.IsEmpty) return;
        try
        {
            if (!_accept(track)) return;
        }
        catch (Exception) { return; } // 引擎线程上不能抛出
        var copy = ArrayPool<float>.Shared.Rent(interleaved.Length);
        interleaved.CopyTo(copy);
        _queue.Enqueue(new Item(track, startFrame, copy, interleaved.Length, sampleRate, channels, false));
        _signal.Set();
    }

    public void Ended(TrackId track)
    {
        if (_disposed) return;
        _queue.Enqueue(new Item(track, 0, null, 0, 0, 0, true));
        _signal.Set();
    }

    private void Work()
    {
        while (!_disposed)
        {
            _signal.WaitOne(500);
            while (_queue.TryDequeue(out var item))
            {
                try { Process(item); }
                catch (Exception e) when (e is not OutOfMemoryException)
                {
                    var track = _session?.Track ?? item.Track;
                    _session = null;
                    Failed?.Invoke(track, e);
                }
                finally
                {
                    if (item.Data is { } data) ArrayPool<float>.Shared.Return(data);
                }
            }
        }
    }

    private void Process(Item item)
    {
        if (item.IsEnd)
        {
            if (_session is { } s && s.Track == item.Track)
            {
                _session = null;
                if (!s.Acc.HasGaps && s.Acc.Position > 0) Completed?.Invoke(s.Track, s.Acc.Finish());
            }
            return;
        }

        // 换了一首歌（或者输出格式变了）：重新开始。新会话从第 0 帧开始，中途开始播放会留下空缺。
        if (_session is not { } session || session.Track != item.Track || session.Rate != item.Rate || session.Channels != item.Channels)
        {
            session = _session = new Session(item.Track, item.Rate, item.Channels,
                new SoundFeatureAnalyzer.Accumulator(item.Rate, item.Channels, TimeSpan.FromMinutes(5)));
        }

        var acc = session.Acc;
        if (item.StartFrame != acc.Position)
        {
            acc.JumpTo(item.StartFrame);
            session.LastSnapshot = -1; // 跳转后尽快给出新位置附近的特征
        }
        acc.Add(item.Data.AsSpan(0, item.Length));

        if (session.LastSnapshot < 0 || acc.Position - session.LastSnapshot >= item.Rate * SnapshotSeconds)
        {
            session.LastSnapshot = acc.Position;
            LiveUpdated?.Invoke(session.Track, acc.Snapshot());
        }
    }

    public void Dispose()
    {
        _disposed = true;
        _signal.Set();
        _worker.Join(TimeSpan.FromSeconds(2));
        _signal.Dispose();
        while (_queue.TryDequeue(out var item))
            if (item.Data is { } data) ArrayPool<float>.Shared.Return(data);
    }

    private readonly record struct Item(TrackId Track, long StartFrame, float[]? Data, int Length, int Rate, int Channels, bool IsEnd);

    private sealed class Session(TrackId track, int rate, int channels, SoundFeatureAnalyzer.Accumulator acc)
    {
        public TrackId Track { get; } = track;
        public int Rate { get; } = rate;
        public int Channels { get; } = channels;
        public SoundFeatureAnalyzer.Accumulator Acc { get; } = acc;
        public long LastSnapshot { get; set; } = -1;
    }
}
