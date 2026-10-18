using System.Runtime.InteropServices;
using Solisten.Audio.Decoding;
using Solisten.Audio.Engine;
using Solisten.Audio.Output;
using Solisten.Core;
using Solisten.Core.Model;
using Solisten.Library;
using Solisten.Library.Analysis;

namespace Solisten.App.Services;

/// <summary>
/// 宿主：组装 Store / 引擎 / 曲库，并执行 Reducer 返回的副作用。
/// 引擎与曲库事件转换为 Action 派发回 Store；Store 从不被引擎反向修改浏览状态。
/// </summary>
public sealed partial class AppHost : IEffectHandler, IDisposable
{
    private readonly Timer _deviceDebounce;
    private readonly AudioDeviceWatcher? _deviceWatcher;

    public AppHost()
    {
        // SOLISTEN_DATA_DIR 用于开发和测试，避免触碰真实的曲库数据。
        Paths = new AppPaths(Environment.GetEnvironmentVariable("SOLISTEN_DATA_DIR") is { Length: > 0 } dataDir ? dataDir : null);
        Settings = AppSettings.Load(Paths.Settings);
        Log = new FileLog(Paths.LogsDir);
        Library = new LibraryService(Paths, PcmReaderFactory.Default);
        Engine = new PlaybackEngine(src => new WasapiOutput(src, Settings.OutputDeviceId));
        Engine.Volume = VolumeCurve.FromSlider(Settings.Volume);
        Store = new PlayerStore(AppState.Initial(Settings.ToCore()));
        Store.AddEffectHandler(this);

        Engine.TrackBoundaryReached += id => Store.Dispatch(new TrackBoundaryReached(id));
        Engine.TrackEnded += id => Store.Dispatch(new TrackEnded(id));
        Engine.PlaybackFailed += (id, reason) =>
        {
            Log.Write($"playback failed {id}: {reason}");
            Store.Dispatch(new PlaybackFailed(id, reason));
        };
        Engine.DeviceLost += reason =>
        {
            Log.Write($"device lost: {reason}");
            Store.Dispatch(new OutputDeviceLost());
        };

        Library.Changed += index => Store.Dispatch(new LibraryUpdated(index));
        Library.Analysis.Analyzed += id => AnalysisReady?.Invoke(id);
        Library.Analysis.Failed += (id, e) => Log.Write($"analysis failed {id}: {e.Message}");

        // 默认设备变化：防抖后在新设备上重建输出，从当前位置继续（§5.6）。
        _deviceDebounce = new Timer(_ => Engine.ReopenOutput());
        try
        {
            _deviceWatcher = new AudioDeviceWatcher();
            // 只有"跟随系统默认设备"时才跟随默认设备变化；指定了设备时保持不动。
            _deviceWatcher.DefaultDeviceChanged += _ => { if (Settings.OutputDeviceId is null) _deviceDebounce.Change(300, Timeout.Infinite); };
        }
        catch (COMException e) { Log.Write($"device watcher unavailable: {e.Message}"); }
    }

    // ───────────────────────── 设置（§8.7） ─────────────────────────

    public void SetEndBehavior(EndBehavior behavior)
    {
        UpdateSettings(s => s with { EndBehavior = behavior });
        Store.Dispatch(new SetEndBehavior(behavior));
    }


    /// <summary>切换输出设备（null 表示跟随系统默认设备），在新设备上从当前位置继续。</summary>
    public void SetOutputDevice(string? deviceId)
    {
        if (Settings.OutputDeviceId == deviceId) return;
        UpdateSettings(s => s with { OutputDeviceId = deviceId });
        Engine.ReopenOutput();
    }

    public static IReadOnlyList<(string Id, string Name)> ListOutputDevices()
    {
        try { return AudioDeviceWatcher.ListDevices(); }
        catch (COMException) { return []; }
    }

    public AppPaths Paths { get; }
    public AppSettings Settings { get; private set; }
    public LibraryService Library { get; }
    public PlaybackEngine Engine { get; }
    public PlayerStore Store { get; }
    public FileLog Log { get; }

    /// <summary>某首歌分析完成（可能在后台线程触发）。</summary>
    public event Action<TrackId>? AnalysisReady;

    /// <summary>需要显示的轻提示（可能在后台线程触发）。</summary>
    public event Action<string>? Toast;

    private Task _verified = Task.CompletedTask;

    /// <summary>冷启动：先从数据库恢复聆听画布，再在后台做校验扫描，然后处理命令行传入的文件或文件夹。</summary>
    public void Start(IReadOnlyList<string>? paths = null)
    {
        var index = Library.Load();
        var session = Library.Database.LoadSession();
        Store.Dispatch(new LibraryLoaded(index, session));
        _verified = Task.Run(async () =>
        {
            try { await Library.VerifyAllAsync(); }
            catch (Exception e) { Log.Write($"verify scan failed: {e}"); }
        });
        if (paths is { Count: > 0 }) _ = OpenPathsAsync(paths);
    }

    /// <summary>
    /// §8.6 打开外部传入的路径（命令行、"打开方式"、另一个实例转发）。
    /// 文件夹加入曲库；音频文件以它所在的文件夹作为临时 BrowseContext 显示出来，是否播放由用户确认，不打断当前歌曲。
    /// 不在曲库中的文件会先导入它所在的文件夹。
    /// </summary>
    public async Task OpenPathsAsync(IReadOnlyList<string> paths)
    {
        try
        {
            await _verified;
            TrackId? reveal = null;
            foreach (var raw in paths)
            {
                var path = Path.GetFullPath(raw);
                if (Directory.Exists(path))
                {
                    await AddFolderAsync(path);
                }
                else if (File.Exists(path) && global::Solisten.Library.Metadata.TrackMetadataReader.IsSupported(path))
                {
                    reveal ??= await EnsureTrackAsync(path);
                }
            }
            if (reveal is { } id) Store.Dispatch(new RevealTrack(id));
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            Log.Write($"open paths failed: {e}");
            Toast?.Invoke($"无法打开：{e.Message}");
        }
    }

    private async Task<TrackId?> EnsureTrackAsync(string file)
    {
        if (FindTrack(file) is { } existing) return existing;
        var folder = Path.GetDirectoryName(file)!;
        Toast?.Invoke($"正在导入 {folder}");
        await AddFolderAsync(folder);
        if (FindTrack(file) is { } added) return added;
        Toast?.Invoke("无法导入这个文件");
        return null;
    }

    private TrackId? FindTrack(string file)
    {
        foreach (var t in Library.Index.Tracks.Values)
        {
            if (string.Equals(Path.GetFullPath(t.Path), file, StringComparison.OrdinalIgnoreCase)) return t.Id;
        }
        return null;
    }

    /// <summary>加入文件夹。已经在某个曲库文件夹之内时只重新扫描那个文件夹，避免同一首歌出现两次。</summary>
    public async Task AddFolderAsync(string path)
    {
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        var containing = Library.Roots.FirstOrDefault(r =>
            string.Equals(r.Path, full, StringComparison.OrdinalIgnoreCase)
            || full.StartsWith(r.Path + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
        if (containing is not null)
        {
            try { await Library.RescanAsync(containing.Id); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Toast?.Invoke($"扫描失败：{e.Message}"); }
            return;
        }

        try { await Library.AddRootAsync(full); }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            Log.Write($"import failed {path}: {e}");
            Toast?.Invoke($"导入失败：{e.Message}");
        }
    }

    public void UpdateSettings(Func<AppSettings, AppSettings> update)
    {
        Settings = update(Settings);
        try { Settings.Save(Paths.Settings); }
        catch (IOException e) { Log.Write($"settings save failed: {e.Message}"); }
    }

    /// <summary>当前播放位置：引擎时钟指向播放锚点时以它为准，否则用 Store 中的位置。</summary>
    public (TimeSpan Position, bool Running) PositionOf(TrackId? anchor)
    {
        var clock = Engine.Clock;
        if (anchor is { } a && clock.Track == a) return (clock.Position, clock.IsRunning);
        return (Store.State.Playing?.Position ?? TimeSpan.Zero, false);
    }

    private string? PathOf(TrackId id) => Store.State.Library.Find(id)?.Path;

    public void Handle(Effect effect, PlayerStore store)
    {
        switch (effect)
        {
            case LoadTrack l when PathOf(l.Track) is { } path:
                Engine.Load(l.Track, path, l.StartAt, l.Paused);
                Library.Analysis.Enqueue(l.Track, AnalysisPriority.Playing);
                break;
            case LoadTrack l:
                store.Dispatch(new PlaybackFailed(l.Track, "曲库中找不到这首歌"));
                break;
            case SetSuccessor s:
                Engine.SetSuccessor(s.Track, s.Track is { } succ ? PathOf(succ) : null);
                if (s.Track is { } next) Library.Analysis.Enqueue(next, AnalysisPriority.Snapshot);
                break;
            case PauseAudio: Engine.Pause(); break;
            case ResumeAudio: Engine.Resume(); break;
            case SeekAudio s: Engine.Seek(s.Position); break;
            case StopAudio: Engine.Stop(); break;

            case PersistSession p:
                Background(() => Library.Database.SaveSession(p.Session));
                break;
            case RecordPlay r:
                Background(() => Library.RecordPlay(r.Track));
                break;
            case RunSearch s:
                Background(() => store.Dispatch(new SearchResultsArrived(s.Query, Library.Database.Search(s.Query))));
                break;
            case PrioritizeAnalysis p:
                Library.Analysis.Enqueue(p.Track, AnalysisPriority.Visible);
                break;
            case SaveMarker m:
                Background(() => { Library.Database.AddMarker(m.Track, m.Position, m.Label); MarkersChanged?.Invoke(m.Track); });
                Toast?.Invoke($"已在 {Rendering.Progress.ProgressLayout.FormatTime(m.Position)} 打标记");
                break;
            case SaveMarkerLabel m:
                Background(() => { Library.Database.SetMarkerLabel(m.MarkerId, m.Label); MarkersChanged?.Invoke(m.Track); });
                break;
            case RemoveMarker m:
                Background(() => { Library.Database.DeleteMarker(m.MarkerId); MarkersChanged?.Invoke(m.Track); });
                break;
            case SaveTags t:
                Background(() => Library.Database.SetTags(t.Track, t.Tags));
                break;
            case SaveNote n:
                Background(() => Library.Database.SetNote(n.Track, n.Note));
                break;
            case SaveFavorite f:
                Background(() => Library.SetFavorite(f.Track, f.Favorite));
                break;
            case ShowToast t:
                Toast?.Invoke(t.Message);
                break;
        }
    }

    /// <summary>某首歌的标记有变化。</summary>
    public event Action<TrackId>? MarkersChanged;

    private void Background(Action a) => Task.Run(() =>
    {
        try { a(); }
        catch (Exception e) { Log.Write($"background effect failed: {e}"); }
    });

    /// <summary>退出前保存当前位置。</summary>
    public void SaveSessionNow()
    {
        var s = Store.State;
        if (s.Playing is null) return;
        var (pos, _) = PositionOf(s.Playing.Track);
        var snapshot = Reducer.ToSession(s) with { Position = pos };
        try { Library.Database.SaveSession(snapshot); }
        catch (Exception e) { Log.Write($"session save failed: {e.Message}"); }
    }

    public void Dispose()
    {
        _deviceDebounce.Dispose();
        _deviceWatcher?.Dispose();
        Engine.Dispose();
        Library.Dispose();
    }

    // ───────────────────────── 防止睡眠 ─────────────────────────

    private const uint EsContinuous = 0x80000000;
    private const uint EsSystemRequired = 0x00000001;

    /// <summary>§8.6：播放时只阻止系统空闲睡眠，不阻止显示器关闭。</summary>
    public static void PreventSleep(bool playing) => SetThreadExecutionState(playing ? EsContinuous | EsSystemRequired : EsContinuous);

    [LibraryImport("kernel32.dll")]
    private static partial uint SetThreadExecutionState(uint flags);
}

public sealed class FileLog(string dir)
{
    private readonly Lock _gate = new();

    public void Write(string message)
    {
        try
        {
            lock (_gate)
                File.AppendAllText(Path.Combine(dir, $"{DateTime.Now:yyyy-MM-dd}.log"), $"{DateTime.Now:HH:mm:ss.fff} {message}{Environment.NewLine}");
        }
        catch (IOException) { }
    }
}
