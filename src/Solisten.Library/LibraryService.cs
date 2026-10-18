using Solisten.Core.Audio;
using Solisten.Core.Model;
using Solisten.Library.Analysis;
using Solisten.Library.Scanning;
using Solisten.Library.Storage;

namespace Solisten.Library;

/// <summary>
/// 曲库门面：导入、启动校验扫描、FileSystemWatcher 增量更新（防抖 2 秒）、后台分析。
/// Watcher 可能丢事件（网络盘、缓冲区溢出），所以每次启动都做一次校验扫描。
/// </summary>
public sealed class LibraryService : IDisposable
{
    public static readonly TimeSpan WatcherDebounce = TimeSpan.FromSeconds(2);

    private readonly LibraryScanner _scanner;
    private readonly Dictionary<long, FileSystemWatcher> _watchers = [];
    private readonly Dictionary<long, Timer> _debounce = [];
    private readonly Lock _gate = new();
    private readonly SemaphoreSlim _scanLock = new(1, 1);
    private volatile LibraryIndex _index = LibraryIndex.Empty;

    public LibraryService(AppPaths paths, IPcmReaderFactory decoders, LibraryDatabase? db = null)
    {
        Paths = paths;
        Database = db ?? LibraryDatabase.Open(paths.Database);
        _scanner = new LibraryScanner(Database, paths);
        Analysis = new AnalysisScheduler(Database, paths, decoders, id => _index.Find(id));
    }

    public AppPaths Paths { get; }
    public LibraryDatabase Database { get; }
    public AnalysisScheduler Analysis { get; }
    public LibraryIndex Index => _index;

    /// <summary>曲库内容变化（扫描完成、播放次数或收藏更新）。</summary>
    public event Action<LibraryIndex>? Changed;
    public event Action<ScanProgress>? ScanProgressChanged;

    /// <summary>扫描阶段排除的已知格式（APE、WavPack、DSD…），按根目录记录，供设置页列出"未导入的文件"。</summary>
    public IReadOnlyList<string> Unsupported
    {
        get { lock (_gate) return [.. _unsupported.Values.SelectMany(v => v).Order(StringComparer.OrdinalIgnoreCase)]; }
    }

    private readonly Dictionary<long, IReadOnlyList<string>> _unsupported = [];

    /// <summary>根目录或扫描结果发生变化（设置页刷新用）。</summary>
    public event Action? RootsChanged;

    public IReadOnlyList<LibraryRoot> Roots => Database.GetRoots();

    /// <summary>重新扫描一个根目录（或全部根目录）。</summary>
    public Task RescanAsync(long? rootId = null, CancellationToken ct = default)
    {
        var roots = Database.GetRoots().Where(r => rootId is null || r.Id == rootId).ToList();
        foreach (var r in roots) Watch(r);
        return ScanAsync(roots, ct);
    }

    /// <summary>读取数据库中的曲库（冷启动时先用它显示聆听画布，再做校验扫描）。</summary>
    public LibraryIndex Load()
    {
        _index = Database.LoadIndex();
        return _index;
    }

    public async Task<LibraryRoot> AddRootAsync(string path, CancellationToken ct = default)
    {
        var root = Database.AddRoot(path);
        await ScanAsync([root], ct).ConfigureAwait(false);
        Watch(root);
        return root;
    }

    public async Task RemoveRootAsync(long rootId)
    {
        Unwatch(rootId);
        await _scanLock.WaitAsync().ConfigureAwait(false);
        try
        {
            Database.RemoveRoot(rootId);
            lock (_gate) _unsupported.Remove(rootId);
            var index = Publish();
            Database.RebuildGroups(index);
        }
        finally { _scanLock.Release(); }
        RootsChanged?.Invoke();
    }

    /// <summary>启动校验扫描：所有根目录全部比对一遍，然后开始监听。</summary>
    public async Task VerifyAllAsync(CancellationToken ct = default)
    {
        var roots = Database.GetRoots();
        await ScanAsync(roots, ct).ConfigureAwait(false);
        foreach (var r in roots) Watch(r);
        // 补齐所有尚未分析的歌。
        Analysis.EnqueueRange(Database.GetUnanalyzed(WaveformData.CurrentVersion, SoundFeatures.CurrentVersion), AnalysisPriority.Background);
    }

    public async Task ScanAsync(IReadOnlyList<LibraryRoot> roots, CancellationToken ct = default)
    {
        await _scanLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            bool changed = false;
            var progress = new Progress<ScanProgress>(p => ScanProgressChanged?.Invoke(p));
            foreach (var root in roots)
            {
                var result = await Task.Run(() => _scanner.Scan(root, progress, ct), ct).ConfigureAwait(false);
                lock (_gate) _unsupported[root.Id] = result.Unsupported;
                changed |= result.HasChanges;
                Analysis.EnqueueRange(result.AddedOrChanged, AnalysisPriority.Background);
            }
            if (changed || _index.IsEmpty)
            {
                var index = Publish();
                Database.RebuildGroups(index);
            }
        }
        finally { _scanLock.Release(); }
        RootsChanged?.Invoke();
    }

    public void RecordPlay(TrackId id)
    {
        var data = Database.RecordPlay(id, DateTimeOffset.UtcNow);
        _index = _index.WithUserData(id, data);
        Changed?.Invoke(_index);
    }

    public void SetFavorite(TrackId id, bool favorite)
    {
        Database.SetFavorite(id, favorite);
        _index = _index.WithUserData(id, _index.User(id) with { Favorite = favorite });
    }

    private LibraryIndex Publish()
    {
        _index = Database.LoadIndex();
        Changed?.Invoke(_index);
        return _index;
    }

    // ───────────────────────── 文件监听 ─────────────────────────

    private void Watch(LibraryRoot root)
    {
        lock (_gate)
        {
            if (_watchers.ContainsKey(root.Id) || !Directory.Exists(root.Path)) return;
            var w = new FileSystemWatcher(root.Path)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size,
                InternalBufferSize = 64 * 1024,
            };
            FileSystemEventHandler onChange = (_, _) => Schedule(root);
            w.Created += onChange;
            w.Changed += onChange;
            w.Deleted += onChange;
            w.Renamed += (_, _) => Schedule(root);
            w.Error += (_, _) => Schedule(root); // 缓冲区溢出：退回完整比对
            w.EnableRaisingEvents = true;
            _watchers[root.Id] = w;
        }
    }

    private void Unwatch(long rootId)
    {
        lock (_gate)
        {
            if (_watchers.Remove(rootId, out var w)) w.Dispose();
            if (_debounce.Remove(rootId, out var t)) t.Dispose();
        }
    }

    private void Schedule(LibraryRoot root)
    {
        lock (_gate)
        {
            if (_debounce.TryGetValue(root.Id, out var t))
            {
                t.Change(WatcherDebounce, Timeout.InfiniteTimeSpan);
                return;
            }
            _debounce[root.Id] = new Timer(_ => _ = ScanAsync([root]), null, WatcherDebounce, Timeout.InfiniteTimeSpan);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            foreach (var w in _watchers.Values) w.Dispose();
            foreach (var t in _debounce.Values) t.Dispose();
            _watchers.Clear();
            _debounce.Clear();
        }
        Analysis.Dispose();
        Database.Dispose();
        _scanLock.Dispose();
    }
}
