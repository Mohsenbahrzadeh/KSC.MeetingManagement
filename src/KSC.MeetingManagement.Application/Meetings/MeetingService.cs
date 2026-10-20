using KSC.MeetingManagement.Application.Identity;
using KSC.MeetingManagement.Application.Participants;
using KSC.MeetingManagement.Application.Tenants;
using KSC.MeetingManagement.Domain.Meetings;

namespace KSC.MeetingManagement.Application.Meetings;

public sealed class MeetingService(
    IMeetingRepository repository,
    IParticipantRepository participants,
    ITenantContext tenant,
    ICurrentUser user,
    TimeProvider clock)
{
    public const int MaxPageSize = 200;

    /// <summary>Longest range one list request may cover (a six-week month grid plus slack).</summary>
    public static readonly TimeSpan MaxRange = TimeSpan.FromDays(62);

    /// <summary>
    /// Creates a meeting. A repeated request with the same client-supplied id returns the existing meeting
    /// instead of creating a duplicate.
    /// </summary>
    public async Task<CreateMeetingResult> CreateAsync(CreateMeetingCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (command.Id is { } requested && requested != Guid.Empty
            && await repository.GetAsync(requested, cancellationToken) is { } existing)
        {
            return new CreateMeetingResult(MeetingDto.From(existing), Created: false);
        }

        if ((command.Start is null) != (command.End is null))
        {
            throw new MeetingDomainException("Both start and end are required together.");
        }

        var id = command.Id is { } given && given != Guid.Empty ? given : Guid.NewGuid();
        var meeting = new Meeting(id, tenant.RequiredTenantId, command.Title, command.Mode, user.RequiredUserId);
        if (!string.IsNullOrWhiteSpace(command.Description))
        {
            meeting.UpdateDetails(command.Title, command.Description, command.Mode);
        }

        if (command.Start is { } start && command.End is { } end)
        {
            meeting.Schedule(start, end);
            if (!command.AllowConflicts)
            {
                await EnsureNoConflictsAsync(meeting, [meeting.OrganizerId], cancellationToken);
            }
        }

        await repository.AddNumberedAsync(meeting, clock.GetUtcNow().Year, cancellationToken);
        return new CreateMeetingResult(MeetingDto.From(meeting), Created: true);
    }

    /// <summary>Updates details and time. Returns null when the meeting does not exist in the current tenant.</summary>
    public async Task<MeetingDto?> UpdateAsync(Guid id, UpdateMeetingCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        if ((command.Start is null) != (command.End is null))
        {
            throw new MeetingDomainException("Both start and end are required together.");
        }

        return await MutateAsync(id, command.ExpectedVersion, meeting =>
        {
            meeting.UpdateDetails(command.Title, command.Description, command.Mode);
            if (command.Start is { } start && command.End is { } end)
            {
                meeting.Schedule(start, end);
            }
        }, cancellationToken, command.AllowConflicts ? null : EnsureNoConflictsForAttendeesAsync);
    }

    public Task<MeetingDto?> CancelAsync(Guid id, long expectedVersion, string reason, CancellationToken cancellationToken) =>
        MutateAsync(id, expectedVersion, m => m.Cancel(reason), cancellationToken);

    public Task<MeetingDto?> PostponeAsync(Guid id, long expectedVersion, CancellationToken cancellationToken) =>
        MutateAsync(id, expectedVersion, m => m.Postpone(), cancellationToken);

    private async Task<MeetingDto?> MutateAsync(
        Guid id,
        long expectedVersion,
        Action<Meeting> change,
        CancellationToken cancellationToken,
        Func<Meeting, CancellationToken, Task>? validate = null)
    {
        if (await repository.GetAsync(id, cancellationToken) is not { } meeting)
        {
            return null;
        }

        if (meeting.Version != expectedVersion)
        {
            throw new MeetingConcurrencyException("The meeting was changed by someone else; reload and retry.");
        }

        change(meeting);
        if (validate is not null)
        {
            await validate(meeting, cancellationToken);
        }

        await repository.SaveChangesAsync(cancellationToken);
        return MeetingDto.From(meeting);
    }

    private async Task EnsureNoConflictsForAttendeesAsync(Meeting meeting, CancellationToken cancellationToken)
    {
        if (meeting.StartUtc is null)
        {
            return;
        }

        var attendees = (await participants.ListAsync(meeting.Id, cancellationToken))
            .Where(p => p.UserId is not null && p.Status is InvitationStatus.Invited or InvitationStatus.Accepted)
            .Select(p => p.UserId!.Value)
            .Append(meeting.OrganizerId)
            .Distinct()
            .ToList();
        await EnsureNoConflictsAsync(meeting, attendees, cancellationToken);
    }

    private async Task EnsureNoConflictsAsync(Meeting meeting, IReadOnlyCollection<Guid> userIds, CancellationToken cancellationToken)
    {
        if (meeting.StartUtc is not { } start || meeting.EndUtc is not { } end)
        {
            return;
        }

        var conflicts = await repository.FindConflictsAsync(userIds, start, end, meeting.Id, cancellationToken);
        if (conflicts.Count > 0)
        {
            throw new MeetingTimeConflictException(conflicts);
        }
    }

    public async Task<MeetingDto?> GetAsync(Guid id, CancellationToken cancellationToken) =>
        await repository.GetAsync(id, cancellationToken) is { } meeting ? MeetingDto.From(meeting) : null;

    public async Task<IReadOnlyList<MeetingDto>> ListAsync(MeetingQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        if (query.From is { } from && query.To is { } to && to <= from)
        {
            throw new MeetingDomainException("Range end must be after its start.");
        }

        if (query.From is { } rangeStart && query.To is { } rangeEnd && rangeEnd - rangeStart > MaxRange)
        {
            throw new MeetingDomainException($"The requested range cannot be longer than {MaxRange.TotalDays:0} days.");
        }

        var bounded = query with
        {
            Skip = Math.Max(0, query.Skip),
            Take = Math.Clamp(query.Take, 1, MaxPageSize),
        };
        var meetings = await repository.ListAsync(bounded, cancellationToken);
        return meetings.Select(MeetingDto.From).ToList();
    }
}
