using KSC.MeetingManagement.Domain.Meetings;

namespace KSC.MeetingManagement.UnitTests;

public sealed class RichTextContentTests
{
    [Fact]
    public void Sanitizes_to_semantic_html_and_keeps_word_friendly_markup()
    {
        var html = RichTextContent.Sanitize("<h3>عنوان</h3><p><strong>متن</strong><script>alert(1)</script></p><ul><li>مورد</li></ul>", 100);

        Assert.Contains("<h3>عنوان</h3>", html, StringComparison.Ordinal);
        Assert.Contains("<strong>متن</strong>", html, StringComparison.Ordinal);
        Assert.DoesNotContain("script", html, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("عنوان\nمتن\nمورد", RichTextContent.ToPlainText(html));
    }

    [Fact]
    public void Preserves_plain_text_and_encodes_it_for_safe_display()
    {
        var plain = RichTextContent.Sanitize("متن ساده با <script> به‌عنوان واژه", 100);

        Assert.Equal("متن ساده با <script> به‌عنوان واژه", plain);
        Assert.Contains("&lt;script&gt;", RichTextContent.ToDisplayHtml(plain), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Counts_visible_text_for_length_limits()
    {
        Assert.Throws<MeetingDomainException>(() => RichTextContent.Sanitize("<p>متن</p>", 2));
        Assert.Throws<MeetingDomainException>(() => RichTextContent.Sanitize("متن ساده", 2));
        Assert.Equal("متن", RichTextContent.ToPlainText("<p><strong>متن</strong></p>"));
    }

    [Fact]
    public void Rejects_documents_with_no_visible_text()
    {
        Assert.Throws<MeetingDomainException>(() => new Minutes(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "<p><br></p>", Guid.NewGuid(), DateTimeOffset.UtcNow));
        Assert.Throws<MeetingDomainException>(() => new Resolution(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 1, "<p><br></p>", null));
    }

    [Fact]
    public void Bounds_storage_markup_without_changing_visible_text_limit()
    {
        Assert.Equal(260096, RichTextContent.MaxStoredLength(4000));
        Assert.Equal(4000, RichTextContent.ToPlainText(RichTextContent.Sanitize($"<p>{new string('x', 4000)}</p>", 4000)).Length);
        Assert.Throws<MeetingDomainException>(() => RichTextContent.Sanitize($"<p>{new string('x', 4001)}</p>", 4000));
        Assert.Equal(4000, RichTextContent.ToPlainText(RichTextContent.Sanitize($"<p>{new string('&', 4000)}</p>", 4000)).Length);
    }
}
