using Solisten.Integrations.NetEase.Diagnostics;

namespace Solisten.Integrations.NetEase.Api;

public enum NetEaseErrorKind
{
    /// <summary>连接失败、DNS 错误等。</summary>
    Network,
    Timeout,
    /// <summary>429 或服务端风控。不快速重试。</summary>
    RateLimited,
    /// <summary>登录态失效或从未登录。</summary>
    Unauthorized,
    /// <summary>5xx、网关错误。</summary>
    ServerError,
    /// <summary>响应结构无法识别（接口可能已经变化）。</summary>
    InvalidResponse,
    /// <summary>集成整体不可用（没有可用的 Provider、接口下线）。</summary>
    Unavailable,
}

/// <summary>Provider 返回的结构化错误。消息在构造时脱敏，可以直接写入日志。</summary>
public sealed class NetEaseApiException(NetEaseErrorKind kind, string message, Exception? inner = null, TimeSpan? retryAfter = null)
    : Exception(Redactor.Redact(message), inner)
{
    public NetEaseErrorKind Kind { get; } = kind;

    /// <summary>服务端建议的重试间隔（429 的 Retry-After）。</summary>
    public TimeSpan? RetryAfter { get; } = retryAfter;

    /// <summary>可以有限退避后重试的错误。</summary>
    public bool IsTransient => Kind is NetEaseErrorKind.Network or NetEaseErrorKind.Timeout or NetEaseErrorKind.ServerError;

    /// <summary>出现后整个同步应当停止（继续请求其他歌单也不会成功）。</summary>
    public bool StopsSync => Kind is NetEaseErrorKind.Unauthorized or NetEaseErrorKind.RateLimited or NetEaseErrorKind.Unavailable;

    /// <summary>给用户看的说明（设计文档 §12）。</summary>
    public string UserMessage => Kind switch
    {
        NetEaseErrorKind.Network => "网络连接失败",
        NetEaseErrorKind.Timeout => "网络请求超时",
        NetEaseErrorKind.RateLimited => "服务暂时限制请求，请稍后再试",
        NetEaseErrorKind.Unauthorized => "登录已失效，请重新登录",
        NetEaseErrorKind.ServerError => "网易云服务暂时不可用",
        _ => "网易云集成暂不可用",
    };
}
