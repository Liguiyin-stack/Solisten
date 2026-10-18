using System.Text;

namespace Solisten.Library.Search;

/// <summary>汉字 → 拼音（无声调，取首个读音）。数据来自 mozillazg/pinyin-data（MIT）。</summary>
public static class Pinyin
{
    private const int First = 0x4E00;
    private const int Last = 0x9FFF;

    private static readonly Lazy<string[]> Table = new(Load);

    private static string[] Load()
    {
        using var s = typeof(Pinyin).Assembly.GetManifestResourceStream("Solisten.Library.pinyin")
            ?? throw new InvalidOperationException("Missing pinyin resource");
        using var r = new StreamReader(s, Encoding.ASCII);
        var table = new string[Last - First + 1];
        for (int i = 0; i < table.Length; i++) table[i] = r.ReadLine() ?? "";
        return table;
    }

    public static bool IsHan(char c) => c >= First && c <= Last;

    public static string? Of(char c) => IsHan(c) && Table.Value[c - First] is { Length: > 0 } p ? p : null;

    /// <summary>生成搜索列：全拼连写 + 首字母，例如 "晴天" → "qingtian qt"。不含汉字时返回空字符串。</summary>
    public static string SearchKey(string text)
    {
        var full = new StringBuilder();
        var initials = new StringBuilder();
        bool any = false;

        void Flush(List<string> words)
        {
            if (full.Length > 0) { words.Add(full.ToString()); words.Add(initials.ToString()); }
            full.Clear();
            initials.Clear();
        }

        var result = new List<string>();
        foreach (var c in text)
        {
            if (Of(c) is { } py)
            {
                any = true;
                full.Append(py);
                initials.Append(py[0]);
            }
            else if (char.IsLetterOrDigit(c))
            {
                // 混排的字母数字参与连写，例如 "七里香2" → "qilixiang2"
                if (full.Length > 0) { full.Append(char.ToLowerInvariant(c)); initials.Append(char.ToLowerInvariant(c)); }
            }
            else
            {
                Flush(result);
            }
        }
        Flush(result);
        return any ? string.Join(' ', result.Distinct()) : "";
    }
}
