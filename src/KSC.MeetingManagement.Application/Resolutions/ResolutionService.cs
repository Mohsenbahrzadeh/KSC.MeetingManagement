using KSC.MeetingManagement.Application.Agenda;
using KSC.MeetingManagement.Application.Identity;
using KSC.MeetingManagement.Application.Meetings;
using KSC.MeetingManagement.Application.Tenants;
using KSC.MeetingManagement.Domain.Meetings;

namespace KSC.MeetingManagement.Application.Resolutions;

public sealed record ResolutionDto(
    Guid Id,
    Guid MeetingId,
    int Number,
    string Text,
    Guid? AgendaItemId,
    ResolutionStatus Status,
    DateTimeOffset? DecidedAtUtc,
    Guid? DecidedByUserId,
    string? RevocationReason,
    long LockVersion)
{
    public static ResolutionDto From(Resolution r)
    {
        ArgumentNullException.ThrowIfNull(r);
        return new(r.Id, r.MeetingId, r.Number, r.Text, r.AgendaItemId, r.Status, r.DecidedAtUtc, r.DecidedByUserId, r.RevocationReason, r.LockVersion);
    }
}

/// <summary>Persistence port. Implementations must only return resolutions of the current tenant.</summary>
public interface IResolutionRepository
{
    Task<Resolution?> GetAsync(Guid meetingId, Guid resolutionId, CancellationToken cancellationToken);

    Task<IReadOnlyList<Resolution>> ListAsync(Guid meetingId, CancellationToken cancellationToken);

    Task<bool> AnyForAgendaItemAsync(Guid agendaItemId, CancellationToken cancellationToken);

    /// <summary>Assigns the next number of the meeting and saves; concurrent callers never get the same number.</summary>
    Task AddNumberedAsync(Resolution resolution, CancellationToken cancellationToken);

    Task SaveChangesAsync(CancellationToken cancellationToken);
}

public sealed class ResolutionService(
    IResolutionRepository resolutions,
    IMeetingRepository meetings,
    IAgendaRepository agenda,
    ITenantContext tenant,
    ICurrentUser user,
    TimeProvider clock)
{
    public async Task<IReadOnlyList<ResolutionDto>?> ListAsync(Guid meetingId, CancellationToken cancellationToken)
    {
        if (await meetings.GetAsync(meetingId, cancellationToken) is null)
        {
            return null;
        }

        return (await resolutions.ListAsync(meetingId, cancellationToken)).Select(ResolutionDto.From).ToList();
    }

    public async Task<ResolutionDto?> CreateAsync(Guid meetingId, string text, Guid? agendaItemId, CancellationToken cancellationToken)
    {
        if (await meetings.GetAsync(meetingId, cancellationToken) is not { } meeting)
        {
            return null;
        }

        EnsureOpen(meeting);
        await EnsureAgendaItemAsync(meetingId, agendaItemId, cancellationToken);
        var resolution = new Resolution(Guid.NewGuid(), tenant.RequiredTenantId, meetingId, 1, text, agendaItemId);
        await resolutions.AddNumberedAsync(resolution, cancellationToken);
        return ResolutionDto.From(resolution);
    }

    public Task<ResolutionDto?> EditAsync(Guid meetingId, Guid resolutionId, long expectedLockVersion, string text, Guid? agendaItemId, CancellationToken cancellationToken) =>
        ChangeAsync(meetingId, resolutionId, expectedLockVersion, async r =>
        {
            await EnsureAgendaItemAsync(meetingId, agendaItemId, cancellationToken);
            r.Edit(text, agendaItemId);
        }, cancellationToken);

    public Task<ResolutionDto?> AdoptAsync(Guid meetingId, Guid resolutionId, long expectedLockVersion, CancellationToken cancellationToken) =>
        ChangeAsync(meetingId, resolutionId, expectedLockVersion, r =>
        {
            r.Adopt(user.RequiredUserId, clock.GetUtcNow());
            return Task.CompletedTask;
        }, cancellationToken);

    public Task<ResolutionDto?> RejectAsync(Guid meetingId, Guid resolutionId, long expectedLockVersion, CancellationToken cancellationToken) =>
        ChangeAsync(meetingId, resolutionId, expectedLockVersion, r =>
        {
            r.Reject(user.RequiredUserId, clock.GetUtcNow());
            return Task.CompletedTask;
        }, cancellationToken);

    public Task<ResolutionDto?> RevokeAsync(Guid meetingId, Guid resolutionId, long expectedLockVersion, string reason, CancellationToken cancellationToken) =>
        ChangeAsync(meetingId, resolutionId, expectedLockVersion, r =>
        {
            r.Revoke(user.RequiredUserId, reason, clock.GetUtcNow());
            return Task.CompletedTask;
        }, cancellationToken);

    private async Task<ResolutionDto?> ChangeAsync(
        Guid meetingId, Guid resolutionId, long expectedLockVersion, Func<Resolution, Task> change, CancellationToken cancellationToken)
    {
        if (await resolutions.GetAsync(meetingId, resolutionId, cancellationToken) is not { } resolution)
        {
            return null;
        }

        if (resolution.LockVersion != expectedLockVersion)
        {
            throw new MeetingConcurrencyException("The resolution was changed by someone else; reload and retry.");
        }

        await change(resolution);
        await resolutions.SaveChangesAsync(cancellationToken);
        return ResolutionDto.From(resolution);
    }

    private async Task EnsureAgendaItemAsync(Guid meetingId, Guid? agendaItemId, CancellationToken cancellationToken)
    {
        if (agendaItemId is { } id && (await agenda.ListAsync(meetingId, cancellationToken)).All(i => i.Id != id))
        {
            throw new MeetingDomainException("The agenda item does not belong to this meeting.");
        }
    }

    private static void EnsureOpen(Meeting meeting)
    {
        if (meeting.Status == MeetingStatus.Cancelled)
        {
            throw new MeetingDomainException("A cancelled meeting cannot have resolutions.");
        }
    }
}
