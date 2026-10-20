using KSC.MeetingManagement.Domain.Meetings;

namespace KSC.MeetingManagement.UnitTests;

public class MeetingParticipantTests
{
    private static readonly DateTimeOffset Now = new(2026, 3, 1, 10, 0, 0, TimeSpan.Zero);

    private static (MeetingParticipant P, string Token) NewGuest(DateTimeOffset? expires = null) =>
        MeetingParticipant.Guest(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "Guest", "Guest@Example.com", expires ?? Now.AddDays(2), Now);

    [Fact]
    public void Valid_token_redeems_and_accepts()
    {
        var (p, token) = NewGuest();

        Assert.True(p.TryRedeem(token, Now.AddHours(1)));
        Assert.Equal(InvitationStatus.Accepted, p.Status);
    }

    [Fact]
    public void Token_is_not_stored_in_clear()
    {
        var (p, token) = NewGuest();

        Assert.NotEqual(token, p.InviteTokenHash);
        Assert.Equal("guest@example.com", p.GuestEmail);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("wrong-token")]
    public void Wrong_or_missing_token_is_rejected(string? token)
    {
        var (p, _) = NewGuest();

        Assert.False(p.TryRedeem(token, Now));
        Assert.Equal(InvitationStatus.Invited, p.Status);
    }

    [Fact]
    public void Expired_invitation_is_rejected()
    {
        var (p, token) = NewGuest(Now.AddDays(1));

        Assert.False(p.TryRedeem(token, Now.AddDays(1)));
    }

    [Fact]
    public void Revoked_invitation_is_rejected()
    {
        var (p, token) = NewGuest();
        p.Revoke();

        Assert.False(p.TryRedeem(token, Now));
        Assert.Throws<MeetingDomainException>(() => p.Respond(true));
    }

    [Fact]
    public void Guest_requires_name_valid_email_and_future_expiry()
    {
        Assert.Throws<MeetingDomainException>(() => MeetingParticipant.Guest(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), " ", "a@b.c", Now.AddDays(1), Now));
        Assert.Throws<MeetingDomainException>(() => MeetingParticipant.Guest(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "G", "no-at", Now.AddDays(1), Now));
        Assert.Throws<MeetingDomainException>(() => MeetingParticipant.Guest(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "G", "a@b.c", Now, Now));
    }

    [Fact]
    public void Internal_participant_can_accept_and_decline()
    {
        var p = MeetingParticipant.Internal(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());

        p.Respond(true);
        Assert.Equal(InvitationStatus.Accepted, p.Status);
        p.Respond(false);
        Assert.Equal(InvitationStatus.Declined, p.Status);
    }

    [Fact]
    public void Internal_participant_cannot_redeem_as_guest()
    {
        var p = MeetingParticipant.Internal(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());

        Assert.False(p.TryRedeem("anything", Now));
    }
}
