using KSC.MeetingManagement.Domain.Meetings;

namespace KSC.MeetingManagement.Application.Meetings;

public sealed record CreateMeetingCommand(
    Guid? Id,
    string Title,
    string? Description,
    MeetingMode Mode,
    DateTimeOffset? Start,
    DateTimeOffset? End,
    bool AllowConflicts = false,
    Guid? CommitteeId = null);

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
    long Version,
    string? Number = null,
    Guid? CommitteeId = null)
{
    public static MeetingDto From(Meeting m)
    {
        ArgumentNullException.ThrowIfNull(m);
        return new(m.Id, m.Title, m.Description, m.Mode, m.Status, m.StartUtc, m.EndUtc, m.OrganizerId, m.Version, m.Number, m.CommitteeId);
    }
}

public sealed record UpdateMeetingCommand(
    long ExpectedVersion,
    string Title,
    string? Description,
    MeetingMode Mode,
    DateTimeOffset? Start,
    DateTimeOffset? End,
    bool AllowConflicts = false,
    Guid? CommitteeId = null);

/// <summary>An existing meeting that overlaps a requested time for a person who would attend both.</summary>
public sealed record MeetingConflict(Guid MeetingId, string? Number, string Title, DateTimeOffset StartUtc, DateTimeOffset EndUtc, Guid UserId);

/// <summary>Thrown when the requested time overlaps another active meeting of the organizer or a participant.</summary>
public sealed class MeetingTimeConflictException(IReadOnlyList<MeetingConflict> conflicts)
    : InvalidOperationException("The requested time overlaps another meeting.")
{
    public IReadOnlyList<MeetingConflict> Conflicts { get; } = conflicts;
}

/// <summary>Thrown when the meeting was changed by someone else since the caller last read it.</summary>
public sealed class MeetingConcurrencyException : InvalidOperationException
{
    public MeetingConcurrencyException(string message)
        : base(message)
    {
    }

    public MeetingConcurrencyException(string message, Exception inner)
        : base(message, inner)
    {
    }
}

public sealed record CreateMeetingResult(MeetingDto Meeting, bool Created);

/// <summary>Persistence port. Implementations must only return meetings of the current tenant.</summary>
public interface IMeetingRepository
{
    Task<Meeting?> GetAsync(Guid id, CancellationToken cancellationToken);

    Task<IReadOnlyList<Meeting>> ListAsync(MeetingQuery query, CancellationToken cancellationToken);

    Task AddAsync(Meeting meeting, CancellationToken cancellationToken);

    /// <summary>
    /// Active (scheduled or in progress) meetings of the current tenant that overlap [start, end) and where one of the
    /// users is the organizer or a non-revoked internal participant. Touching ends do not overlap.
    /// </summary>
    Task<IReadOnlyList<MeetingConflict>> FindConflictsAsync(
        IReadOnlyCollection<Guid> userIds, DateTimeOffset startUtc, DateTimeOffset endUtc, Guid? excludeMeetingId, CancellationToken cancellationToken);

    /// <summary>
    /// Assigns the next number of the given year in the current tenant and saves the meeting atomically.
    /// Concurrent callers never receive the same number.
    /// </summary>
    Task AddNumberedAsync(Meeting meeting, int year, CancellationToken cancellationToken);

    Task SaveChangesAsync(CancellationToken cancellationToken);
}
