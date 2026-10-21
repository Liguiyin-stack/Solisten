using Solisten.Core.Model;
using Solisten.Integrations.NetEase.Api;

namespace Solisten.Integrations.NetEase.Auth;

public interface INeteaseQrLoginService
{
    Task<QrLoginSession> CreateAsync(CancellationToken ct);
    Task<QrLoginStatus> PollAsync(QrLoginSession session, CancellationToken ct);
    Task<RemoteAccount> ValidateAsync(CancellationToken ct);
    Task LogoutAsync(CancellationToken ct);
}

/// <summary>二维码轮询约束（设计文档 §7.3）。</summary>
public sealed record QrLoginPolicy
{
    public static readonly QrLoginPolicy Default = new();

    /// <summary>两次状态查询之间的间隔（1.5–2.5 秒）。</summary>
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>连续网络错误达到这个次数后转为可重试的失败，不无限重试。</summary>
    public int MaxConsecutiveFailures { get; init; } = 3;
}

/// <summary>
/// 扫码登录。流程：创建二维码 → 轮询状态 → 成功后立即用凭据读取账号验证 → 验证通过才写入安全存储。
/// 未经验证的会话、取消或过期的会话都不会保存。凭据只在这里和 Provider 之间传递，界面只拿到账号摘要。
/// </summary>
public sealed class NetEaseQrLoginService(IMusicProvider provider, ICredentialStore store, QrLoginPolicy? policy = null) : INeteaseQrLoginService
{
    private readonly QrLoginPolicy _policy = policy ?? QrLoginPolicy.Default;
    private int _polling;

    public IMusicProvider Provider => provider;

    /// <summary>本机是否保存了会话（不代表会话仍然有效）。</summary>
    public bool HasSession => store.Load(provider.Provider) is not null;

    public async Task<QrLoginSession> CreateAsync(CancellationToken ct)
    {
        var ticket = await provider.CreateQrLoginAsync(ct).ConfigureAwait(false);
        return new QrLoginSession(Guid.NewGuid().ToString("N"), ticket.Key, ticket.ExpiresAt, ticket.QrUrl, ticket.QrPng);
    }

    public async Task<QrLoginStatus> PollAsync(QrLoginSession session, CancellationToken ct)
    {
        if (DateTimeOffset.UtcNow >= session.ExpiresAt) return QrLoginStatus.Of(QrLoginState.Expired);

        var result = await provider.PollQrLoginAsync(session.ProviderKey, ct).ConfigureAwait(false);
        if (result.State == QrLoginState.Failed && !string.IsNullOrWhiteSpace(result.Message))
            return new QrLoginStatus(QrLoginState.Failed, $"登录失败：{result.Message}");
        if (result.State != QrLoginState.Succeeded) return QrLoginStatus.Of(result.State);
        if (result.Credential is not { } credential) return new QrLoginStatus(QrLoginState.Failed, "已扫码，但没有收到登录信息");

        RemoteAccount account;
        try
        {
            account = await provider.GetAccountAsync(credential, ct).ConfigureAwait(false);
        }
        catch (NetEaseApiException)
        {
            return new QrLoginStatus(QrLoginState.Failed, "已扫码，但无法确认登录状态");
        }
        store.Save(provider.Provider, account.UserId, credential);
        return new QrLoginStatus(QrLoginState.Succeeded, $"已登录：{account.Nickname}", account);
    }

    /// <summary>
    /// 轮询直到成功、过期、失败或取消。同一时间只允许一个轮询循环；只在状态变化时报告进度。
    /// 网络错误有限重试，超过次数后返回可重试的失败。
    /// </summary>
    public async Task<QrLoginStatus> WaitAsync(QrLoginSession session, IProgress<QrLoginStatus>? progress, CancellationToken ct)
    {
        if (Interlocked.Exchange(ref _polling, 1) == 1) throw new InvalidOperationException("已有一个扫码登录正在等待");
        try
        {
            QrLoginState? last = null;
            int failures = 0;
            while (true)
            {
                QrLoginStatus status;
                try
                {
                    status = await PollAsync(session, ct).ConfigureAwait(false);
                    failures = 0;
                }
                catch (NetEaseApiException e) when (e.IsTransient && ++failures < _policy.MaxConsecutiveFailures)
                {
                    await Task.Delay(_policy.PollInterval, ct).ConfigureAwait(false);
                    continue;
                }
                catch (NetEaseApiException e)
                {
                    status = new QrLoginStatus(QrLoginState.Failed, e.UserMessage);
                }

                if (status.State != last)
                {
                    last = status.State;
                    progress?.Report(status);
                }
                if (status.IsTerminal) return status;
                await Task.Delay(_policy.PollInterval, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return QrLoginStatus.Of(QrLoginState.Cancelled);
        }
        finally
        {
            Volatile.Write(ref _polling, 0);
        }
    }

    /// <summary>
    /// 用保存的会话读取账号，确认仍然有效。会话被远端判定失效时删除本机凭据（远程缓存保留）；
    /// 网络错误不删除凭据。
    /// </summary>
    public async Task<RemoteAccount> ValidateAsync(CancellationToken ct)
    {
        var credential = store.Load(provider.Provider) ?? throw new NetEaseApiException(NetEaseErrorKind.Unauthorized, "not signed in");
        try
        {
            return await provider.GetAccountAsync(credential, ct).ConfigureAwait(false);
        }
        catch (NetEaseApiException e) when (e.Kind == NetEaseErrorKind.Unauthorized)
        {
            store.Delete(provider.Provider);
            throw;
        }
    }

    /// <summary>退出登录：尽力通知远端注销，然后删除本机凭据。远程缓存是否删除由调用方决定。</summary>
    public async Task LogoutAsync(CancellationToken ct)
    {
        var credential = store.Load(provider.Provider);
        try
        {
            if (credential is not null) await provider.LogoutAsync(credential, ct).ConfigureAwait(false);
        }
        catch (NetEaseApiException) { } // 远端注销失败不影响本机退出
        finally
        {
            store.Delete(provider.Provider);
        }
    }
}
