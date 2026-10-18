using System.Text;

namespace Solisten.Library.Search;

/// <summary>
/// FTS5 unicode61 分词不会切分中文，所以入库时把每个汉字单独成词，查询时把汉字串变成短语。
/// 这样 "天" 和 "晴天" 都能命中 "晴天"，拉丁字母仍按前缀匹配。
/// </summary>
public static class SearchText
{
    public static string Index(string? text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        var sb = new StringBuilder(text.Length * 2);
        foreach (var c in text)
        {
            if (IsCjk(c)) sb.Append(' ').Append(c).Append(' ');
            else sb.Append(c);
        }
        return sb.ToString();
    }

    /// <summary>把用户输入转成 FTS5 MATCH 表达式；没有可搜索内容时返回 null。</summary>
    public static string? MatchQuery(string input)
    {
        var parts = new List<string>();
        foreach (var raw in input.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            var latin = new StringBuilder();
            var han = new List<char>();

            void FlushLatin()
            {
                if (latin.Length > 0) parts.Add($"\"{latin}\"*");
                latin.Clear();
            }

            void FlushHan()
            {
                if (han.Count > 0) parts.Add($"\"{string.Join(' ', han)}\"");
                han.Clear();
            }

            foreach (var c in raw)
            {
                if (IsCjk(c)) { FlushLatin(); han.Add(c); }
                else if (char.IsLetterOrDigit(c)) { FlushHan(); latin.Append(char.ToLowerInvariant(c)); }
                else { FlushLatin(); FlushHan(); }
            }
            FlushLatin();
            FlushHan();
        }
        return parts.Count == 0 ? null : string.Join(" AND ", parts);
    }

    private static bool IsCjk(char c) =>
        c is >= '぀' and <= 'ヿ'   // 假名
          or >= '㐀' and <= '䶿'   // 扩展 A
          or >= '一' and <= '鿿'   // 基本区
          or >= '가' and <= '힯'   // 韩文
          or >= '豈' and <= '﫿';
}
