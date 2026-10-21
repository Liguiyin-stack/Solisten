using System.Diagnostics;
using Solisten.Integrations.NetEase.Api;

namespace Solisten.Integrations.NetEase.Sync;

/// <summary>同步时的分页、超时、限速和重试（设计文档 §8.2）。</summary>
public sealed record SyncPolicy
{
    public static readonly SyncPolicy Default = new();

    public int PageSize { get; init; } = 100;

    /// <summary>单次请求的总超时。</summary>
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(20);

    /// <summary>两次请求之间的最小间隔（同一时间只有一个请求在进行）。</summary>
    public TimeSpan MinRequestInterval { get; init; } = TimeSpan.FromMilliseconds(300);

    /// <summary>网络错误、超时、5xx 的最多尝试次数（含第一次）。</summary>
    public int MaxAttempts { get; init; } = 3;

    /// <summary>第一次重试前的等待，之后每次翻倍。</summary>
    public TimeSpan RetryBaseDelay { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>被限速后等待一次再重试；仍被限速就停止同步。</summary>
    public TimeSpan RateLimitDelay { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>防止异常分页（远端一直返回 HasMore）导致无限请求。</summary>
    public int MaxPages { get; init; } = 200;
}

/// <summary>
/// 串行化 + 最小间隔 + 超时 + 有限重试。取消令牌一直传到 Provider。
/// </summary>
internal sealed class SyncRateLimiter(SyncPolicy policy)
{
    private readonly SemaphoreSlim _one = new(1, 1);
    private long _last;

    public async Task<T> RunAsync<T>(Func<CancellationToken, Task<T>> call, CancellationToken ct)
    {
        bool rateLimitedOnce = false;
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                return await OnceAsync(call, ct).ConfigureAwait(false);
            }
            catch (NetEaseApiException e) when (e.Kind == NetEaseErrorKind.RateLimited && !rateLimitedOnce)
            {
                rateLimitedOnce = true;
                await Task.Delay(e.RetryAfter is { } after && after < policy.RateLimitDelay * 3 ? after : policy.RateLimitDelay, ct).ConfigureAwait(false);
            }
            catch (NetEaseApiException e) when (e.IsTransient && attempt < policy.MaxAttempts)
            {
                await Task.Delay(policy.RetryBaseDelay * Math.Pow(2, attempt - 1), ct).ConfigureAwait(false);
            }
        }
    }

    private async Task<T> OnceAsync<T>(Func<CancellationToken, Task<T>> call, CancellationToken ct)
    {
        await _one.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var wait = _last == 0 ? TimeSpan.Zero : policy.MinRequestInterval - Stopwatch.GetElapsedTime(_last);
            if (wait > TimeSpan.Zero) await Task.Delay(wait, ct).ConfigureAwait(false);

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(policy.RequestTimeout);
            try
            {
                return await call(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new NetEaseApiException(NetEaseErrorKind.Timeout, "request timed out");
            }
        }
        finally
        {
            _last = Stopwatch.GetTimestamp();
            _one.Release();
        }
    }
}
