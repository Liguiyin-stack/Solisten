using Solisten.Core.Model;

namespace Solisten.Integrations.NetEase.Auth;

public enum QrLoginState
{
    WaitingForScan,
    WaitingForConfirmation,
    Succeeded,
    Expired,
    Failed,
    Cancelled,
}

/// <summary>一次扫码登录会话。<see cref="ProviderKey"/> 只在内存中，不写日志、不持久化。</summary>
public sealed record QrLoginSession(
    string SessionId,
    string ProviderKey,
    DateTimeOffset ExpiresAt,
    Uri? QrUrl,
    byte[]? QrPng);

/// <summary>扫码状态。登录成功时附带已经验证过的账号。</summary>
public sealed record QrLoginStatus(QrLoginState State, string Message, RemoteAccount? Account = null)
{
    public bool IsTerminal => State is QrLoginState.Succeeded or QrLoginState.Expired or QrLoginState.Failed or QrLoginState.Cancelled;

    public static QrLoginStatus Of(QrLoginState state) => new(state, state switch
    {
        QrLoginState.WaitingForScan => "请使用网易云音乐 App 扫码",
        QrLoginState.WaitingForConfirmation => "已扫码，请在手机上确认登录",
        QrLoginState.Succeeded => "登录成功",
        QrLoginState.Expired => "二维码已过期，点击刷新",
        QrLoginState.Cancelled => "已取消",
        _ => "登录失败",
    });
}

/// <summary>
/// 社区接口约定的扫码状态码（800 过期、801 等待扫码、802 待确认、803 成功）。
/// 这不是网易云官方的稳定承诺，只供原型适配器使用；未知状态码一律视为失败。
/// </summary>
public static class QrStatusCodes
{
    public const int Expired = 800;
    public const int WaitingForScan = 801;
    public const int WaitingForConfirmation = 802;
    public const int Succeeded = 803;

    public static QrLoginState Map(int code) => code switch
    {
        Expired => QrLoginState.Expired,
        WaitingForScan => QrLoginState.WaitingForScan,
        WaitingForConfirmation => QrLoginState.WaitingForConfirmation,
        Succeeded => QrLoginState.Succeeded,
        _ => QrLoginState.Failed,
    };
}
