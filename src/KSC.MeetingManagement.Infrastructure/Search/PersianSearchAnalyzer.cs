using System.Text;
using System.Text.RegularExpressions;
using System.Globalization;

namespace KSC.MeetingManagement.Infrastructure.Search;

/// <summary>Stable canonicalisation shared by Persian indexing and query analysis.</summary>
public static partial class PersianSearchText
{
    private static readonly Regex WhiteSpace = WhiteSpaceRegex();

    public static string Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        var builder = new StringBuilder(value.Length);
        foreach (var character in value.Normalize(NormalizationForm.FormKC))
        {
            if (char.GetUnicodeCategory(character) is UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark or UnicodeCategory.EnclosingMark)
                continue;

            var mapped = character switch
            {
                '\u064A' or '\u0649' => '\u06CC', // Arabic yeh/alef maksura → Persian yeh
                '\u0643' => '\u06A9',             // Arabic kaf → Persian kaf
                '\u200C' or '\u200D' or '\uFEFF' => ' ',
                >= '\u0660' and <= '\u0669' => (char)('0' + character - '\u0660'),
                >= '\u06F0' and <= '\u06F9' => (char)('0' + character - '\u06F0'),
                '\u0640' => '\0',                 // tatweel
                _ => character,
            };
            if (mapped != '\0') builder.Append(char.ToLowerInvariant(mapped));
        }
        return WhiteSpace.Replace(builder.ToString(), " ").Trim();
    }

    public static IReadOnlyList<string> Tokens(string? value) =>
        Normalize(value).Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    public static string Snippet(string? content, string query, int maxLength = 180)
    {
        var normalized = Normalize(content);
        if (normalized.Length <= maxLength) return normalized;
        var first = Tokens(query).Select(token => normalized.IndexOf(token, StringComparison.Ordinal)).Where(index => index >= 0).DefaultIfEmpty(0).Min();
        var start = Math.Clamp(first - maxLength / 3, 0, Math.Max(0, normalized.Length - maxLength));
        var length = Math.Min(maxLength, normalized.Length - start);
        return (start > 0 ? "…" : string.Empty) + normalized.AsSpan(start, length).ToString() + (start + maxLength < normalized.Length ? "…" : string.Empty);
    }

    [GeneratedRegex("\\s+", RegexOptions.CultureInvariant)]
    private static partial Regex WhiteSpaceRegex();
}
