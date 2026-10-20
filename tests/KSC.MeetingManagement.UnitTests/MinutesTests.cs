using KSC.MeetingManagement.Domain.Meetings;

namespace KSC.MeetingManagement.UnitTests;

public class MinutesTests
{
    private static readonly DateTimeOffset Now = new(2026, 5, 1, 10, 0, 0, TimeSpan.Zero);
    private static readonly Guid Author = Guid.NewGuid();
    private static readonly Guid Approver = Guid.NewGuid();

    private static Minutes New(string content = "متن اولیه") =>
        new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), content, Author, Now);

    private static Minutes Approved()
    {
        var m = New();
        m.Submit(Author, Now);
        m.Approve(Approver, "ok", Now.AddHours(1));
        return m;
    }

    [Fact]
    public void New_minutes_start_as_a_draft_with_version_one()
    {
        var m = New("  متن  ");

        Assert.Equal(MinutesStatus.Draft, m.Status);
        Assert.Equal(1, m.CurrentVersionNumber);
        Assert.Equal("متن", m.CurrentVersion.Content);
        Assert.Null(m.ApprovedVersionNumber);
        Assert.Empty(m.Transitions);
    }

    [Fact]
    public void Each_changed_save_creates_a_new_immutable_version_and_identical_text_does_not()
    {
        var m = New("v1");

        Assert.True(m.SaveDraft("v2", Author, Now.AddMinutes(1)));
        Assert.False(m.SaveDraft("v2", Author, Now.AddMinutes(2)));
        Assert.True(m.SaveDraft("v3", Author, Now.AddMinutes(3)));

        Assert.Equal(["v1", "v2", "v3"], m.Versions.OrderBy(v => v.Number).Select(v => v.Content));
        Assert.Equal(3, m.CurrentVersionNumber);
    }

    [Fact]
    public void Approval_workflow_records_who_did_what_and_when()
    {
        var m = New();
        m.Submit(Author, Now.AddMinutes(5));
        m.Approve(Approver, "تأیید شد", Now.AddMinutes(10));

        Assert.Equal(MinutesStatus.Approved, m.Status);
        Assert.Equal(1, m.ApprovedVersionNumber);
        Assert.Collection(
            m.Transitions,
            t => Assert.Equal((MinutesStatus.Draft, MinutesStatus.InReview, Author), (t.From, t.To, t.ActorUserId)),
            t =>
            {
                Assert.Equal((MinutesStatus.InReview, MinutesStatus.Approved, Approver), (t.From, t.To, t.ActorUserId));
                Assert.Equal("تأیید شد", t.Comment);
                Assert.Equal(Now.AddMinutes(10), t.AtUtc);
                Assert.Equal(1, t.VersionNumber);
            });
    }

    [Fact]
    public void Text_cannot_be_edited_while_under_review_or_after_approval()
    {
        var review = New();
        review.Submit(Author, Now);
        var approved = Approved();

        Assert.Throws<MeetingDomainException>(() => review.SaveDraft("x", Author, Now));
        var ex = Assert.Throws<MeetingDomainException>(() => approved.SaveDraft("x", Author, Now));
        Assert.Contains("reopen", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("متن اولیه", approved.CurrentVersion.Content);
        Assert.Equal(1, approved.CurrentVersionNumber);
    }

    [Fact]
    public void Rejection_requires_a_comment_and_editing_afterwards_returns_to_draft_with_a_new_version()
    {
        var m = New();
        m.Submit(Author, Now);

        Assert.Throws<MeetingDomainException>(() => m.Reject(Approver, " ", Now));
        m.Reject(Approver, "ناقص است", Now.AddMinutes(1));
        Assert.Equal(MinutesStatus.Rejected, m.Status);
        Assert.Null(m.ApprovedVersionNumber);

        m.SaveDraft("اصلاح‌شده", Author, Now.AddMinutes(2));

        Assert.Equal(MinutesStatus.Draft, m.Status);
        Assert.Equal(2, m.CurrentVersionNumber);
        Assert.Equal(MinutesStatus.Draft, m.Transitions[^1].To);
    }

    [Fact]
    public void Reopen_is_the_only_way_to_change_approved_minutes_and_needs_a_reason()
    {
        var m = Approved();

        Assert.Throws<MeetingDomainException>(() => m.Reopen(Approver, "", Now));
        m.Reopen(Approver, "خطای تایپی", Now.AddDays(1));

        Assert.Equal(MinutesStatus.Draft, m.Status);
        Assert.Null(m.ApprovedVersionNumber);
        Assert.Equal("خطای تایپی", m.Transitions[^1].Comment);
        Assert.Equal(Approver, m.Transitions[^1].ActorUserId);
        Assert.True(m.SaveDraft("نسخهٔ جدید", Author, Now.AddDays(1)));
        Assert.Equal(2, m.CurrentVersionNumber);
        Assert.Equal("متن اولیه", m.Versions.Single(v => v.Number == 1).Content);
    }

    [Theory]
    [InlineData("submit-twice")]
    [InlineData("approve-draft")]
    [InlineData("reject-draft")]
    [InlineData("reopen-draft")]
    [InlineData("approve-twice")]
    public void Invalid_transitions_are_rejected_and_leave_the_status_unchanged(string scenario)
    {
        var m = New();
        Action act = scenario switch
        {
            "submit-twice" => () =>
            {
                m.Submit(Author, Now);
                m.Submit(Author, Now);
            },
            "approve-draft" => () => m.Approve(Approver, null, Now),
            "reject-draft" => () => m.Reject(Approver, "x", Now),
            "reopen-draft" => () => m.Reopen(Approver, "x", Now),
            _ => () =>
            {
                m.Submit(Author, Now);
                m.Approve(Approver, null, Now);
                m.Approve(Approver, null, Now);
            },
        };

        Assert.Throws<MeetingDomainException>(act);
        Assert.NotEqual(MinutesStatus.Rejected, m.Status);
    }

    [Fact]
    public void Lock_version_increases_with_every_change()
    {
        var m = New();
        var start = m.LockVersion;

        m.Submit(Author, Now);
        m.Approve(Approver, null, Now);

        Assert.True(m.LockVersion > start);
    }

    [Fact]
    public void Content_and_comment_limits_are_enforced()
    {
        Assert.Throws<MeetingDomainException>(() => New(" "));
        Assert.Throws<MeetingDomainException>(() => New(new string('x', Minutes.MaxContentLength + 1)));
        var m = New();
        m.Submit(Author, Now);
        Assert.Throws<MeetingDomainException>(() => m.Reject(Approver, new string('c', Minutes.MaxCommentLength + 1), Now));
        Assert.Throws<MeetingDomainException>(() => m.Approve(Guid.Empty, null, Now));
    }
}
