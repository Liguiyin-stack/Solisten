using Solisten.Core.Audio;
using Solisten.Core.Model;
using Solisten.Library.Storage;

namespace Solisten.Library.Analysis;

/// <summary>§6.3 优先级：正在播放 &gt; 导航带可见范围 &gt; 当前快照内 &gt; 其余。数值越小越优先。</summary>
public enum AnalysisPriority { Playing = 0, Visible = 1, Snapshot = 2, Background = 3 }

/// <summary>
/// 后台分析队列：波形包络、封面主色、缩略图。并发数 max(1, 逻辑核数/2)，线程优先级 BelowNormal。
/// 同一首歌重复入队时只保留更高的优先级（插队）。
/// </summary>
public sealed class AnalysisScheduler : IDisposable
{
    public static readonly int[] ThumbSizes = [256, 512];

    private readonly LibraryDatabase _db;
    private readonly AppPaths _paths;
    private readonly IPcmReaderFactory _decoders;
    private readonly Func<TrackId, Track?> _lookup;
    private readonly Lock _gate = new();
    private readonly PriorityQueue<TrackId, (int Priority, long Seq)> _queue = new();
    private readonly Dictionary<TrackId, int> _best = [];
    private readonly HashSet<TrackId> _running = [];
    private readonly SemaphoreSlim _signal = new(0);
    private readonly CancellationTokenSource _cts = new();
    private readonly Thread[] _workers;
    private long _seq;

    public AnalysisScheduler(LibraryDatabase db, AppPaths paths, IPcmReaderFactory decoders, Func<TrackId, Track?> lookup, int? concurrency = null)
    {
        _db = db;
        _paths = paths;
        _decoders = decoders;
        _lookup = lookup;
        int n = concurrency ?? Math.Max(1, Environment.ProcessorCount / 2);
        _workers = new Thread[n];
        for (int i = 0; i < n; i++)
        {
            _workers[i] = new Thread(Work) { IsBackground = true, Priority = ThreadPriority.BelowNormal, Name = $"Solisten analysis {i}" };
            _workers[i].Start();
        }
    }

    /// <summary>一首歌分析完成（波形文件、主色和缩略图都已就绪或确认不存在）。</summary>
    public event Action<TrackId>? Analyzed;
    public event Action<TrackId, Exception>? Failed;

    public int Pending
    {
        get { lock (_gate) return _best.Count; }
    }

    public void Enqueue(TrackId id, AnalysisPriority priority)
    {
        lock (_gate)
        {
            if (_running.Contains(id)) return;
            int p = (int)priority;
            if (_best.TryGetValue(id, out var existing) && existing <= p) return;
            _best[id] = p;
            _queue.Enqueue(id, (p, Interlocked.Increment(ref _seq)));
        }
        _signal.Release();
    }

    public void EnqueueRange(IEnumerable<TrackId> ids, AnalysisPriority priority)
    {
        foreach (var id in ids) Enqueue(id, priority);
    }

    public bool IsAnalyzed(TrackId id) =>
        File.Exists(_paths.Waveform(id)) && File.Exists(_paths.Features(id))
        && _db.GetAnalysis(id) is { WaveformVersion: WaveformData.CurrentVersion, FeaturesVersion: SoundFeatures.CurrentVersion };

    private void Work()
    {
        var ct = _cts.Token;
        while (!ct.IsCancellationRequested)
        {
            try { _signal.Wait(ct); }
            catch (OperationCanceledException) { return; }

            TrackId id;
            lock (_gate)
            {
                // 跳过已被更高优先级重新入队的旧条目。
                while (true)
                {
                    if (!_queue.TryDequeue(out id, out var key)) goto next;
                    if (_best.TryGetValue(id, out var best) && best == key.Priority) break;
                }
                _best.Remove(id);
                _running.Add(id);
            }

            try
            {
                if (_lookup(id) is { Available: true } track)
                {
                    AnalyzeOne(track, ct);
                    Analyzed?.Invoke(id);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception e)
            {
                Failed?.Invoke(id, e);
            }
            finally
            {
                lock (_gate) _running.Remove(id);
            }
        next:;
        }
    }

    internal void AnalyzeOne(Track track, CancellationToken ct)
    {
        var wfPath = _paths.Waveform(track.Id);
        var sfPath = _paths.Features(track.Id);
        if (!IsAnalyzed(track.Id))
        {
            // 一次解码（立体声，保留左右声道平衡），同时喂给波形和音频特征两个分析器。
            using var reader = _decoders.Open(track.Path, WaveformAnalyzer.AnalysisSampleRate, 2);
            var waveform = new WaveformAnalyzer.Accumulator(reader.SampleRate, reader.Channels, reader.Duration);
            var features = new SoundFeatureAnalyzer.Accumulator(reader.SampleRate, reader.Channels, reader.Duration);
            PcmPump.Run(reader, ct, waveform, features);
            AtomicFile.Write(wfPath, waveform.Finish().Write);
            AtomicFile.Write(sfPath, features.Finish().Write);
        }

        Palette? palette = null;
        if (_db.GetCoverHash(track.Id) is { } hash && _paths.FindCover(hash) is { } coverPath)
        {
            var bytes = File.ReadAllBytes(coverPath);
            try
            {
                var rgba = ImageTools.DecodeRgbaAsync(bytes, PaletteExtractor.SampleSize).GetAwaiter().GetResult();
                palette = PaletteExtractor.Extract(rgba);
                foreach (var size in ThumbSizes)
                {
                    var thumb = _paths.Thumb(hash, size);
                    if (!File.Exists(thumb)) ImageTools.WriteThumbnailAsync(bytes, size, thumb).GetAwaiter().GetResult();
                }
            }
            catch (Exception e) when (e is ArgumentException or System.Runtime.InteropServices.COMException)
            {
                // 封面图片损坏：使用中性色场。
                palette = null;
            }
        }

        _db.SaveAnalysis(track.Id, WaveformData.CurrentVersion, SoundFeatures.CurrentVersion, palette);
    }

    public static SoundFeatures? LoadFeatures(AppPaths paths, TrackId id)
    {
        var path = paths.Features(id);
        if (!File.Exists(path)) return null;
        try
        {
            using var fs = File.OpenRead(path);
            return SoundFeatures.Read(fs);
        }
        catch (Exception e) when (e is IOException or EndOfStreamException)
        {
            return null;
        }
    }

    public static WaveformData? LoadWaveform(AppPaths paths, TrackId id)
    {
        var path = paths.Waveform(id);
        if (!File.Exists(path)) return null;
        try
        {
            using var fs = File.OpenRead(path);
            return WaveformData.Read(fs);
        }
        catch (Exception e) when (e is IOException or EndOfStreamException)
        {
            return null;
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        foreach (var w in _workers) w.Join(TimeSpan.FromSeconds(2));
        _cts.Dispose();
        _signal.Dispose();
    }
}
