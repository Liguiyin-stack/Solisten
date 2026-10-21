using Solisten.Core.Model;
using Solisten.Integrations.NetEase.Api;
using Solisten.Integrations.NetEase.Auth;
using Solisten.Integrations.NetEase.Diagnostics;

namespace Solisten.Integrations.NetEase.Tests;

internal sealed class Recorder<T> : IProgress<T>
{
    public List<T> Items { get; } = [];
    public void Report(T value) { lock (Items) Items.Add(value); }
}

public class QrLoginTests
{
    private static readonly QrLoginPolicy Fast = new() { PollInterval = TimeSpan.FromMilliseconds(1) };

    private static (FakeMusicProvider Provider, InMemoryCredentialStore Store, NetEaseQrLoginService Login) Create(params int[] codes)
    {
        var provider = new FakeMusicProvider();
        if (codes.Length > 0) provider.QrCodes = codes;
        var store = new InMemoryCredentialStore();
        return (provider, store, new NetEaseQrLoginService(provider, store, Fast));
    }

    [Theory]
    [InlineData(800, QrLoginState.Expired)]
    [InlineData(801, QrLoginState.WaitingForScan)]
    [InlineData(802, QrLoginState.WaitingForConfirmation)]
    [InlineData(803, QrLoginState.Succeeded)]
    [InlineData(500, QrLoginState.Failed)]
    [InlineData(0, QrLoginState.Failed)]
    public void StatusCodes_Map(int code, QrLoginState expected) => Assert.Equal(expected, QrStatusCodes.Map(code));

    [Fact]
    public async Task ScanConfirmSucceed_SavesValidatedCredential_AndReportsOnlyChanges()
    {
        var (provider, store, login) = Create(801, 801, 801, 802, 803);
        var session = await login.CreateAsync(default);
        var progress = new Recorder<QrLoginStatus>();

        var result = await login.WaitAsync(session, progress, default);

        Assert.Equal(QrLoginState.Succeeded, result.State);
        Assert.Equal(provider.Account, result.Account);
        Assert.NotNull(store.Load(RemoteProvider.NetEase));
        Assert.Equal([QrLoginState.WaitingForScan, QrLoginState.WaitingForConfirmation, QrLoginState.Succeeded], progress.Items.Select(s => s.State));
    }

    [Fact]
    public async Task ExpiredCode_DoesNotSaveCredential()
    {
        var (_, store, login) = Create(801, 800);
        var result = await login.WaitAsync(await login.CreateAsync(default), null, default);
        Assert.Equal(QrLoginState.Expired, result.State);
        Assert.Null(store.Load(RemoteProvider.NetEase));
    }

    [Fact]
    public async Task LocallyExpiredSession_IsNotPolled()
    {
        var (provider, store, login) = Create(803);
        provider.QrLifetime = TimeSpan.FromSeconds(-1);
        var session = await login.CreateAsync(default);
        int calls = provider.Calls;

        var result = await login.PollAsync(session, default);

        Assert.Equal(QrLoginState.Expired, result.State);
        Assert.Equal(calls, provider.Calls);
        Assert.Null(store.Load(RemoteProvider.NetEase));
    }

    [Fact]
    public async Task ScannedButValidationFails_DoesNotSaveCredential()
    {
        var (provider, store, login) = Create(803);
        provider.Fail = call => call == "account" ? new NetEaseApiException(NetEaseErrorKind.ServerError, "502") : null;

        var result = await login.WaitAsync(await login.CreateAsync(default), null, default);

        Assert.Equal(QrLoginState.Failed, result.State);
        Assert.Equal("已扫码，但无法确认登录状态", result.Message);
        Assert.Null(store.Load(RemoteProvider.NetEase));
    }

    [Fact]
    public async Task Cancel_StopsPolling_AndSavesNothing()
    {
        var (provider, store, login) = Create(801);
        var session = await login.CreateAsync(default);
        using var cts = new CancellationTokenSource();
        var wait = login.WaitAsync(session, new Progress<QrLoginStatus>(_ => cts.Cancel()), cts.Token);

        var result = await wait;
        int calls = provider.Calls;
        await Task.Delay(30);

        Assert.Equal(QrLoginState.Cancelled, result.State);
        Assert.Equal(calls, provider.Calls);
        Assert.Null(store.Load(RemoteProvider.NetEase));
    }

    [Fact]
    public async Task NetworkErrors_RetryLimited_ThenFail()
    {
        var (provider, _, login) = Create(801);
        var session = await login.CreateAsync(default);
        int polls = 0;
        provider.Fail = call => call == "poll" && ++polls > 0 ? new NetEaseApiException(NetEaseErrorKind.Network, "offline") : null;

        var result = await login.WaitAsync(session, null, default);

        Assert.Equal(QrLoginState.Failed, result.State);
        Assert.Equal(QrLoginPolicy.Default.MaxConsecutiveFailures, polls);
    }

    [Fact]
    public async Task OnlyOnePollingLoop_PerService()
    {
        var (_, _, login) = Create(801);
        var session = await login.CreateAsync(default);
        using var cts = new CancellationTokenSource();
        var first = login.WaitAsync(session, null, cts.Token);

        await Assert.ThrowsAsync<InvalidOperationException>(() => login.WaitAsync(session, null, default));

        cts.Cancel();
        Assert.Equal(QrLoginState.Cancelled, (await first).State);
    }

    [Fact]
    public async Task Logout_DeletesCredential_EvenWhenRemoteLogoutFails()
    {
        var (provider, store, login) = Create(803);
        await login.WaitAsync(await login.CreateAsync(default), null, default);
        provider.Fail = call => call == "logout" ? new NetEaseApiException(NetEaseErrorKind.Network, "offline") : null;

        await login.LogoutAsync(default);

        Assert.Null(store.Load(RemoteProvider.NetEase));
        Assert.False(login.HasSession);
    }

    [Fact]
    public async Task Validate_WithExpiredSession_DeletesCredential_ButNetworkErrorKeepsIt()
    {
        var (provider, store, login) = Create();
        store.Save(RemoteProvider.NetEase, "u", new ProviderCredential("fake-session"));

        provider.Fail = call => call == "account" ? new NetEaseApiException(NetEaseErrorKind.Network, "offline") : null;
        await Assert.ThrowsAsync<NetEaseApiException>(() => login.ValidateAsync(default));
        Assert.NotNull(store.Load(RemoteProvider.NetEase));

        provider.Fail = null;
        store.Save(RemoteProvider.NetEase, "u", new ProviderCredential("stale"));
        var e = await Assert.ThrowsAsync<NetEaseApiException>(() => login.ValidateAsync(default));
        Assert.Equal(NetEaseErrorKind.Unauthorized, e.Kind);
        Assert.Null(store.Load(RemoteProvider.NetEase));
    }
}

public class RedactionTests
{
    [Theory]
    [InlineData("Cookie: MUSIC_U=abc123def; __csrf=xyz", "abc123def", "xyz")]
    [InlineData("set-cookie=MUSIC_U=secretvalue", "secretvalue", null)]
    [InlineData("GET /login/qr/check?key=9f8e7d&timestamp=1", "9f8e7d", null)]
    [InlineData("Authorization: Bearer abc.def", "abc.def", null)]
    [InlineData("{\"token\":\"t0k3n\"}", "t0k3n", null)]
    public void Redact_RemovesSecrets(string input, string secret, string? secret2)
    {
        var output = Redactor.Redact(input);
        Assert.DoesNotContain(secret, output);
        if (secret2 is not null) Assert.DoesNotContain(secret2, output);
        Assert.Contains(Redactor.Mask, output);
    }

    [Fact]
    public void Redact_LeavesOrdinaryTextAlone() =>
        Assert.Equal("同步完成：8 个歌单", Redactor.Redact("同步完成：8 个歌单"));

    [Fact]
    public void Credential_NeverPrintsItself() =>
        Assert.DoesNotContain("MUSIC_U", new ProviderCredential("MUSIC_U=abc").ToString());

    [Fact]
    public void ApiException_MessageIsRedacted() =>
        Assert.DoesNotContain("abc", new NetEaseApiException(NetEaseErrorKind.ServerError, "failed with MUSIC_U=abc").Message);
}

public class PasswordVaultTests
{
    [Fact]
    public void RoundTrip_SaveLoadDelete()
    {
        // 独立的资源名，不触碰真实的登录信息。
        var store = new PasswordVaultCredentialStore($"Solisten.Test.{Guid.NewGuid():N}");
        try
        {
            Assert.Null(store.Load(RemoteProvider.NetEase));
            store.Save(RemoteProvider.NetEase, "42", new ProviderCredential("MUSIC_U=abc; deviceId=D"));
            Assert.Equal("MUSIC_U=abc; deviceId=D", store.Load(RemoteProvider.NetEase)!.Secret);
            store.Save(RemoteProvider.NetEase, "43", new ProviderCredential("MUSIC_U=new"));
            Assert.Equal("MUSIC_U=new", store.Load(RemoteProvider.NetEase)!.Secret);
        }
        finally
        {
            store.Delete(RemoteProvider.NetEase);
        }
        Assert.Null(store.Load(RemoteProvider.NetEase));
    }
}

public class CachedCredentialStoreTests
{
    private sealed class CountingStore : ICredentialStore
    {
        public int Loads;
        public ProviderCredential? Value;
        public ProviderCredential? Load(RemoteProvider provider) { Loads++; return Value; }
        public void Save(RemoteProvider provider, string accountId, ProviderCredential credential) => Value = credential;
        public void Delete(RemoteProvider provider) => Value = null;
    }

    [Fact]
    public void LoadsOnce_AndTracksWrites()
    {
        var inner = new CountingStore();
        var store = new CachedCredentialStore(inner);
        Assert.Null(store.Load(RemoteProvider.NetEase));
        Assert.Null(store.Load(RemoteProvider.NetEase));
        Assert.Equal(1, inner.Loads);

        store.Save(RemoteProvider.NetEase, "1", new ProviderCredential("a"));
        Assert.Equal("a", store.Load(RemoteProvider.NetEase)!.Secret);
        Assert.Equal("a", inner.Value!.Secret);

        store.Delete(RemoteProvider.NetEase);
        Assert.Null(store.Load(RemoteProvider.NetEase));
        Assert.Null(inner.Value);
        Assert.Equal(1, inner.Loads);
    }
}
