using KSC.MeetingManagement.Application.Identity;
using KSC.MeetingManagement.Application.Participants;
using KSC.MeetingManagement.Application.Tenants;
using KSC.MeetingManagement.Domain.Meetings;

namespace KSC.MeetingManagement.Application.Meetings;

public sealed class MeetingService(
    IMeetingRepository repository,
    IParticipantRepository participants,
    Committees.ICommitteeRepository committees,
    Notifications.NotificationService notifications,
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
        await EnsureCommitteeAsync(command.CommitteeId, cancellationToken);
        var meeting = new Meeting(id, tenant.RequiredTenantId, command.Title, command.Mode, user.RequiredUserId);
        meeting.SetCommittee(command.CommitteeId);
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

        await EnsureCommitteeAsync(command.CommitteeId, cancellationToken);
        return await MutateAsync(id, command.ExpectedVersion, meeting =>
        {
            meeting.UpdateDetails(command.Title, command.Description, command.Mode);
            meeting.SetCommittee(command.CommitteeId);
            if (command.Start is { } start && command.End is { } end)
            {
                meeting.Schedule(start, end);
            }
        }, cancellationToken, command.AllowConflicts ? null : EnsureNoConflictsForAttendeesAsync, NotifyChangedAsync);
    }

    /// <summary>Changes only the time (used by drag and drop). Conflicts are checked unless explicitly allowed.</summary>
    public Task<MeetingDto?> RescheduleAsync(
        Guid id, long expectedVersion, DateTimeOffset start, DateTimeOffset end, bool allowConflicts, CancellationToken cancellationToken) =>
        MutateAsync(id, expectedVersion, m => m.Schedule(start, end), cancellationToken, allowConflicts ? null : EnsureNoConflictsForAttendeesAsync, NotifyChangedAsync);

    /// <summary>Marks the meeting as started now (Scheduled to InProgress) and records the actual start time.</summary>
    public Task<MeetingDto?> StartAsync(Guid id, long expectedVersion, CancellationToken cancellationToken) =>
        MutateAsync(id, expectedVersion, m => m.Start(clock.GetUtcNow()), cancellationToken);

    /// <summary>
    /// Ends the meeting now and records the actual end time. A meeting that was never started is started first, so it can be closed
    /// in one step. After this the room no longer admits anyone.
    /// </summary>
    public Task<MeetingDto?> EndAsync(Guid id, long expectedVersion, CancellationToken cancellationToken) =>
        MutateAsync(
            id,
            expectedVersion,
            m =>
            {
                var now = clock.GetUtcNow();
                if (m.Status == MeetingStatus.Scheduled)
                {
                    m.Start(now);
                }

                m.Complete(now);
            },
            cancellationToken);

    public Task<MeetingDto?> CancelAsync(Guid id, long expectedVersion, string reason, CancellationToken cancellationToken) =>
        MutateAsync(id, expectedVersion, m => m.Cancel(reason), cancellationToken, afterChange: NotifyCancelledAsync);

    public Task<MeetingDto?> PostponeAsync(Guid id, long expectedVersion, CancellationToken cancellationToken) =>
        MutateAsync(id, expectedVersion, m => m.Postpone(), cancellationToken, afterChange: NotifyChangedAsync);

    private async Task<MeetingDto?> MutateAsync(
        Guid id,
        long expectedVersion,
        Action<Meeting> change,
        CancellationToken cancellationToken,
        Func<Meeting, CancellationToken, Task>? validate = null,
        Func<Meeting, CancellationToken, Task>? afterChange = null)
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

        // Queued in the same unit of work as the change, so the notification is saved or lost together with it.
        if (afterChange is not null)
        {
            await afterChange(meeting, cancellationToken);
        }

        await repository.SaveChangesAsync(cancellationToken);
        return MeetingDto.From(meeting);
    }

    // Participants of a draft are not told about changes; the meeting is not announced yet.
    private Task NotifyChangedAsync(Meeting meeting, CancellationToken cancellationToken) =>
        meeting.Status == MeetingStatus.Draft ? Task.CompletedTask : notifications.MeetingChangedAsync(meeting, user.RequiredUserId, cancellationToken);

    private Task NotifyCancelledAsync(Meeting meeting, CancellationToken cancellationToken) =>
        notifications.MeetingCancelledAsync(meeting, user.RequiredUserId, cancellationToken);

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

    private async Task EnsureCommitteeAsync(Guid? committeeId, CancellationToken cancellationToken)
    {
        if (committeeId is { } id && await committees.GetAsync(id, cancellationToken) is not { IsActive: true })
        {
            throw new MeetingDomainException("The committee does not exist or is not active.");
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
