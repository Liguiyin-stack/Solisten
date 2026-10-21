using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Solisten.Core.Model;
using Solisten.Integrations.NetEase.Auth;

namespace Solisten.Integrations.NetEase.Api;

/// <summary>
/// 网易云音乐客户端接口适配器。这是非官方集成：接口与加密方式来自社区客户端（NeteaseCloudMusicApi），
/// 随时可能变化或触发风控。只读取账号、歌单和歌曲元数据，不请求音频地址。
/// 扫码登录走桌面客户端接口（eapi，type=3）；网页端旧接口（weapi，type=1）已被风控拦截。
/// 请求只发往 *.music.163.com；会话 Cookie 与设备标识一起保存在本机安全存储中，之后的请求沿用登录时的设备。
/// </summary>
public sealed class NetEaseProvider : IMusicProvider, IDisposable
{
    private const string WebDomain = "https://music.163.com";
    /// <summary>
    /// 客户端接口（eapi）的域名：桌面客户端域名和网页域名都接受同样的请求。网易云风控时会在其中一个上挂住并重置连接，
    /// 这时自动换到另一个，并记住能用的那个。
    /// </summary>
    private static readonly string[] EapiDomains = ["https://interfacepc.music.163.com", "https://music.163.com"];
    private static readonly TimeSpan EapiAttemptTimeout = TimeSpan.FromSeconds(6);
    private const string WebUserAgent = "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36 Edg/124.0.0.0";
    private const string ClientUserAgent = "NeteaseMusic 9.0.90/5038 (iPhone; iOS 16.2; zh_CN)";
    private const string OsVer = "Microsoft-Windows-10-Professional-build-19045-64bit";
    private const string AppVer = "3.1.17.204416";

    /// <summary>凭据中保存的 Cookie（会话 + 设备标识）；其余 Set-Cookie（统计、广告）丢弃。</summary>
    private static readonly string[] SavedCookies = ["MUSIC_U", "__csrf", "MUSIC_A_T", "MUSIC_R_T", "NMTID", "deviceId"];

    /// <summary>每次歌曲详情请求最多带多少首（远端上限 1000）。</summary>
    private const int SongDetailBatch = 500;

    /// <summary>远端二维码的有效期约 5 分钟；以远端返回的 800 为准，这里只用于倒计时显示。</summary>
    public static readonly TimeSpan QrLifetime = TimeSpan.FromMinutes(5);

    private readonly HttpClient _http;
    private readonly bool _ownsClient;
    private readonly Lock _gate = new();
    private readonly string _deviceId = Convert.ToHexString(RandomNumberGenerator.GetBytes(26));
    private readonly string _nuid = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
    private string? _nmtid;
    private (string PlaylistId, long[] TrackIds, DateTimeOffset At)? _trackIds;

    public NetEaseProvider(HttpClient? http = null)
    {
        _ownsClient = http is null;
        _http = http ?? new HttpClient(new SocketsHttpHandler
        {
            UseCookies = false, // Cookie 由凭据管理，不放进进程级的 CookieContainer
            AutomaticDecompression = DecompressionMethods.All,
            ConnectTimeout = TimeSpan.FromSeconds(10),
            // 网易云的新连接首个请求要 2–3 秒：保持连接更久，在线播放切歌时复用。
            PooledConnectionIdleTimeout = TimeSpan.FromMinutes(5),
        })
        {
            Timeout = TimeSpan.FromSeconds(20),
        };
    }

    public RemoteProvider Provider => RemoteProvider.NetEase;
    public string DisplayName => "网易云音乐（客户端接口）";
    public bool IsOfficial => false;

    public void Dispose()
    {
        if (_ownsClient) _http.Dispose();
    }

    private enum Crypto { Weapi, Eapi }

    // ───────────────────────── 登录 ─────────────────────────

    public async Task<QrLoginTicket> CreateQrLoginAsync(CancellationToken ct)
    {
        var (json, _) = await PostAsync("/api/login/qrcode/unikey", new JsonObject { ["type"] = 3 }, null, Crypto.Eapi, ct).ConfigureAwait(false);
        var key = Str(json, "unikey") ?? throw new NetEaseApiException(NetEaseErrorKind.InvalidResponse, "missing unikey");
        var url = new Uri($"https://music.163.com/login?codekey={Uri.EscapeDataString(key)}");
        return new QrLoginTicket(key, DateTimeOffset.UtcNow + QrLifetime, url, QrImage.Png(url.AbsoluteUri));
    }

    public async Task<QrPollResult> PollQrLoginAsync(string key, CancellationToken ct)
    {
        var (json, cookies) = await PostAsync("/api/login/qrcode/client/login", new JsonObject { ["key"] = key, ["type"] = 3 }, null, Crypto.Eapi, ct,
            acceptAnyCode: true).ConfigureAwait(false);
        int code = Int(json, "code");
        var state = QrStatusCodes.Map(code);
        if (state == QrLoginState.Succeeded)
        {
            if (!cookies.ContainsKey("MUSIC_U")) return new QrPollResult(QrLoginState.Failed, null, "没有收到登录信息");
            cookies["deviceId"] = _deviceId;
            lock (_gate) if (_nmtid is { } nmtid) cookies.TryAdd("NMTID", nmtid);
            return new QrPollResult(state, new ProviderCredential(FormatCookies(cookies)));
        }
        string? message = state == QrLoginState.Failed ? $"{Str(json, "message") ?? "未知状态"}（{code}）" : null;
        return new QrPollResult(state, null, message);
    }

    public async Task<RemoteAccount> GetAccountAsync(ProviderCredential credential, CancellationToken ct)
    {
        var (json, _) = await PostAsync("/api/w/nuser/account/get", new JsonObject(), credential, Crypto.Weapi, ct).ConfigureAwait(false);
        if (json["profile"] is not JsonObject profile)
            throw new NetEaseApiException(NetEaseErrorKind.Unauthorized, "no profile in account response");
        var id = profile["userId"]?.ToString() ?? json["account"]?["id"]?.ToString()
            ?? throw new NetEaseApiException(NetEaseErrorKind.InvalidResponse, "missing userId");
        return new RemoteAccount(Provider, id, Str(profile, "nickname") ?? id, Str(profile, "avatarUrl"));
    }

    public async Task LogoutAsync(ProviderCredential credential, CancellationToken ct) =>
        await PostAsync("/api/logout", new JsonObject(), credential, Crypto.Eapi, ct, acceptAnyCode: true).ConfigureAwait(false);

    // ───────────────────────── 歌单 ─────────────────────────

    public async Task<RemotePage<RemotePlaylist>> GetPlaylistsAsync(ProviderCredential credential, string userId, int offset, int limit, CancellationToken ct)
    {
        var (json, _) = await PostAsync("/api/user/playlist",
            new JsonObject { ["uid"] = userId, ["limit"] = limit, ["offset"] = offset, ["includeVideo"] = true }, credential, Crypto.Weapi, ct).ConfigureAwait(false);
        var items = new List<RemotePlaylist>();
        foreach (var node in json["playlist"] as JsonArray ?? [])
        {
            if (node is not JsonObject p || p["id"]?.ToString() is not { Length: > 0 } id) continue;
            var creator = p["creator"]?["userId"]?.ToString();
            items.Add(new RemotePlaylist(Provider, id, Str(p, "name") ?? id, Str(p, "description"), Str(p, "coverImgUrl"),
                IsOwned: creator == userId && !Bool(p, "subscribed"),
                TrackCount: Int(p, "trackCount"),
                UpdatedAt: Time(p, "updateTime")));
        }
        bool more = json["more"] is JsonValue m && m.TryGetValue<bool>(out var b) ? b : items.Count >= limit;
        return new RemotePage<RemotePlaylist>(items, more && items.Count > 0);
    }

    public async Task<RemotePage<RemoteTrack>> GetPlaylistTracksAsync(ProviderCredential credential, string playlistId, int offset, int limit, CancellationToken ct)
    {
        var ids = await TrackIdsAsync(credential, playlistId, fresh: offset == 0, ct).ConfigureAwait(false);
        var page = ids.Skip(offset).Take(limit).ToArray();
        var tracks = new List<RemoteTrack>(page.Length);
        foreach (var batch in page.Chunk(SongDetailBatch))
            tracks.AddRange(await SongDetailsAsync(credential, batch, ct).ConfigureAwait(false));
        return new RemotePage<RemoteTrack>(tracks, offset + page.Length < ids.Length);
    }

    /// <summary>歌单的完整曲目编号列表。分页读取同一歌单时复用第一页取到的列表，保证各页顺序一致。</summary>
    private async Task<long[]> TrackIdsAsync(ProviderCredential credential, string playlistId, bool fresh, CancellationToken ct)
    {
        lock (_gate)
        {
            if (!fresh && _trackIds is { } cached && cached.PlaylistId == playlistId && DateTimeOffset.UtcNow - cached.At < TimeSpan.FromMinutes(10))
                return cached.TrackIds;
        }
        var (json, _) = await PostAsync("/api/v6/playlist/detail",
            new JsonObject { ["id"] = playlistId, ["n"] = 100000, ["s"] = 8 }, credential, Crypto.Eapi, ct).ConfigureAwait(false);
        var ids = (json["playlist"]?["trackIds"] as JsonArray ?? [])
            .Select(n => n is JsonObject o ? Long(o, "id") : 0)
            .Where(id => id > 0)
            .ToArray();
        lock (_gate) _trackIds = (playlistId, ids, DateTimeOffset.UtcNow);
        return ids;
    }

    private async Task<IReadOnlyList<RemoteTrack>> SongDetailsAsync(ProviderCredential credential, long[] ids, CancellationToken ct)
    {
        var c = "[" + string.Join(",", ids.Select(id => $"{{\"id\":{id.ToString(CultureInfo.InvariantCulture)}}}")) + "]";
        var (json, _) = await PostAsync("/api/v3/song/detail", new JsonObject { ["c"] = c }, credential, Crypto.Eapi, ct).ConfigureAwait(false);

        var privileges = new Dictionary<long, JsonObject>();
        foreach (var node in json["privileges"] as JsonArray ?? [])
            if (node is JsonObject pr && Long(pr, "id") is > 0 and var pid) privileges[pid] = pr;

        var byId = new Dictionary<long, RemoteTrack>();
        foreach (var node in json["songs"] as JsonArray ?? [])
        {
            if (node is not JsonObject s) continue;
            long id = Long(s, "id");
            if (id <= 0) continue;
            var artists = (s["ar"] as JsonArray ?? []).Select(a => a is JsonObject o ? Str(o, "name") : null).Where(n => !string.IsNullOrWhiteSpace(n));
            var album = s["al"] as JsonObject;
            bool playable = privileges.TryGetValue(id, out var pr) && Int(pr, "st", -1) >= 0 && Int(pr, "pl") > 0;
            byId[id] = new RemoteTrack(
                new RemoteTrackId(Provider, id.ToString(CultureInfo.InvariantCulture)),
                Str(s, "name") ?? "",
                string.Join(" / ", artists),
                album is null ? "" : Str(album, "name") ?? "",
                TimeSpan.FromMilliseconds(Long(s, "dt")),
                null,
                album is null ? null : Str(album, "picUrl"),
                playable,
                Time(s, "publishTime"));
        }
        // 按请求的顺序返回；远端缺失（下架）的歌曲跳过。
        return [.. ids.Where(byId.ContainsKey).Select(id => byId[id])];
    }

    // ───────────────────────── 在线播放 ─────────────────────────

    public async Task<RemoteStream?> GetStreamAsync(ProviderCredential credential, RemoteTrackId track, StreamQuality quality, CancellationToken ct)
    {
        var level = quality switch
        {
            StreamQuality.Standard => "standard",
            StreamQuality.Higher => "higher",
            StreamQuality.Lossless => "lossless",
            _ => "exhigh",
        };
        var (json, _) = await PostAsync("/api/song/enhance/player/url/v1",
            new JsonObject { ["ids"] = $"[{track.Value}]", ["level"] = level, ["encodeType"] = "flac" }, credential, Crypto.Eapi, ct).ConfigureAwait(false);
        return ParseStream(json, track.Value);
    }

    internal static RemoteStream? ParseStream(JsonObject json, string songId)
    {
        var item = (json["data"] as JsonArray ?? []).OfType<JsonObject>()
            .FirstOrDefault(d => d["id"]?.ToString() == songId);
        if (item is null || Str(item, "url") is not { Length: > 0 } url || !Uri.TryCreate(url, UriKind.Absolute, out var uri)) return null;
        if (uri.Scheme is not ("https" or "http")) return null;

        TimeSpan? start = null, end = null;
        if (item["freeTrialInfo"] is JsonObject trial)
        {
            start = TimeSpan.FromSeconds(Long(trial, "start"));
            end = TimeSpan.FromSeconds(Long(trial, "end"));
            if (end <= start) (start, end) = (null, null);
        }
        int expires = Int(item, "expi", 1200);
        // 提前一分钟视为过期，避免恰好在播放途中失效。
        return new RemoteStream(uri, Int(item, "br"), Str(item, "type") ?? "", start, end,
            DateTimeOffset.UtcNow + TimeSpan.FromSeconds(Math.Max(60, expires - 60)));
    }

    // ───────────────────────── 歌词 ─────────────────────────

    public async Task<RemoteLyrics?> GetLyricsAsync(ProviderCredential credential, RemoteTrackId track, CancellationToken ct)
    {
        var (json, _) = await PostAsync("/api/song/lyric",
            new JsonObject { ["id"] = track.Value, ["tv"] = -1, ["lv"] = -1, ["rv"] = -1, ["kv"] = -1, ["_nmclfl"] = 1 }, credential, Crypto.Eapi, ct).ConfigureAwait(false);
        return ParseLyrics(json);
    }

    internal static RemoteLyrics? ParseLyrics(JsonObject json)
    {
        if (Bool(json, "nolyric") || Bool(json, "pureMusic")) return null;
        var lyric = json["lrc"] is JsonObject l ? Str(l, "lyric") : null;
        if (string.IsNullOrWhiteSpace(lyric)) return null;
        var translation = json["tlyric"] is JsonObject t ? Str(t, "lyric") : null;
        return new RemoteLyrics(lyric, string.IsNullOrWhiteSpace(translation) ? null : translation);
    }

    // ───────────────────────── 请求 ─────────────────────────

    /// <summary>
    /// 发送一个请求，返回响应 JSON 和响应中设置的、需要保存的 Cookie。
    /// 业务错误码转换为 <see cref="NetEaseApiException"/>；错误消息中不包含 Cookie 或请求体。
    /// </summary>
    private async Task<(JsonObject Json, Dictionary<string, string> Cookies)> PostAsync(
        string uri, JsonObject body, ProviderCredential? credential, Crypto crypto, CancellationToken ct, bool acceptAnyCode = false)
    {
        var saved = credential is null ? new Dictionary<string, string>(StringComparer.Ordinal) : ParseCookies(credential.Secret);
        var deviceId = saved.GetValueOrDefault("deviceId") ?? _deviceId;
        string? nmtid = saved.GetValueOrDefault("NMTID");
        lock (_gate) nmtid ??= _nmtid;
        var csrf = saved.GetValueOrDefault("__csrf", "");
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        string url;
        KeyValuePair<string, string>[] form;
        Dictionary<string, string> cookie;
        string userAgent;
        if (crypto == Crypto.Weapi)
        {
            cookie = new(saved, StringComparer.Ordinal)
            {
                ["__remember_me"] = "true",
                ["ntes_kaola_ad"] = "1",
                ["_ntes_nuid"] = _nuid,
                ["_ntes_nnid"] = $"{_nuid},{now}",
                ["WEVNSM"] = "1.0.0",
                ["osver"] = OsVer,
                ["deviceId"] = deviceId,
                ["os"] = "pc",
                ["channel"] = "netease",
                ["appver"] = AppVer,
            };
            if (nmtid is not null) cookie["NMTID"] = nmtid;
            body["csrf_token"] = csrf;
            var (encrypted, encSecKey) = NetEaseCrypto.Weapi(body.ToJsonString());
            url = $"{WebDomain}/weapi/{uri[5..]}";
            form = [new("params", encrypted), new("encSecKey", encSecKey)];
            userAgent = WebUserAgent;
        }
        else
        {
            // eapi 的"header"既是 Cookie，也作为 header 字段放进加密的请求体。
            cookie = new(StringComparer.Ordinal)
            {
                ["osver"] = OsVer,
                ["deviceId"] = deviceId,
                ["os"] = "pc",
                ["appver"] = AppVer,
                ["versioncode"] = "140",
                ["mobilename"] = "",
                ["buildver"] = (now / 1000).ToString(CultureInfo.InvariantCulture),
                ["resolution"] = "1920x1080",
                ["__csrf"] = csrf,
                ["channel"] = "netease",
                ["requestId"] = $"{now}_{RandomNumberGenerator.GetInt32(1000):0000}",
            };
            if (saved.GetValueOrDefault("MUSIC_U") is { } musicU) cookie["MUSIC_U"] = musicU;
            if (nmtid is not null) cookie["NMTID"] = nmtid;
            body["e_r"] = false;
            var header = new JsonObject();
            foreach (var (k, v) in cookie) header[k] = v;
            body["header"] = header;
            url = "";
            form = [new("params", NetEaseCrypto.Eapi(uri, body.ToJsonString()))];
            userAgent = ClientUserAgent;
        }

        string[] urls = crypto == Crypto.Weapi ? [url] : EapiUrls(uri);
        string cookieHeader = string.Join("; ", cookie.Select(kv => $"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value)}"));

        HttpResponseMessage? response = null;
        for (int attempt = 0; response is null; attempt++)
        {
            bool last = attempt == urls.Length - 1;
            using var request = new HttpRequestMessage(HttpMethod.Post, urls[attempt]) { Content = new FormUrlEncodedContent(form) };
            request.Headers.TryAddWithoutValidation("User-Agent", userAgent);
            if (crypto == Crypto.Weapi) request.Headers.TryAddWithoutValidation("Referer", WebDomain);
            request.Headers.TryAddWithoutValidation("Cookie", cookieHeader);

            // 还有备用域名时，单次尝试不等太久：网易云拦截时会挂住连接十几秒才重置。
            using var attemptCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            if (!last) attemptCts.CancelAfter(EapiAttemptTimeout);
            try
            {
                response = await _http.SendAsync(request, attemptCts.Token).ConfigureAwait(false);
                if (crypto == Crypto.Eapi) RememberEapiDomain(urls[attempt]);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                if (last) throw new NetEaseApiException(NetEaseErrorKind.Timeout, $"[{uri}] timed out");
            }
            catch (HttpRequestException e)
            {
                if (last) throw new NetEaseApiException(NetEaseErrorKind.Network, $"[{uri}] {e.Message}");
            }
        }

        using (response)
        {
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
                throw new NetEaseApiException(NetEaseErrorKind.RateLimited, $"[{uri}] HTTP 429", retryAfter: response.Headers.RetryAfter?.Delta);
            if ((int)response.StatusCode >= 500)
                throw new NetEaseApiException(NetEaseErrorKind.ServerError, $"[{uri}] HTTP {(int)response.StatusCode}");

            JsonObject json;
            try
            {
                var text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                json = JsonNode.Parse(text) as JsonObject ?? throw new JsonException("not an object");
            }
            catch (JsonException)
            {
                throw new NetEaseApiException(NetEaseErrorKind.InvalidResponse, $"[{uri}] HTTP {(int)response.StatusCode}, response is not JSON");
            }

            var set = new Dictionary<string, string>(StringComparer.Ordinal);
            if (response.Headers.TryGetValues("Set-Cookie", out var values))
            {
                foreach (var v in values)
                {
                    var pair = v.Split(';', 2)[0];
                    int eq = pair.IndexOf('=');
                    if (eq <= 0) continue;
                    var name = pair[..eq].Trim();
                    var value = pair[(eq + 1)..].Trim();
                    if (value.Length == 0 || !SavedCookies.Contains(name)) continue;
                    set[name] = value;
                    if (name == "NMTID") lock (_gate) _nmtid ??= value; // 服务端下发的设备标识，之后的请求沿用
                }
            }

            int code = json["code"] is null ? (int)response.StatusCode : Int(json, "code");
            if (!acceptAnyCode && code != 200) throw Error(uri, code, Str(json, "message") ?? Str(json, "msg"));
            return (json, set);
        }
    }

    private int _eapiDomain;

    /// <summary>按"上次能用的域名优先"的顺序给出这次请求要尝试的地址。</summary>
    private string[] EapiUrls(string uri)
    {
        int first = Volatile.Read(ref _eapiDomain);
        return [.. Enumerable.Range(0, EapiDomains.Length).Select(i => $"{EapiDomains[(first + i) % EapiDomains.Length]}/eapi/{uri[5..]}")];
    }

    private void RememberEapiDomain(string url)
    {
        int i = Array.FindIndex(EapiDomains, d => url.StartsWith(d + "/", StringComparison.Ordinal));
        if (i >= 0) Volatile.Write(ref _eapiDomain, i);
    }

    private static NetEaseApiException Error(string uri, int code, string? message)
    {
        var kind = code switch
        {
            301 or 302 or 50000005 => NetEaseErrorKind.Unauthorized, // 需要登录 / 登录失效
            405 or -460 or -462 or 8821 or 509 => NetEaseErrorKind.RateLimited, // 操作频繁 / 风控 / 需要验证
            >= 500 and < 600 => NetEaseErrorKind.ServerError,
            _ => NetEaseErrorKind.InvalidResponse,
        };
        return new NetEaseApiException(kind, $"[{uri}] code {code} {message}");
    }

    // ───────────────────────── 工具 ─────────────────────────

    internal static Dictionary<string, string> ParseCookies(string header)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var part in header.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            int eq = part.IndexOf('=');
            if (eq > 0) map[part[..eq]] = part[(eq + 1)..];
        }
        return map;
    }

    private static string FormatCookies(Dictionary<string, string> cookies) =>
        string.Join("; ", SavedCookies.Where(cookies.ContainsKey).Select(k => $"{k}={cookies[k]}"));

    private static string? Str(JsonObject o, string name) => o[name] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    private static bool Bool(JsonObject o, string name) => o[name] is JsonValue v && v.TryGetValue<bool>(out var b) && b;

    private static int Int(JsonObject o, string name, int fallback = 0) =>
        o[name] is JsonValue v && v.TryGetValue<int>(out var i) ? i : fallback;

    private static long Long(JsonObject o, string name) =>
        o[name] is JsonValue v && v.TryGetValue<long>(out var l) ? l : 0;

    private static DateTimeOffset Time(JsonObject o, string name) =>
        Long(o, name) is > 0 and var ms ? DateTimeOffset.FromUnixTimeMilliseconds(ms) : DateTimeOffset.MinValue;
}
