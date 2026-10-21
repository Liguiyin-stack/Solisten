using System.Collections.Immutable;
using System.Globalization;
using Solisten.App.Services;
using Solisten.Core.Model;
using Solisten.Integrations.NetEase.Api;
using Solisten.Integrations.NetEase.Auth;
using Solisten.Integrations.NetEase.Diagnostics;
using Solisten.Library;
using Solisten.Library.Remote;

namespace Solisten.App.Integrations;

/// <summary>
/// 网易云在线播放：把已同步的远程曲目变成可播放的在线曲目（编号为负数），播放前才请求临时播放地址。
/// 播放权限完全由远端按账号决定：没有完整权限时只播放远端给出的试听片段，没有地址就不能播放。
/// 音频只流式读取，不写入磁盘；只缓存封面图片。未登录时不提供在线曲目，也不发出任何请求。
/// </summary>
public sealed class NetEaseOnline(IMusicProvider provider, ICredentialStore credentials, RemoteMusicDatabase database, AppPaths paths, FileLog log)
{
    private static readonly HttpClient CoverClient = new() { Timeout = TimeSpan.FromSeconds(15) };

    private readonly Lock _gate = new();
    private ImmutableDictionary<TrackId, Track> _tracks = ImmutableDictionary<TrackId, Track>.Empty;
    private ImmutableDictionary<TrackId, string?> _covers = ImmutableDictionary<TrackId, string?>.Empty;
    private ImmutableDictionary<TrackId, (StreamQuality Quality, RemoteStream Stream)> _streams = ImmutableDictionary<TrackId, (StreamQuality, RemoteStream)>.Empty;

    /// <summary>当前可播放的在线曲目（已登录时为全部已同步的远程曲目）。</summary>
    public ImmutableDictionary<TrackId, Track> Tracks { get { lock (_gate) return _tracks; } }

    /// <summary>在线曲目集合变化（同步完成、登录、退出、试听时长更新）。</summary>
    public event Action? TracksChanged;

    public StreamQuality Quality { get; set; } = StreamQuality.ExHigh;

    public static TrackId IdOf(RemoteTrackId remote) =>
        long.TryParse(remote.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n > 0 ? new TrackId(-n) : default;

    public RemoteTrackId RemoteOf(TrackId id) => new(provider.Provider, (-id.Value).ToString(CultureInfo.InvariantCulture));

    /// <summary>从本机缓存重建在线曲目。未登录时清空。</summary>
    public void Reload(bool connected)
    {
        var builder = ImmutableDictionary.CreateBuilder<TrackId, Track>();
        var covers = ImmutableDictionary.CreateBuilder<TrackId, string?>();
        if (connected)
        {
            foreach (var t in database.GetTracks(provider.Provider))
            {
                var id = IdOf(t.Id);
                if (id == default) continue;
                lock (_gate) builder[id] = _tracks.TryGetValue(id, out var existing) && existing.Meta.Title == t.Title ? existing : ToTrack(id, t);
                covers[id] = t.CoverUrl;
            }
        }
        lock (_gate)
        {
            _tracks = builder.ToImmutable();
            _covers = covers.ToImmutable();
            if (!connected) _streams = _streams.Clear();
        }
        TracksChanged?.Invoke();
    }

    private static Track ToTrack(TrackId id, RemoteTrack t) => new(
        id,
        $"netease:song:{-id.Value}",
        new TrackMeta(t.Title, t.Artist, "", t.Album, 1, 0, null, t.Duration),
        new AudioFormat("online", 0, 0, 2),
        new GroupKeys("netease:", "网易云音乐", $"netease:{t.Album}", t.Album),
        Available: true);

    /// <summary>
    /// 请求播放地址。返回 null 表示远端不给地址（无版权、已下架、需要单曲购买）。
    /// 试听片段会把这首歌的时长改为片段长度，进度条与实际音频一致。
    /// </summary>
    /// <remarks>地址在过期前复用（只在内存中）；<paramref name="fresh"/> 为 true 时忽略缓存（播放途中地址失效后重试）。</remarks>
    public async Task<RemoteStream?> ResolveAsync(TrackId id, CancellationToken ct, bool fresh = false)
    {
        if (!fresh)
        {
            lock (_gate)
            {
                if (_streams.TryGetValue(id, out var cached) && cached.Quality == Quality && cached.Stream.ExpiresAt > DateTimeOffset.UtcNow)
                    return cached.Stream;
            }
        }
        var credential = credentials.Load(provider.Provider) ?? throw new NetEaseApiException(NetEaseErrorKind.Unauthorized, "not signed in");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        var quality = Quality;
        var stream = await provider.GetStreamAsync(credential, RemoteOf(id), quality, timeout.Token).ConfigureAwait(false);
        lock (_gate)
        {
            if (stream is null) _streams = _streams.Remove(id);
            else _streams = _streams.SetItem(id, (quality, stream));
        }
        if (stream?.TrialLength is { } trial && trial > TimeSpan.Zero) SetDuration(id, trial);
        return stream;
    }

    /// <summary>后台预先请求地址（导航层焦点停在一首在线歌曲上时），之后确认播放就不用再等网络。</summary>
    public void Prefetch(TrackId id)
    {
        lock (_gate)
        {
            if (!_tracks.ContainsKey(id) || (_streams.TryGetValue(id, out var cached) && cached.Stream.ExpiresAt > DateTimeOffset.UtcNow)) return;
        }
        _ = Task.Run(async () =>
        {
            try { await ResolveAsync(id, CancellationToken.None).ConfigureAwait(false); }
            catch (Exception e) when (e is NetEaseApiException or OperationCanceledException)
            {
                log.Write($"netease prefetch failed {id}: {Redactor.Redact(e.Message)}");
            }
        });
    }

    /// <summary>丢弃缓存的地址（播放失败时）。</summary>
    public void Forget(TrackId id)
    {
        lock (_gate) _streams = _streams.Remove(id);
    }

    private void SetDuration(TrackId id, TimeSpan duration)
    {
        lock (_gate)
        {
            if (!_tracks.TryGetValue(id, out var t) || t.Meta.Duration == duration) return;
            _tracks = _tracks.SetItem(id, t with { Meta = t.Meta with { Duration = duration } });
        }
        TracksChanged?.Invoke();
    }

    // ───────────────────────── 歌词 ─────────────────────────

    private string LyricsFile(RemoteTrackId id) => Path.Combine(paths.Root, "cache", "lyrics", $"netease-{id.Value}.json");

    private sealed record LyricsCache(string? Lyric, string? Translation);

    /// <summary>
    /// 网易云歌词：先读本机缓存（不联网）；没有缓存且已登录时请求一次并缓存（"没有歌词"也缓存，不反复请求）。
    /// 返回 (是否得到了确定的结果, 歌词)；网络失败时第一项为 false，之后可以再试。
    /// </summary>
    public async Task<(bool Known, RemoteLyrics? Lyrics)> LyricsAsync(RemoteTrackId id, bool connected, CancellationToken ct)
    {
        var file = LyricsFile(id);
        if (File.Exists(file))
        {
            try
            {
                if (System.Text.Json.JsonSerializer.Deserialize<LyricsCache>(await File.ReadAllTextAsync(file, ct).ConfigureAwait(false)) is { } c)
                    return (true, c.Lyric is { Length: > 0 } l ? new RemoteLyrics(l, c.Translation) : null);
            }
            catch (Exception e) when (e is IOException or System.Text.Json.JsonException) { }
        }
        if (!connected) return (false, null);

        try
        {
            var credential = credentials.Load(provider.Provider) ?? new ProviderCredential("");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(15));
            var lyrics = await provider.GetLyricsAsync(credential, id, timeout.Token).ConfigureAwait(false);
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            var tmp = file + $".{Guid.NewGuid():N}.tmp";
            await File.WriteAllTextAsync(tmp, System.Text.Json.JsonSerializer.Serialize(new LyricsCache(lyrics?.Lyric, lyrics?.Translation)), ct).ConfigureAwait(false);
            File.Move(tmp, file, overwrite: true);
            return (true, lyrics);
        }
        catch (Exception e) when (e is NetEaseApiException or OperationCanceledException or IOException)
        {
            if (!ct.IsCancellationRequested) log.Write($"netease lyrics failed {id}: {Redactor.Redact(e.Message)}");
            return (false, null);
        }
    }

    // ───────────────────────── 封面 ─────────────────────────

    /// <summary>同时下载的封面数量上限（浏览导航条时不一下子发出几十个请求）。</summary>
    private static readonly SemaphoreSlim CoverSlots = new(4, 4);

    private string CoverFile(TrackId id, int size) =>
        Path.Combine(paths.Root, "cache", "remote-cover", size >= 600 ? $"{-id.Value}.jpg" : $"{-id.Value}-{size}.jpg");

    /// <summary>已经缓存的封面（不发请求）：优先大图，其次导航条缩略图。</summary>
    public string? CachedCover(TrackId id)
    {
        var full = CoverFile(id, 600);
        if (File.Exists(full)) return full;
        var thumb = CoverFile(id, 200);
        return File.Exists(thumb) ? thumb : null;
    }

    /// <summary>聆听画布用的封面（600px）。失败时返回 null（显示默认画面）。</summary>
    public Task<string?> CoverAsync(TrackId id) => DownloadCoverAsync(id, 600);

    /// <summary>导航条卡片用的缩略图（200px，约 10 KB）；已经有大图时直接用大图。</summary>
    public async Task<string?> ThumbAsync(TrackId id) => CachedCover(id) ?? await DownloadCoverAsync(id, 200).ConfigureAwait(false);

    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, Task<string?>> _coverDownloads = new();

    /// <summary>同一个文件同时只下载一次：并发请求共用同一个任务（否则两个下载会争着替换同一个文件）。</summary>
    private Task<string?> DownloadCoverAsync(TrackId id, int size)
    {
        var file = CoverFile(id, size);
        if (File.Exists(file)) return Task.FromResult<string?>(file);
        var task = _coverDownloads.GetOrAdd(file, _ => DownloadCoverCoreAsync(id, size));
        _ = task.ContinueWith(_ => _coverDownloads.TryRemove(new KeyValuePair<string, Task<string?>>(file, task)), TaskScheduler.Default);
        return task;
    }

    private async Task<string?> DownloadCoverCoreAsync(TrackId id, int size)
    {
        var file = CoverFile(id, size);
        if (File.Exists(file)) return file;
        string? url;
        lock (_gate) url = _covers.GetValueOrDefault(id);
        if (string.IsNullOrEmpty(url) || !Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("https" or "http")) return null;
        await CoverSlots.WaitAsync().ConfigureAwait(false);
        try
        {
            if (File.Exists(file)) return file;
            var bytes = await CoverClient.GetByteArrayAsync($"{uri.GetLeftPart(UriPartial.Path)}?param={size}y{size}").ConfigureAwait(false);
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            var tmp = file + $".{Guid.NewGuid():N}.tmp";
            await File.WriteAllBytesAsync(tmp, bytes).ConfigureAwait(false);
            File.Move(tmp, file, overwrite: true);
            return file;
        }
        catch (Exception e) when (e is HttpRequestException or IOException or TaskCanceledException or UnauthorizedAccessException)
        {
            log.Write($"netease cover failed {id}: {Redactor.Redact(e.Message)}");
            return null;
        }
        finally
        {
            CoverSlots.Release();
        }
    }
}
