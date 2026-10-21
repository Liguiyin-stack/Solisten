using Solisten.Core.Model;
using Solisten.Integrations.NetEase.Auth;

namespace Solisten.Integrations.NetEase.Api;

/// <summary>
/// 可替换的 Provider Adapter（设计文档 §4.1）：登录与歌单接口的唯一入口。
/// 官方授权接口与非官方原型接口都实现它，上层（登录、同步、界面）不感知具体接口。
/// 所有方法都必须尊重 <see cref="CancellationToken"/>，失败时抛出 <see cref="NetEaseApiException"/>。
/// </summary>
public interface IMusicProvider
{
    RemoteProvider Provider { get; }

    /// <summary>界面上显示的名称，非官方适配器需要在这里写明。</summary>
    string DisplayName { get; }

    /// <summary>是否为官方授权接口。非官方适配器只能在用户显式开启后使用，并在界面上标注。</summary>
    bool IsOfficial { get; }

    Task<QrLoginTicket> CreateQrLoginAsync(CancellationToken ct);

    /// <summary>查询一次扫码状态。登录成功时附带会话凭据，凭据只交给 <see cref="ICredentialStore"/>。</summary>
    Task<QrPollResult> PollQrLoginAsync(string key, CancellationToken ct);

    /// <summary>用凭据读取账号信息（同时用于验证登录态）。凭据失效时抛出 <see cref="NetEaseErrorKind.Unauthorized"/>。</summary>
    Task<RemoteAccount> GetAccountAsync(ProviderCredential credential, CancellationToken ct);

    /// <summary>尽力通知远端注销。</summary>
    Task LogoutAsync(ProviderCredential credential, CancellationToken ct);

    Task<RemotePage<RemotePlaylist>> GetPlaylistsAsync(ProviderCredential credential, string userId, int offset, int limit, CancellationToken ct);

    Task<RemotePage<RemoteTrack>> GetPlaylistTracksAsync(ProviderCredential credential, string playlistId, int offset, int limit, CancellationToken ct);

    /// <summary>
    /// 请求一首歌的在线播放地址。远端按账号权限决定给完整音频、试听片段，或不给（返回 null：无版权、已下架、需要购买）。
    /// 不使用任何第三方音源。
    /// </summary>
    Task<RemoteStream?> GetStreamAsync(ProviderCredential credential, RemoteTrackId track, StreamQuality quality, CancellationToken ct);

    /// <summary>歌词（LRC 文本）和翻译。纯音乐或远端没有歌词时返回 null。</summary>
    Task<RemoteLyrics?> GetLyricsAsync(ProviderCredential credential, RemoteTrackId track, CancellationToken ct);
}

public sealed record RemotePage<T>(IReadOnlyList<T> Items, bool HasMore);

/// <summary>Provider 创建的一次性二维码。二维码内容只用于显示，不写日志、不持久化。</summary>
public sealed record QrLoginTicket(string Key, DateTimeOffset ExpiresAt, Uri? QrUrl, byte[]? QrPng);

/// <summary>一次扫码查询的结果。<see cref="Message"/> 是远端给出的说明（例如风控提示），失败时显示给用户。</summary>
public sealed record QrPollResult(QrLoginState State, ProviderCredential? Credential = null, string? Message = null);

/// <summary>
/// 不透明的会话凭据（Cookie 或令牌）。ToString 永远不输出内容，避免意外进入日志或界面。
/// 只有 Provider 和 <see cref="ICredentialStore"/> 读取 <see cref="Secret"/>。
/// </summary>
public sealed class ProviderCredential(string secret)
{
    public string Secret { get; } = secret;

    public override string ToString() => "ProviderCredential(***)";
}

/// <summary>在线播放的音质。远端会按账号权限降级（例如非会员拿不到无损）。</summary>
public enum StreamQuality { Standard, Higher, ExHigh, Lossless }

/// <summary>
/// 一首在线歌曲的临时播放地址。地址很快过期（网易云约 20 分钟），只在播放前请求，不持久化、不写日志。
/// <see cref="TrialStart"/> / <see cref="TrialEnd"/> 不为空时，地址只包含远端提供的试听片段（账号没有完整播放权限）。
/// </summary>
public sealed record RemoteStream(
    Uri Url,
    int Bitrate,
    string Format,
    TimeSpan? TrialStart,
    TimeSpan? TrialEnd,
    DateTimeOffset ExpiresAt)
{
    public bool IsTrial => TrialEnd is not null;

    public TimeSpan? TrialLength => TrialEnd is { } end ? end - (TrialStart ?? TimeSpan.Zero) : null;

    public override string ToString() => $"RemoteStream({Format}, {Bitrate / 1000} kbps{(IsTrial ? ", trial" : "")})";
}

/// <summary>远端歌词：原文和翻译都是 LRC 文本（翻译可能为空）。</summary>
public sealed record RemoteLyrics(string Lyric, string? Translation);
