using System.Runtime.InteropServices;
using Solisten.Core.Model;
using Solisten.Integrations.NetEase.Api;
using Windows.Security.Credentials;

namespace Solisten.Integrations.NetEase.Auth;

/// <summary>
/// 会话凭据的本机安全存储（设计文档 §7.4）。凭据从不进入 settings.json、SQLite、日志或剪贴板。
/// 每个来源只保存一个账号的会话。
/// </summary>
public interface ICredentialStore
{
    ProviderCredential? Load(RemoteProvider provider);

    void Save(RemoteProvider provider, string accountId, ProviderCredential credential);

    void Delete(RemoteProvider provider);
}

/// <summary>Windows 凭据管理器（PasswordVault），按当前 Windows 用户加密保存。</summary>
public sealed class PasswordVaultCredentialStore(string resourcePrefix = "Solisten") : ICredentialStore
{
    private string Resource(RemoteProvider provider) => $"{resourcePrefix}.{provider}";

    public ProviderCredential? Load(RemoteProvider provider)
    {
        try
        {
            var vault = new PasswordVault();
            var entry = vault.FindAllByResource(Resource(provider)).FirstOrDefault();
            if (entry is null) return null;
            entry.RetrievePassword();
            return string.IsNullOrEmpty(entry.Password) ? null : new ProviderCredential(entry.Password);
        }
        catch (COMException) { return null; } // 没有保存过：FindAllByResource 抛出"找不到元素"
    }

    public void Save(RemoteProvider provider, string accountId, ProviderCredential credential)
    {
        Delete(provider);
        new PasswordVault().Add(new PasswordCredential(Resource(provider), accountId, credential.Secret));
    }

    public void Delete(RemoteProvider provider)
    {
        try
        {
            var vault = new PasswordVault();
            foreach (var entry in vault.FindAllByResource(Resource(provider)).ToList()) vault.Remove(entry);
        }
        catch (COMException) { }
    }
}

/// <summary>只在内存中保存（测试和开发用的假 Provider）。</summary>
public sealed class InMemoryCredentialStore : ICredentialStore
{
    private readonly Dictionary<RemoteProvider, (string Account, ProviderCredential Credential)> _items = [];
    private readonly Lock _gate = new();

    public ProviderCredential? Load(RemoteProvider provider)
    {
        lock (_gate) return _items.TryGetValue(provider, out var v) ? v.Credential : null;
    }

    public void Save(RemoteProvider provider, string accountId, ProviderCredential credential)
    {
        lock (_gate) _items[provider] = (accountId, credential);
    }

    public void Delete(RemoteProvider provider)
    {
        lock (_gate) _items.Remove(provider);
    }
}

/// <summary>
/// 在内存中缓存另一个存储的结果：凭据管理器的访问可能需要几百毫秒到数秒，
/// 界面判断"是否已登录"时不应每次都访问它。写入与删除直接穿透到底层存储。
/// </summary>
public sealed class CachedCredentialStore(ICredentialStore inner) : ICredentialStore
{
    private readonly Lock _gate = new();
    private readonly Dictionary<RemoteProvider, ProviderCredential?> _cache = [];

    public ProviderCredential? Load(RemoteProvider provider)
    {
        lock (_gate)
        {
            if (_cache.TryGetValue(provider, out var cached)) return cached;
        }
        var loaded = inner.Load(provider);
        lock (_gate) return _cache.TryAdd(provider, loaded) ? loaded : _cache[provider];
    }

    public void Save(RemoteProvider provider, string accountId, ProviderCredential credential)
    {
        inner.Save(provider, accountId, credential);
        lock (_gate) _cache[provider] = credential;
    }

    public void Delete(RemoteProvider provider)
    {
        lock (_gate) _cache[provider] = null;
        inner.Delete(provider);
    }
}
