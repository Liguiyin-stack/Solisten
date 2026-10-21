using System.Numerics;
using System.Security.Cryptography;
using System.Text;

namespace Solisten.Integrations.NetEase.Api;

/// <summary>
/// 网易云客户端请求加密，与社区客户端（NeteaseCloudMusicApi）的做法一致，不是官方文档化的接口。
/// weapi（网页端）：JSON 先用固定密钥 AES-CBC 加密，再用随机密钥加密一次；随机密钥反转后用网页端公开的 RSA 公钥（无填充）加密。
/// </summary>
internal static class NetEaseCrypto
{
    private const string PresetKey = "0CoJUm6Qyw8W8jud";
    private const string Iv = "0102030405060708";
    private const string Base62 = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";
    private const string Modulus =
        "00e0b509f6259df8642dbc35662901477df22677ec152b5ff68ace615bb7b725152b3ab17a876aea8a5aa76d2e417629ec4ee341f56135fccf695280104e0312ecbda92557c93870114af6c9d05c4f7f0c3685b7a46bee255932575cce10b424d813cfe4875d3e82047b97ddef52741d546b8e289dc6935b3ece0462db0a22b8e7";
    private static readonly BigInteger N = BigInteger.Parse(Modulus, System.Globalization.NumberStyles.HexNumber);
    private static readonly BigInteger E = 0x10001;

    public static (string Params, string EncSecKey) Weapi(string json, string? secretKey = null)
    {
        secretKey ??= new string(RandomNumberGenerator.GetItems<char>(Base62, 16));
        var first = AesCbc(json, PresetKey);
        var second = AesCbc(first, secretKey);
        return (second, Rsa(new string([.. secretKey.Reverse()])));
    }

    private const string EapiKey = "e82ckenh8dichen8";

    /// <summary>
    /// 桌面客户端（eapi）请求加密：把接口路径、JSON 和二者的 MD5 拼接后用固定密钥 AES-ECB 加密，输出大写十六进制。
    /// </summary>
    public static string Eapi(string uri, string json)
    {
        var digest = Convert.ToHexStringLower(MD5.HashData(Encoding.UTF8.GetBytes($"nobody{uri}use{json}md5forencrypt")));
        using var aes = Aes.Create();
        aes.Key = Encoding.UTF8.GetBytes(EapiKey);
        return Convert.ToHexString(aes.EncryptEcb(Encoding.UTF8.GetBytes($"{uri}-36cd479b6b5-{json}-36cd479b6b5-{digest}"), PaddingMode.PKCS7));
    }

    private static string AesCbc(string text, string key)
    {
        using var aes = Aes.Create();
        aes.Key = Encoding.UTF8.GetBytes(key);
        return Convert.ToBase64String(aes.EncryptCbc(Encoding.UTF8.GetBytes(text), Encoding.UTF8.GetBytes(Iv), PaddingMode.PKCS7));
    }

    internal static string Rsa(string text)
    {
        var m = new BigInteger(Encoding.UTF8.GetBytes(text), isUnsigned: true, isBigEndian: true);
        var bytes = BigInteger.ModPow(m, E, N).ToByteArray(isUnsigned: true, isBigEndian: true);
        return Convert.ToHexStringLower(bytes).PadLeft(256, '0');
    }
}
