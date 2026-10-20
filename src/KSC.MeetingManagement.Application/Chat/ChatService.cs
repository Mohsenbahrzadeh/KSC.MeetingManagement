using KSC.MeetingManagement.Application.Identity;
using KSC.MeetingManagement.Application.Media;
using KSC.MeetingManagement.Application.Meetings;
using KSC.MeetingManagement.Application.Participants;
using KSC.MeetingManagement.Application.Tenants;
using KSC.MeetingManagement.Domain.Meetings;

namespace KSC.MeetingManagement.Application.Chat;

/// <summary>A message as clients see it. A deleted message has no text.</summary>
public sealed record ChatMessageDto(Guid Id, long Sequence, Guid SenderUserId, string? Text, DateTimeOffset SentAtUtc, ChatMessageStatus Status)
{
    public static ChatMessageDto From(ChatMessage m)
    {
        ArgumentNullException.ThrowIfNull(m);
        return new(m.Id, m.Sequence, m.SenderUserId, m.Status == ChatMessageStatus.Visible ? m.Text : null, m.SentAtUtc, m.Status);
    }
}

/// <summary>The caller is not allowed in this meeting's room chat.</summary>
public sealed class ChatForbiddenException() : InvalidOperationException("Not allowed in this chat.");

/// <summary>Persistence port. Implementations must only return messages of the current tenant.</summary>
public interface IChatRepository
{
    Task<IReadOnlyList<ChatMessage>> ListAsync(Guid meetingId, long afterSequence, int take, CancellationToken cancellationToken);

    Task<ChatMessage?> GetAsync(Guid meetingId, Guid messageId, CancellationToken cancellationToken);

    /// <summary>Assigns the next sequence number of the meeting and saves; concurrent senders never share a number.</summary>
    Task AddNumberedAsync(ChatMessage message, CancellationToken cancellationToken);

    Task SaveChangesAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Room chat for members. Only the organizer and invited members (not removed from the room) can read or write; writing is possible only
/// while the room is open. Guests have no API session and cannot chat (their media token carries no data-channel permission).
/// </summary>
public sealed class ChatService(
    IChatRepository messages,
    IMeetingRepository meetings,
    IParticipantRepository participants,
    IRoomEntryRepository entries,
    MediaOptions media,
    ITenantContext tenant,
    ICurrentUser user,
    TimeProvider clock)
{
    public const int MaxPageSize = 200;

    /// <summary>Messages after the given sequence number, oldest first. Null when the meeting does not exist.</summary>
    public async Task<IReadOnlyList<ChatMessageDto>?> ListAsync(Guid meetingId, long afterSequence, int take, CancellationToken cancellationToken)
    {
        if (await meetings.GetAsync(meetingId, cancellationToken) is not { } meeting)
        {
            return null;
        }

        await EnsureMemberAsync(meeting, cancellationToken);
        var page = await messages.ListAsync(meetingId, Math.Max(0, afterSequence), Math.Clamp(take, 1, MaxPageSize), cancellationToken);
        return page.Select(ChatMessageDto.From).ToList();
    }

    public async Task<ChatMessageDto?> SendAsync(Guid meetingId, string text, CancellationToken cancellationToken)
    {
        if (await meetings.GetAsync(meetingId, cancellationToken) is not { } meeting)
        {
            return null;
        }

        await EnsureMemberAsync(meeting, cancellationToken);
        EnsureRoomOpen(meeting);

        var message = new ChatMessage(Guid.NewGuid(), tenant.RequiredTenantId, meetingId, 1, user.RequiredUserId, text, clock.GetUtcNow());
        await messages.AddNumberedAsync(message, cancellationToken);
        return ChatMessageDto.From(message);
    }

    /// <summary>The sender may delete their own message; a moderator (<paramref name="canModerate"/>) may delete anyone's.</summary>
    public async Task<ChatMessageDto?> DeleteAsync(Guid meetingId, Guid messageId, bool canModerate, CancellationToken cancellationToken)
    {
        if (await meetings.GetAsync(meetingId, cancellationToken) is not { } meeting)
        {
            return null;
        }

        await EnsureMemberAsync(meeting, cancellationToken);
        if (await messages.GetAsync(meetingId, messageId, cancellationToken) is not { } message)
        {
            return null;
        }

        if (!canModerate && message.SenderUserId != user.RequiredUserId)
        {
            throw new ChatForbiddenException();
        }

        message.Delete(user.RequiredUserId, clock.GetUtcNow());
        await messages.SaveChangesAsync(cancellationToken);
        return ChatMessageDto.From(message);
    }

    private async Task EnsureMemberAsync(Meeting meeting, CancellationToken cancellationToken)
    {
        var userId = user.RequiredUserId;
        var allowed = meeting.OrganizerId == userId
            || (await participants.ListAsync(meeting.Id, cancellationToken)).Any(p => p.UserId == userId && p.Status is InvitationStatus.Invited or InvitationStatus.Accepted);
        if (!allowed || (await entries.FindAsync(meeting.Id, $"u-{userId:N}", cancellationToken))?.Status is RoomEntryStatus.Removed or RoomEntryStatus.Denied)
        {
            throw new ChatForbiddenException();
        }
    }

    private void EnsureRoomOpen(Meeting meeting)
    {
        if (meeting.Mode == MeetingMode.InPerson)
        {
            throw new MeetingDomainException("An in-person meeting has no room chat.");
        }

        var now = clock.GetUtcNow();
        if (meeting.Status is not (MeetingStatus.Scheduled or MeetingStatus.InProgress)
            || meeting.StartUtc is not { } start || meeting.EndUtc is not { } end
            || now < start - TimeSpan.FromMinutes(media.JoinOpensMinutesBeforeStart)
            || now > end + TimeSpan.FromMinutes(media.JoinClosesMinutesAfterEnd))
        {
            throw new MeetingDomainException("The room is not open, so messages cannot be sent now.");
        }
    }
}
