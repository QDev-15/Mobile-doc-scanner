using System.Globalization;
using System.Text;

namespace DocScanner.Core;

/// <summary>Search as Vietnamese users type: without caring about case or diacritics ("hop dong" finds "Hợp đồng"),
/// every word of the query somewhere in the name.</summary>
public static class TextSearch
{
    public static bool Matches(string text, string? query)
    {
        if (string.IsNullOrWhiteSpace(query)) return true;
        string haystack = Fold(text);
        return Fold(query).Split(' ', StringSplitOptions.RemoveEmptyEntries).All(haystack.Contains);
    }

    /// <summary>Lower case, diacritics removed, đ -> d.</summary>
    public static string Fold(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (char c in s.Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark) continue;
            char l = char.ToLowerInvariant(c);
            sb.Append(l == 'đ' ? 'd' : l);
        }
        return sb.ToString();
    }
}
