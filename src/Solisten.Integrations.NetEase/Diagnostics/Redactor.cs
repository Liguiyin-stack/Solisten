using System.Text.RegularExpressions;

namespace Solisten.Integrations.NetEase.Diagnostics;

/// <summary>
/// 日志脱敏：写日志、异常消息之前去掉 Cookie、认证 header、登录 key 等会话材料（设计文档 §13）。
/// 只能兜底，不能代替"不把凭据放进消息"的约定。
/// </summary>
public static partial class Redactor
{
    public const string Mask = "***";

    /// <summary>键值形式：MUSIC_U=…、key=…、"token":"…"。</summary>
    [GeneratedRegex(@"(?i)\b(MUSIC_[A-Z]+|__csrf|NMTID|__remember_me|set-cookie|cookie|token|access_token|refresh_token|unikey|qr_?key|key|session)(""?\s*[=:]\s*)(""[^""]*""|[^;\s&,""]+)")]
    private static partial Regex Pairs();

    /// <summary>认证 header 的值一直到行尾（"Bearer xxx" 含空格）。</summary>
    [GeneratedRegex(@"(?i)\b(authorization|proxy-authorization)(\s*:\s*)([^\r\n]+)")]
    private static partial Regex AuthHeaders();

    public static string Redact(string? text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        var s = AuthHeaders().Replace(text, m => $"{m.Groups[1].Value}{m.Groups[2].Value}{Mask}");
        return Pairs().Replace(s, m => m.Groups[3].Value == Mask ? m.Value : $"{m.Groups[1].Value}{m.Groups[2].Value}{Mask}");
    }
}
