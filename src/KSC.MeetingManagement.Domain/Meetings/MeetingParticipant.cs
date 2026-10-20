using System.Security.Cryptography;
using System.Text;

namespace KSC.MeetingManagement.Domain.Meetings;

public enum ParticipantKind
{
    Internal = 1,
    Guest = 2,
}

public enum InvitationStatus
{
    Invited = 1,
    Accepted = 2,
    Declined = 3,
    Revoked = 4,
}

/// <summary>
/// A person invited to a meeting: an internal tenant user or an external guest.
/// A guest is only admitted by presenting the invitation token before it expires and
/// while the invitation has not been revoked. Only a hash of the token is stored.
/// </summary>
public sealed class MeetingParticipant
{
    private MeetingParticipant(Guid id, Guid tenantId, Guid meetingId, ParticipantKind kind)
    {
        if (id == Guid.Empty || tenantId == Guid.Empty || meetingId == Guid.Empty)
        {
            throw new MeetingDomainException("Participant, tenant and meeting ids are required.");
        }

        Id = id;
        TenantId = tenantId;
        MeetingId = meetingId;
        Kind = kind;
        Status = InvitationStatus.Invited;
    }

    public Guid Id { get; }
    public Guid TenantId { get; }
    public Guid MeetingId { get; }
    public ParticipantKind Kind { get; }
    public Guid? UserId { get; private set; }
    public string? GuestName { get; private set; }
    public string? GuestEmail { get; private set; }
    public InvitationStatus Status { get; private set; }
    public string? InviteTokenHash { get; private set; }
    public DateTimeOffset? InviteExpiresUtc { get; private set; }

    public static MeetingParticipant Internal(Guid id, Guid tenantId, Guid meetingId, Guid userId)
    {
        if (userId == Guid.Empty)
        {
            throw new MeetingDomainException("User id is required.");
        }

        return new MeetingParticipant(id, tenantId, meetingId, ParticipantKind.Internal) { UserId = userId };
    }

    /// <summary>Creates a guest invitation and returns the raw token, which is shown once and never stored.</summary>
    public static (MeetingParticipant Participant, string Token) Guest(
        Guid id, Guid tenantId, Guid meetingId, string name, string email, DateTimeOffset expiresUtc, DateTimeOffset nowUtc)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new MeetingDomainException("Guest name is required.");
        }

        if (string.IsNullOrWhiteSpace(email) || !email.Contains('@', StringComparison.Ordinal))
        {
            throw new MeetingDomainException("A valid guest email is required.");
        }

        if (expiresUtc <= nowUtc)
        {
            throw new MeetingDomainException("Invitation expiry must be in the future.");
        }

        var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .Replace('+', '-')
            .Replace('/', '_')
            .TrimEnd('=');
        var participant = new MeetingParticipant(id, tenantId, meetingId, ParticipantKind.Guest)
        {
            GuestName = name.Trim(),
            GuestEmail = email.Trim().ToLowerInvariant(),
            InviteTokenHash = Hash(token),
            InviteExpiresUtc = expiresUtc.ToUniversalTime(),
        };
        return (participant, token);
    }

    public bool IsExpired(DateTimeOffset nowUtc) => InviteExpiresUtc is { } expires && expires <= nowUtc;

    public void Revoke()
    {
        Status = InvitationStatus.Revoked;
        InviteTokenHash = null;
    }

    /// <summary>Internal participant answers their own invitation.</summary>
    public void Respond(bool accept)
    {
        if (Status == InvitationStatus.Revoked)
        {
            throw new MeetingDomainException("The invitation has been revoked.");
        }

        Status = accept ? InvitationStatus.Accepted : InvitationStatus.Declined;
    }

    /// <summary>Checks a guest token. Returns false for wrong, expired or revoked invitations; never throws on bad input.</summary>
    public bool TryRedeem(string? token, DateTimeOffset nowUtc)
    {
        if (Kind != ParticipantKind.Guest || Status == InvitationStatus.Revoked || InviteTokenHash is null
            || string.IsNullOrEmpty(token) || IsExpired(nowUtc))
        {
            return false;
        }

        var expected = Encoding.ASCII.GetBytes(InviteTokenHash);
        var actual = Encoding.ASCII.GetBytes(Hash(token));
        if (!CryptographicOperations.FixedTimeEquals(expected, actual))
        {
            return false;
        }

        Status = InvitationStatus.Accepted;
        return true;
    }

    private static string Hash(string token) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
}
