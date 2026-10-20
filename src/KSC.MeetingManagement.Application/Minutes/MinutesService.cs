using KSC.MeetingManagement.Application.Identity;
using KSC.MeetingManagement.Application.Meetings;
using KSC.MeetingManagement.Application.Tenants;
using KSC.MeetingManagement.Domain.Meetings;

namespace KSC.MeetingManagement.Application.Minutes;

public sealed record MinutesTransitionDto(MinutesStatus From, MinutesStatus To, int VersionNumber, Guid ActorUserId, DateTimeOffset AtUtc, string? Comment);

public sealed record MinutesVersionSummaryDto(int Number, Guid AuthorUserId, DateTimeOffset CreatedAtUtc);

public sealed record MinutesVersionDto(int Number, string Content, Guid AuthorUserId, DateTimeOffset CreatedAtUtc);

public sealed record MinutesDto(
    Guid Id,
    Guid MeetingId,
    MinutesStatus Status,
    int CurrentVersionNumber,
    int? ApprovedVersionNumber,
    string Content,
    IReadOnlyList<MinutesTransitionDto> Transitions)
{
    public static MinutesDto From(Domain.Meetings.Minutes m)
    {
        ArgumentNullException.ThrowIfNull(m);
        return new(
            m.Id, m.MeetingId, m.Status, m.CurrentVersionNumber, m.ApprovedVersionNumber, m.CurrentVersion.Content,
            m.Transitions.OrderBy(t => t.AtUtc).Select(t => new MinutesTransitionDto(t.From, t.To, t.VersionNumber, t.ActorUserId, t.AtUtc, t.Comment)).ToList());
    }
}

/// <summary>Persistence port. Implementations must only return minutes of the current tenant and load versions and transitions.</summary>
public interface IMinutesRepository
{
    Task<Domain.Meetings.Minutes?> GetByMeetingAsync(Guid meetingId, CancellationToken cancellationToken);

    Task AddAsync(Domain.Meetings.Minutes minutes, CancellationToken cancellationToken);

    Task SaveChangesAsync(CancellationToken cancellationToken);
}

public sealed class MinutesService(
    IMinutesRepository repository,
    IMeetingRepository meetings,
    ITenantContext tenant,
    ICurrentUser user,
    TimeProvider clock)
{
    /// <summary>
    /// Returns the minutes. Unapproved text is only returned when the caller may see it; otherwise the minutes look absent.
    /// </summary>
    public async Task<MinutesDto?> GetAsync(Guid meetingId, bool includeUnapproved, CancellationToken cancellationToken) =>
        await repository.GetByMeetingAsync(meetingId, cancellationToken) is { } minutes && (includeUnapproved || minutes.Status == MinutesStatus.Approved)
            ? MinutesDto.From(minutes)
            : null;

    public async Task<IReadOnlyList<MinutesVersionSummaryDto>?> ListVersionsAsync(Guid meetingId, bool includeUnapproved, CancellationToken cancellationToken) =>
        await repository.GetByMeetingAsync(meetingId, cancellationToken) is { } minutes && (includeUnapproved || minutes.Status == MinutesStatus.Approved)
            ? minutes.Versions.OrderBy(v => v.Number).Select(v => new MinutesVersionSummaryDto(v.Number, v.AuthorUserId, v.CreatedAtUtc)).ToList()
            : null;

    public async Task<MinutesVersionDto?> GetVersionAsync(Guid meetingId, int number, bool includeUnapproved, CancellationToken cancellationToken) =>
        await repository.GetByMeetingAsync(meetingId, cancellationToken) is { } minutes && (includeUnapproved || minutes.Status == MinutesStatus.Approved)
            && minutes.Versions.FirstOrDefault(v => v.Number == number) is { } version
            ? new MinutesVersionDto(version.Number, version.Content, version.AuthorUserId, version.CreatedAtUtc)
            : null;

    /// <summary>
    /// Creates the minutes (expected version 0) or saves a new version of them. The caller states the version it edited so
    /// a newer save by someone else is never overwritten silently. Returns null when the meeting does not exist.
    /// </summary>
    public async Task<MinutesDto?> SaveAsync(Guid meetingId, string content, int expectedVersionNumber, CancellationToken cancellationToken)
    {
        if (await meetings.GetAsync(meetingId, cancellationToken) is not { } meeting)
        {
            return null;
        }

        if (meeting.Status == MeetingStatus.Cancelled)
        {
            throw new MeetingDomainException("A cancelled meeting cannot have minutes.");
        }

        var now = clock.GetUtcNow();
        var existing = await repository.GetByMeetingAsync(meetingId, cancellationToken);
        if (existing is null)
        {
            if (expectedVersionNumber != 0)
            {
                throw new MeetingConcurrencyException("The minutes do not exist yet; reload and retry.");
            }

            existing = new Domain.Meetings.Minutes(Guid.NewGuid(), tenant.RequiredTenantId, meetingId, content, user.RequiredUserId, now);
            await repository.AddAsync(existing, cancellationToken);
        }
        else
        {
            if (existing.CurrentVersionNumber != expectedVersionNumber)
            {
                throw new MeetingConcurrencyException("The minutes were changed by someone else; reload and retry.");
            }

            existing.SaveDraft(content, user.RequiredUserId, now);
        }

        await repository.SaveChangesAsync(cancellationToken);
        return MinutesDto.From(existing);
    }

    public Task<MinutesDto?> SubmitAsync(Guid meetingId, CancellationToken cancellationToken) =>
        TransitionAsync(meetingId, m => m.Submit(user.RequiredUserId, clock.GetUtcNow()), cancellationToken);

    public Task<MinutesDto?> ApproveAsync(Guid meetingId, string? comment, CancellationToken cancellationToken) =>
        TransitionAsync(meetingId, m => m.Approve(user.RequiredUserId, comment, clock.GetUtcNow()), cancellationToken);

    public Task<MinutesDto?> RejectAsync(Guid meetingId, string comment, CancellationToken cancellationToken) =>
        TransitionAsync(meetingId, m => m.Reject(user.RequiredUserId, comment, clock.GetUtcNow()), cancellationToken);

    public Task<MinutesDto?> ReopenAsync(Guid meetingId, string reason, CancellationToken cancellationToken) =>
        TransitionAsync(meetingId, m => m.Reopen(user.RequiredUserId, reason, clock.GetUtcNow()), cancellationToken);

    private async Task<MinutesDto?> TransitionAsync(Guid meetingId, Action<Domain.Meetings.Minutes> change, CancellationToken cancellationToken)
    {
        if (await repository.GetByMeetingAsync(meetingId, cancellationToken) is not { } minutes)
        {
            return null;
        }

        change(minutes);
        await repository.SaveChangesAsync(cancellationToken);
        return MinutesDto.From(minutes);
    }
}
