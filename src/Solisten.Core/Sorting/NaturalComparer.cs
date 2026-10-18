using System.Globalization;

namespace Solisten.Core.Sorting;

/// <summary>自然排序：数字段按数值比较（"2" &lt; "10"，"01" 与 "1" 数值相等时短者在前），其余按不区分大小写比较。</summary>
public sealed class NaturalComparer : IComparer<string?>
{
    public static readonly NaturalComparer Instance = new();

    private static readonly CompareInfo Compare = CultureInfo.InvariantCulture.CompareInfo;

    int IComparer<string?>.Compare(string? x, string? y) => CompareStrings(x, y);

    public static int CompareStrings(string? x, string? y)
    {
        if (ReferenceEquals(x, y)) return 0;
        if (x is null) return -1;
        if (y is null) return 1;

        int i = 0, j = 0;
        while (i < x.Length && j < y.Length)
        {
            bool dx = char.IsAsciiDigit(x[i]), dy = char.IsAsciiDigit(y[j]);
            if (dx != dy) return dx ? -1 : 1;
            if (dx)
            {
                int si = i, sj = j;
                while (i < x.Length && char.IsAsciiDigit(x[i])) i++;
                while (j < y.Length && char.IsAsciiDigit(y[j])) j++;
                int c = CompareDigits(x.AsSpan(si, i - si), y.AsSpan(sj, j - sj));
                if (c != 0) return c;
            }
            else
            {
                int si = i, sj = j;
                while (i < x.Length && !char.IsAsciiDigit(x[i])) i++;
                while (j < y.Length && !char.IsAsciiDigit(y[j])) j++;
                int c = Compare.Compare(x.AsSpan(si, i - si), y.AsSpan(sj, j - sj), CompareOptions.IgnoreCase);
                if (c != 0) return c;
            }
        }
        int rest = (x.Length - i).CompareTo(y.Length - j);
        return rest != 0 ? rest : string.CompareOrdinal(x, y);
    }

    private static int CompareDigits(ReadOnlySpan<char> a, ReadOnlySpan<char> b)
    {
        var ta = a.TrimStart('0');
        var tb = b.TrimStart('0');
        if (ta.Length != tb.Length) return ta.Length.CompareTo(tb.Length);
        int c = ta.SequenceCompareTo(tb);
        if (c != 0) return Math.Sign(c);
        return a.Length.CompareTo(b.Length);
    }
}
