using KSC.MeetingManagement.Domain.Meetings;

namespace KSC.MeetingManagement.Application.Meetings;

public sealed record CreateMeetingCommand(
    Guid? Id,
    string Title,
    string? Description,
    MeetingMode Mode,
    DateTimeOffset? Start,
    DateTimeOffset? End);

public sealed record MeetingQuery(
    DateTimeOffset? From,
    DateTimeOffset? To,
    MeetingStatus? Status,
    int Skip,
    int Take);

public sealed record MeetingDto(
    Guid Id,
    string Title,
    string? Description,
    MeetingMode Mode,
    MeetingStatus Status,
    DateTimeOffset? StartUtc,
    DateTimeOffset? EndUtc,
    Guid OrganizerId,
    long Version)
{
    public static MeetingDto From(Meeting m)
    {
        ArgumentNullException.ThrowIfNull(m);
        return new(m.Id, m.Title, m.Description, m.Mode, m.Status, m.StartUtc, m.EndUtc, m.OrganizerId, m.Version);
    }
}

public sealed record CreateMeetingResult(MeetingDto Meeting, bool Created);

/// <summary>Persistence port. Implementations must only return meetings of the current tenant.</summary>
public interface IMeetingRepository
{
    Task<Meeting?> GetAsync(Guid id, CancellationToken cancellationToken);

    Task<IReadOnlyList<Meeting>> ListAsync(MeetingQuery query, CancellationToken cancellationToken);

    Task AddAsync(Meeting meeting, CancellationToken cancellationToken);

    Task SaveChangesAsync(CancellationToken cancellationToken);
}
