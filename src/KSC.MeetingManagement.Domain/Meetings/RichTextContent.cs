using System.Net;
using System.Text.RegularExpressions;
using Ganss.Xss;

namespace KSC.MeetingManagement.Domain.Meetings;

/// <summary>Stores a small, semantic HTML subset that can be safely rendered and exported to Word later.</summary>
public static partial class RichTextContent
{
    private static readonly string[] AllowedElements = ["p", "br", "strong", "b", "em", "i", "u", "ul", "ol", "li", "blockquote", "h2", "h3", "h4"];
    private static readonly Regex RichMarkupPattern = new("</?(p|br|strong|b|em|i|u|ul|ol|li|blockquote|h[2-4])\\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>Maximum database length for sanitized markup at the given visible-text limit.</summary>
    public static int MaxStoredLength(int maxTextLength)
    {
        if (maxTextLength < 1)
            throw new ArgumentOutOfRangeException(nameof(maxTextLength), "The visible-text limit must be positive.");
        // Reserve room for HTML entities and repeated inline/block wrappers around visible characters.
        return checked(maxTextLength * 64 + 4096);
    }

    public static string Sanitize(string? value, int maxTextLength)
    {
        var input = value ?? string.Empty;
        if (!RichMarkupPattern.IsMatch(input))
        {
            var plainText = input.Trim();
            if (plainText.Length > maxTextLength)
                throw new MeetingDomainException($"متن نباید بیشتر از {maxTextLength} نویسه باشد.");
            return plainText;
        }

        var sanitizer = CreateSanitizer();
        var clean = sanitizer.Sanitize(input).Trim();
        if (clean.Length == 0) return string.Empty;

        if (TextLength(clean) > maxTextLength)
            throw new MeetingDomainException($"متن نباید بیشتر از {maxTextLength} نویسه باشد.");
        if (clean.Length > MaxStoredLength(maxTextLength))
            throw new MeetingDomainException("حجم قالب‌بندی متن بیش از حد مجاز است؛ بخشی از قالب‌بندی را ساده کنید.");

        return clean;
    }

    public static string ToPlainText(string? value)
    {
        var input = value ?? string.Empty;
        if (!RichMarkupPattern.IsMatch(input)) return input.Trim();
        var clean = CreateSanitizer().Sanitize(input);
        return PlainText(clean);
    }

    public static string ToDisplayHtml(string? value, int maxTextLength = int.MaxValue)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        if (!RichMarkupPattern.IsMatch(value))
            return "<p>" + WebUtility.HtmlEncode(value).Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\n', ' ')
                .Replace("  ", " ", StringComparison.Ordinal) + "</p>";
        return Sanitize(value, maxTextLength);
    }

    private static int TextLength(string html) => PlainText(html).Length;

    private static string PlainText(string html)
    {
        var withBreaks = BreakPattern().Replace(html, "\n");
        return WebUtility.HtmlDecode(TagPattern().Replace(withBreaks, string.Empty))
            .Replace('\u00a0', ' ')
            .Trim();
    }

    private static HtmlSanitizer CreateSanitizer()
    {
        var sanitizer = new HtmlSanitizer();
        sanitizer.AllowedTags.Clear();
        foreach (var tag in AllowedElements) sanitizer.AllowedTags.Add(tag);
        sanitizer.AllowedAttributes.Clear();
        sanitizer.AllowedCssProperties.Clear();
        sanitizer.AllowedSchemes.Clear();
        return sanitizer;
    }

    [GeneratedRegex("<[^>]*>", RegexOptions.CultureInvariant)]
    private static partial Regex TagPattern();

    [GeneratedRegex("</(p|li|blockquote|h[2-4])\\s*>|<br\\s*/?>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex BreakPattern();
}
