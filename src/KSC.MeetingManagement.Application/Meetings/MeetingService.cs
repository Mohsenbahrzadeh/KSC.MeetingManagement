using KSC.MeetingManagement.Application.Identity;
using KSC.MeetingManagement.Application.Tenants;
using KSC.MeetingManagement.Domain.Meetings;

namespace KSC.MeetingManagement.Application.Meetings;

public sealed class MeetingService(IMeetingRepository repository, ITenantContext tenant, ICurrentUser user)
{
    public const int MaxPageSize = 200;

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
        }

        await repository.AddAsync(meeting, cancellationToken);
        await repository.SaveChangesAsync(cancellationToken);
        return new CreateMeetingResult(MeetingDto.From(meeting), Created: true);
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

        var bounded = query with
        {
            Skip = Math.Max(0, query.Skip),
            Take = Math.Clamp(query.Take, 1, MaxPageSize),
        };
        var meetings = await repository.ListAsync(bounded, cancellationToken);
        return meetings.Select(MeetingDto.From).ToList();
    }
}
