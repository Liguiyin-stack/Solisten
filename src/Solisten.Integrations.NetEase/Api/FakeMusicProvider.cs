using Solisten.Core.Model;
using Solisten.Integrations.NetEase.Auth;

namespace Solisten.Integrations.NetEase.Api;

/// <summary>
/// 本机假 Provider（设计文档 P0）：不发出任何网络请求，用内存中的歌单模拟登录和同步。
/// 测试用它覆盖分页、失败注入、扫码状态序列；开发时设置 SOLISTEN_REMOTE_PROVIDER=fake 可以在界面上走完整流程。
/// </summary>
public sealed class FakeMusicProvider : IMusicProvider
{
    private const string IssuedSecret = "fake-session";
    private readonly Lock _gate = new();
    private readonly Queue<QrLoginState> _qrStates = new();
    private int _calls;

    public RemoteProvider Provider => RemoteProvider.NetEase;
    public string DisplayName => "网易云音乐（本机演示数据）";
    public bool IsOfficial => false;

    public RemoteAccount Account { get; set; } = new(RemoteProvider.NetEase, "10001", "Solisten 演示账号", null);
    public List<RemotePlaylist> Playlists { get; } = [];
    public Dictionary<string, List<RemoteTrack>> Tracks { get; } = [];

    /// <summary>每次扫码查询依次返回的状态码；用完后保持最后一个。默认 801 → 802 → 803。</summary>
    public IReadOnlyList<int> QrCodes { get; set; } = [QrStatusCodes.WaitingForScan, QrStatusCodes.WaitingForConfirmation, QrStatusCodes.Succeeded];

    public TimeSpan QrLifetime { get; set; } = TimeSpan.FromMinutes(2);

    /// <summary>失败注入：参数是调用名（"account"、"playlists"、"tracks:{id}"、"poll"），返回要抛出的异常。</summary>
    public Func<string, Exception?>? Fail { get; set; }

    /// <summary>每次调用前的模拟延迟（用于测试取消）。</summary>
    public TimeSpan Latency { get; set; }

    /// <summary>发出的调用总数（"未登录时不产生请求"的测试用）。</summary>
    public int Calls => Volatile.Read(ref _calls);

    public bool LoggedOut { get; private set; }

    public async Task<QrLoginTicket> CreateQrLoginAsync(CancellationToken ct)
    {
        await Enter("create", ct).ConfigureAwait(false);
        lock (_gate)
        {
            _qrStates.Clear();
            foreach (var code in QrCodes) _qrStates.Enqueue(QrStatusCodes.Map(code));
        }
        var key = Guid.NewGuid().ToString("N");
        return new QrLoginTicket(key, DateTimeOffset.UtcNow + QrLifetime, new Uri($"https://example.invalid/solisten-demo-login?k={key[..8]}"), null);
    }

    public async Task<QrPollResult> PollQrLoginAsync(string key, CancellationToken ct)
    {
        await Enter("poll", ct).ConfigureAwait(false);
        QrLoginState state;
        lock (_gate) state = _qrStates.Count > 1 ? _qrStates.Dequeue() : _qrStates.Count == 1 ? _qrStates.Peek() : QrLoginState.Failed;
        return state == QrLoginState.Succeeded ? new QrPollResult(state, new ProviderCredential(IssuedSecret)) : new QrPollResult(state);
    }

    public async Task<RemoteAccount> GetAccountAsync(ProviderCredential credential, CancellationToken ct)
    {
        await Enter("account", ct).ConfigureAwait(false);
        Authorize(credential);
        return Account;
    }

    public async Task LogoutAsync(ProviderCredential credential, CancellationToken ct)
    {
        await Enter("logout", ct).ConfigureAwait(false);
        LoggedOut = true;
    }

    public async Task<RemotePage<RemotePlaylist>> GetPlaylistsAsync(ProviderCredential credential, string userId, int offset, int limit, CancellationToken ct)
    {
        await Enter("playlists", ct).ConfigureAwait(false);
        Authorize(credential);
        lock (_gate) return Page(Playlists, offset, limit);
    }

    public async Task<RemotePage<RemoteTrack>> GetPlaylistTracksAsync(ProviderCredential credential, string playlistId, int offset, int limit, CancellationToken ct)
    {
        await Enter($"tracks:{playlistId}", ct).ConfigureAwait(false);
        Authorize(credential);
        lock (_gate)
        {
            if (!Tracks.TryGetValue(playlistId, out var list)) throw new NetEaseApiException(NetEaseErrorKind.InvalidResponse, "playlist not found");
            return Page(list, offset, limit);
        }
    }

    /// <summary>演示数据没有在线音频；测试可以通过 <see cref="Streams"/> 提供地址。</summary>
    public Dictionary<string, RemoteStream> Streams { get; } = [];

    public async Task<RemoteStream?> GetStreamAsync(ProviderCredential credential, RemoteTrackId track, StreamQuality quality, CancellationToken ct)
    {
        await Enter($"stream:{track.Value}", ct).ConfigureAwait(false);
        Authorize(credential);
        lock (_gate) return Streams.GetValueOrDefault(track.Value);
    }

    public Dictionary<string, RemoteLyrics> Lyrics { get; } = [];

    public async Task<RemoteLyrics?> GetLyricsAsync(ProviderCredential credential, RemoteTrackId track, CancellationToken ct)
    {
        await Enter($"lyrics:{track.Value}", ct).ConfigureAwait(false);
        lock (_gate) return Lyrics.GetValueOrDefault(track.Value);
    }

    private static RemotePage<T> Page<T>(List<T> all, int offset, int limit) =>
        new([.. all.Skip(offset).Take(limit)], offset + limit < all.Count);

    private static void Authorize(ProviderCredential credential)
    {
        if (credential.Secret != IssuedSecret) throw new NetEaseApiException(NetEaseErrorKind.Unauthorized, "session expired");
    }

    private async Task Enter(string call, CancellationToken ct)
    {
        Interlocked.Increment(ref _calls);
        if (Latency > TimeSpan.Zero) await Task.Delay(Latency, ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        if (Fail?.Invoke(call) is { } e) throw e;
    }

    // ───────────────────────── 演示数据 ─────────────────────────

    public FakeMusicProvider AddPlaylist(string id, string name, bool owned, params RemoteTrack[] tracks)
    {
        lock (_gate)
        {
            Playlists.Add(new RemotePlaylist(Provider, id, name, null, null, owned, tracks.Length, DateTimeOffset.UtcNow));
            Tracks[id] = [.. tracks];
        }
        return this;
    }

    public static RemoteTrack Song(string id, string title, string artist, int seconds, string album = "") =>
        new(new RemoteTrackId(RemoteProvider.NetEase, id), title, artist, album, TimeSpan.FromSeconds(seconds), null, null, false, DateTimeOffset.UtcNow);

    /// <summary>界面演示用的数据：两个自建歌单和一个收藏歌单。</summary>
    public static FakeMusicProvider Demo() => new FakeMusicProvider()
        .AddPlaylist("1", "我喜欢的音乐", owned: true,
            Song("101", "晴天", "周杰伦", 269, "叶惠美"),
            Song("102", "七里香", "周杰伦", 299, "七里香"),
            Song("103", "红豆", "王菲", 254, "唱游"))
        .AddPlaylist("2", "通勤", owned: true,
            Song("201", "夜曲", "周杰伦", 226, "十一月的萧邦"),
            Song("101", "晴天", "周杰伦", 269, "叶惠美"))
        .AddPlaylist("3", "夜晚", owned: false,
            Song("301", "那些花儿", "朴树", 266, "我去 2000 年"));
}
