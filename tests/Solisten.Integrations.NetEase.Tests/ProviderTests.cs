using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Solisten.Integrations.NetEase.Api;
using Solisten.Integrations.NetEase.Auth;

namespace Solisten.Integrations.NetEase.Tests;

/// <summary>本地假服务器：按路径返回预设的 JSON，并记录请求。不连接真实的网易云。</summary>
internal sealed class StubHandler : HttpMessageHandler
{
    public Dictionary<string, Func<HttpRequestMessage, HttpResponseMessage>> Routes { get; } = [];
    public List<(string Path, string? Cookie)> Requests { get; } = [];

    public void Json(string path, string json, params string[] setCookies) => Routes[path] = _ =>
    {
        var r = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        foreach (var c in setCookies) r.Headers.TryAddWithoutValidation("Set-Cookie", c);
        return r;
    };

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var path = request.RequestUri!.AbsolutePath;
        Requests.Add((path, request.Headers.TryGetValues("Cookie", out var c) ? string.Join("; ", c) : null));
        return Task.FromResult(Routes.TryGetValue(path, out var route) ? route(request) : new HttpResponseMessage(HttpStatusCode.NotFound));
    }
}

public class NetEaseProviderTests
{
    private static (NetEaseProvider Provider, StubHandler Server) Create()
    {
        var server = new StubHandler();
        return (new NetEaseProvider(new HttpClient(server)), server);
    }

    private static readonly ProviderCredential Session = new("MUSIC_U=u-secret; __csrf=csrf1");

    [Fact]
    public void Weapi_MatchesReferenceImplementation()
    {
        // 参考值由独立的 Node.js 实现（crypto + BigInt）计算。
        var (p, key) = NetEaseCrypto.Weapi("""{"type":1}""", "abcdefghijklmnop");
        Assert.Equal("0f2+q4z7Gkb6i+Ic3AtL4n9NgjVpemF15SZny99LVtw=", p);
        Assert.Equal("d15a1683c992095d0c234c19966605c5c5964911268bbeda8cb8d08d834913e59d53b32358903a121b5fca784c1f5ae44951fd02524df58ecc98e52cc7cf8689b42c2e93ddf05b0592512d87f5960467e2f086c018849d76014d323500e30f13ef4cafbb0cf5a66731a3f1776c75ca35d0062dac70a3e33245afabcf47938487", key);
    }

    [Fact]
    public void Eapi_MatchesReferenceImplementation() =>
        // 参考值由独立的 Node.js 实现（crypto 模块的 aes-128-ecb + md5）计算。
        Assert.Equal(
            "1CA81A97B7BAEB099F29A9B99A25CD6E58E7BA5B35F25231033065C776830C0ACD212933C9432002B586ED0F458155F631B44BD2C5B733983A66E6224C0585A52777484232CB13E16B21684479BA3B9B3CF9FA119BBE8E17B07F40E02346BE3781A9B22A53CC3C44EC77569FAD1631FF",
            NetEaseCrypto.Eapi("/api/login/qrcode/unikey", """{"type":3,"e_r":false}"""));

    [Fact]
    public async Task SignedInRequests_ReuseLoginDevice()
    {
        var (provider, server) = Create();
        server.Json("/eapi/v6/playlist/detail", """{"code":200,"playlist":{"trackIds":[]}}""");
        await provider.GetPlaylistTracksAsync(new ProviderCredential("MUSIC_U=u; deviceId=DEV42; NMTID=n1"), "p", 0, 10, default);
        var cookie = server.Requests[^1].Cookie!;
        Assert.Contains("deviceId=DEV42", cookie);
        Assert.Contains("NMTID=n1", cookie);
        Assert.Contains("MUSIC_U=u", cookie);
    }

    [Fact]
    public async Task QrLogin_ReturnsPngAndKey_ThenCookieOnSuccess()
    {
        var (provider, server) = Create();
        server.Json("/eapi/login/qrcode/unikey", """{"code":200,"unikey":"k-123"}""");
        var ticket = await provider.CreateQrLoginAsync(default);
        Assert.Equal("k-123", ticket.Key);
        Assert.Equal("https://music.163.com/login?codekey=k-123", ticket.QrUrl!.AbsoluteUri);
        Assert.Equal([0x89, 0x50, 0x4E, 0x47], ticket.QrPng!.Take(4)); // PNG 文件头

        server.Json("/eapi/login/qrcode/client/login", """{"code":801,"message":"等待扫码"}""");
        Assert.Equal(QrLoginState.WaitingForScan, (await provider.PollQrLoginAsync("k-123", default)).State);

        server.Json("/eapi/login/qrcode/client/login", """{"code":803,"message":"授权登陆成功"}""",
            "MUSIC_U=abc; Max-Age=1296000; Path=/; HTTPOnly", "__csrf=c1; Path=/", "_iuqxldmzr_=32; Path=/");
        var ok = await provider.PollQrLoginAsync("k-123", default);
        Assert.Equal(QrLoginState.Succeeded, ok.State);
        Assert.StartsWith("MUSIC_U=abc; __csrf=c1; deviceId=", ok.Credential!.Secret); // 只保留会话 Cookie 和设备标识
        Assert.DoesNotContain("_iuqxldmzr_", ok.Credential.Secret);
    }

    [Fact]
    public async Task QrRiskControl_IsFailedWithMessage()
    {
        var (provider, server) = Create();
        server.Json("/eapi/login/qrcode/client/login", """{"code":8821,"message":"需要行为验证码验证"}""");
        var r = await provider.PollQrLoginAsync("k", default);
        Assert.Equal(QrLoginState.Failed, r.State);
        Assert.Contains("8821", r.Message);
    }

    [Fact]
    public async Task Account_SendsSessionCookie_AndParsesProfile()
    {
        var (provider, server) = Create();
        server.Json("/weapi/w/nuser/account/get", """{"code":200,"account":{"id":42},"profile":{"userId":42,"nickname":"听歌的人","avatarUrl":"https://p.example/a.jpg"}}""");

        var account = await provider.GetAccountAsync(Session, default);

        Assert.Equal("42", account.UserId);
        Assert.Equal("听歌的人", account.Nickname);
        Assert.Contains("MUSIC_U=u-secret", server.Requests[^1].Cookie);
    }

    [Fact]
    public async Task Account_WithoutProfile_IsUnauthorized()
    {
        var (provider, server) = Create();
        server.Json("/weapi/w/nuser/account/get", """{"code":200,"account":null,"profile":null}""");
        var e = await Assert.ThrowsAsync<NetEaseApiException>(() => provider.GetAccountAsync(Session, default));
        Assert.Equal(NetEaseErrorKind.Unauthorized, e.Kind);
    }

    [Fact]
    public async Task Playlists_DistinguishOwnedFromSubscribed()
    {
        var (provider, server) = Create();
        server.Json("/weapi/user/playlist", """
            {"code":200,"more":false,"playlist":[
              {"id":1,"name":"我喜欢的音乐","trackCount":3,"creator":{"userId":42},"subscribed":false,"updateTime":1700000000000},
              {"id":2,"name":"别人的歌单","trackCount":10,"creator":{"userId":7},"subscribed":true,"unknownField":{"x":1}}
            ]}
            """);

        var page = await provider.GetPlaylistsAsync(Session, "42", 0, 50, default);

        Assert.False(page.HasMore);
        Assert.Equal(["1", "2"], page.Items.Select(p => p.Id));
        Assert.True(page.Items[0].IsOwned);
        Assert.False(page.Items[1].IsOwned);
        Assert.Equal(3, page.Items[0].TrackCount);
    }

    [Fact]
    public async Task PlaylistTracks_PageOverTrackIds_KeepOrder_SkipMissing()
    {
        var (provider, server) = Create();
        server.Json("/eapi/v6/playlist/detail", """{"code":200,"playlist":{"trackIds":[{"id":3},{"id":1},{"id":2}]}}""");
        server.Routes["/eapi/v3/song/detail"] = _ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""
                {"code":200,"songs":[
                  {"id":1,"name":"晴天","ar":[{"name":"周杰伦"}],"al":{"name":"叶惠美","picUrl":"https://p/1.jpg"},"dt":269000},
                  {"id":3,"name":"红豆","ar":[{"name":"王菲"},{"name":null}],"al":{"name":"唱游"},"dt":254000}
                ],"privileges":[{"id":1,"st":0,"pl":320000},{"id":3,"st":-200,"pl":0}]}
                """),
        };

        var first = await provider.GetPlaylistTracksAsync(Session, "p", 0, 2, default);
        var second = await provider.GetPlaylistTracksAsync(Session, "p", 2, 2, default);

        Assert.True(first.HasMore);
        Assert.Equal(["3", "1"], first.Items.Select(t => t.Id.Value));
        Assert.Equal("王菲", first.Items[0].Artist);
        Assert.Equal(TimeSpan.FromSeconds(269), first.Items[1].Duration);
        Assert.True(first.Items[1].IsPlayableOnline);
        Assert.False(first.Items[0].IsPlayableOnline);
        Assert.False(second.HasMore);
        Assert.Single(server.Requests, r => r.Path == "/eapi/v6/playlist/detail"); // 第二页复用曲目列表
    }

    [Theory]
    [InlineData(301, NetEaseErrorKind.Unauthorized)]
    [InlineData(-462, NetEaseErrorKind.RateLimited)]
    [InlineData(405, NetEaseErrorKind.RateLimited)]
    [InlineData(502, NetEaseErrorKind.ServerError)]
    [InlineData(400, NetEaseErrorKind.InvalidResponse)]
    public async Task BusinessCodes_MapToErrorKinds(int code, NetEaseErrorKind kind)
    {
        var (provider, server) = Create();
        server.Json("/weapi/user/playlist", $$"""{"code":{{code}},"message":"x"}""");
        var e = await Assert.ThrowsAsync<NetEaseApiException>(() => provider.GetPlaylistsAsync(Session, "1", 0, 10, default));
        Assert.Equal(kind, e.Kind);
    }

    [Fact]
    public async Task HttpErrors_MapToErrorKinds()
    {
        var (provider, server) = Create();
        server.Routes["/weapi/user/playlist"] = _ => new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        Assert.Equal(NetEaseErrorKind.RateLimited, (await Assert.ThrowsAsync<NetEaseApiException>(() => provider.GetPlaylistsAsync(Session, "1", 0, 10, default))).Kind);

        server.Routes["/weapi/user/playlist"] = _ => new HttpResponseMessage(HttpStatusCode.BadGateway);
        Assert.Equal(NetEaseErrorKind.ServerError, (await Assert.ThrowsAsync<NetEaseApiException>(() => provider.GetPlaylistsAsync(Session, "1", 0, 10, default))).Kind);

        server.Routes["/weapi/user/playlist"] = _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("<html>") };
        Assert.Equal(NetEaseErrorKind.InvalidResponse, (await Assert.ThrowsAsync<NetEaseApiException>(() => provider.GetPlaylistsAsync(Session, "1", 0, 10, default))).Kind);

        server.Routes["/weapi/user/playlist"] = _ => throw new HttpRequestException("connection refused");
        Assert.Equal(NetEaseErrorKind.Network, (await Assert.ThrowsAsync<NetEaseApiException>(() => provider.GetPlaylistsAsync(Session, "1", 0, 10, default))).Kind);
    }

    [Fact]
    public async Task ErrorMessages_NeverContainCookies()
    {
        var (provider, server) = Create();
        server.Routes["/weapi/user/playlist"] = _ => throw new HttpRequestException("failed");
        var e = await Assert.ThrowsAsync<NetEaseApiException>(() => provider.GetPlaylistsAsync(Session, "1", 0, 10, default));
        Assert.DoesNotContain("u-secret", e.ToString());
    }
}

/// <summary>连接真实的网易云（只请求一次性二维码 key，不登录）。设置 SOLISTEN_NETEASE_LIVE=1 时运行。</summary>
public class NetEaseLiveTests
{
    [Fact]
    public async Task RealServer_IssuesQrKey()
    {
        if (Environment.GetEnvironmentVariable("SOLISTEN_NETEASE_LIVE") != "1") return;
        using var provider = new NetEaseProvider();
        var ticket = await provider.CreateQrLoginAsync(default);
        Assert.False(string.IsNullOrEmpty(ticket.Key));
        var status = await provider.PollQrLoginAsync(ticket.Key, default);
        Assert.Equal(QrLoginState.WaitingForScan, status.State);

        // 无效会话：网页端账号接口应当判定为未登录，而不是风控。
        var e = await Assert.ThrowsAsync<NetEaseApiException>(() => provider.GetAccountAsync(new ProviderCredential("MUSIC_U=invalid"), default));
        Assert.Equal(NetEaseErrorKind.Unauthorized, e.Kind);
    }

    [Fact]
    public async Task RealServer_PublicPlaylistParses()
    {
        if (Environment.GetEnvironmentVariable("SOLISTEN_NETEASE_LIVE") != "1") return;
        using var provider = new NetEaseProvider();
        var anonymous = new ProviderCredential("");
        // 社区文档中的示例用户，歌单公开。
        var playlists = await provider.GetPlaylistsAsync(anonymous, "32953014", 0, 5, default);
        Assert.NotEmpty(playlists.Items);
        var first = playlists.Items.First(p => p.TrackCount > 0);
        var tracks = await provider.GetPlaylistTracksAsync(anonymous, first.Id, 0, 5, default);
        Assert.NotEmpty(tracks.Items);
        Assert.All(tracks.Items, t =>
        {
            Assert.False(string.IsNullOrEmpty(t.Title));
            Assert.True(t.Duration > TimeSpan.Zero);
        });
    }
}

public class StreamParsingTests
{
    private static JsonObject Json(string s) => (JsonObject)JsonNode.Parse(s)!;

    [Fact]
    public void FullStream_HasNoTrial()
    {
        var s = NetEaseProvider.ParseStream(Json("""{"code":200,"data":[{"id":1,"url":"https://m701.music.126.net/a.mp3","br":320000,"type":"mp3","expi":1200,"freeTrialInfo":null}]}"""), "1")!;
        Assert.False(s.IsTrial);
        Assert.Equal(320000, s.Bitrate);
        Assert.Equal("mp3", s.Format);
        Assert.InRange(s.ExpiresAt - DateTimeOffset.UtcNow, TimeSpan.FromMinutes(18), TimeSpan.FromMinutes(19.5));
        Assert.DoesNotContain("126.net", s.ToString()); // 地址不进入日志
    }

    [Fact]
    public void TrialStream_ReportsClipLength()
    {
        var s = NetEaseProvider.ParseStream(Json("""{"data":[{"id":7,"url":"https://m701.music.126.net/b.mp3","br":128000,"type":"mp3","freeTrialInfo":{"start":0,"end":45}}]}"""), "7")!;
        Assert.True(s.IsTrial);
        Assert.Equal(TimeSpan.FromSeconds(45), s.TrialLength);
    }

    [Theory]
    [InlineData("""{"data":[{"id":1,"url":null,"code":404}]}""")]
    [InlineData("""{"data":[{"id":2,"url":"https://x/y.mp3"}]}""")]
    [InlineData("""{"data":[{"id":1,"url":"file:///C:/a.mp3"}]}""")]
    [InlineData("""{"data":[]}""")]
    [InlineData("""{"code":200}""")]
    public void NoUsableUrl_IsNull(string json) => Assert.Null(NetEaseProvider.ParseStream(Json(json), "1"));

    [Fact]
    public async Task Provider_RequestsStreamWithLevel()
    {
        var server = new StubHandler();
        using var provider = new NetEaseProvider(new HttpClient(server));
        server.Json("/eapi/song/enhance/player/url/v1", """{"code":200,"data":[{"id":5,"url":"https://m701.music.126.net/c.flac","br":999000,"type":"flac"}]}""");
        var s = await provider.GetStreamAsync(new ProviderCredential("MUSIC_U=u"), new Solisten.Core.Model.RemoteTrackId(Solisten.Core.Model.RemoteProvider.NetEase, "5"), StreamQuality.Lossless, default);
        Assert.Equal("flac", s!.Format);
        Assert.Contains("MUSIC_U=u", server.Requests[^1].Cookie);
    }
}

public class LyricsParsingTests
{
    private static JsonObject Json(string s) => (JsonObject)JsonNode.Parse(s)!;

    [Fact]
    public void LyricAndTranslation_AreReturned()
    {
        var l = NetEaseProvider.ParseLyrics(Json("""{"code":200,"lrc":{"lyric":"[00:01.00]hello"},"tlyric":{"lyric":"[00:01.00]你好"}}"""))!;
        Assert.Equal("[00:01.00]hello", l.Lyric);
        Assert.Equal("[00:01.00]你好", l.Translation);
    }

    [Theory]
    [InlineData("""{"code":200,"nolyric":true}""")]
    [InlineData("""{"code":200,"pureMusic":true,"lrc":{"lyric":"[00:00.00]纯音乐，请欣赏"}}""")]
    [InlineData("""{"code":200,"lrc":{"lyric":""}}""")]
    [InlineData("""{"code":200}""")]
    public void NoLyrics_IsNull(string json) => Assert.Null(NetEaseProvider.ParseLyrics(Json(json)));

    [Fact]
    public void EmptyTranslation_IsNull() =>
        Assert.Null(NetEaseProvider.ParseLyrics(Json("""{"lrc":{"lyric":"[00:01.00]a"},"tlyric":{"lyric":""}}"""))!.Translation);
}

public class EapiDomainFallbackTests
{
    /// <summary>桌面客户端域名重置连接（网易云风控时的表现），网页域名正常返回。</summary>
    private sealed class BlockedHostHandler : HttpMessageHandler
    {
        public List<string> Hosts { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var host = request.RequestUri!.Host;
            lock (Hosts) Hosts.Add(host);
            if (host == "interfacepc.music.163.com") throw new HttpRequestException("Connection reset");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"code":200,"data":[{"id":7,"url":"https://m701.music.126.net/x.mp3","br":192000,"type":"mp3"}]}"""),
            });
        }
    }

    [Fact]
    public async Task BlockedClientDomain_FallsBackAndRemembers()
    {
        var handler = new BlockedHostHandler();
        using var provider = new NetEaseProvider(new HttpClient(handler));
        var id = new Solisten.Core.Model.RemoteTrackId(Solisten.Core.Model.RemoteProvider.NetEase, "7");

        Assert.NotNull(await provider.GetStreamAsync(new ProviderCredential(""), id, StreamQuality.Higher, default));
        Assert.Equal(["interfacepc.music.163.com", "music.163.com"], handler.Hosts);

        handler.Hosts.Clear();
        Assert.NotNull(await provider.GetStreamAsync(new ProviderCredential(""), id, StreamQuality.Higher, default));
        Assert.Equal(["music.163.com"], handler.Hosts); // 记住了能用的域名
    }
}
