using KSC.MeetingManagement.Application.Identity;
using KSC.MeetingManagement.Application.Meetings;
using KSC.MeetingManagement.Application.Resolutions;
using KSC.MeetingManagement.Application.Tenants;
using KSC.MeetingManagement.Domain.Meetings;

namespace KSC.MeetingManagement.Application.Actions;

public sealed record ActionItemInput(string Title, string? Description, Guid OwnerUserId, DateOnly DueDate, Guid? ResolutionId);

public sealed record ActionItemDto(
    Guid Id,
    Guid MeetingId,
    Guid? ResolutionId,
    string Title,
    string? Description,
    Guid OwnerUserId,
    DateOnly DueDate,
    ActionItemStatus Status,
    bool IsOverdue,
    DateTimeOffset? CompletedAtUtc,
    Guid? CompletedByUserId,
    string? Note,
    long LockVersion)
{
    public static ActionItemDto From(ActionItem a, DateOnly today)
    {
        ArgumentNullException.ThrowIfNull(a);
        return new(a.Id, a.MeetingId, a.ResolutionId, a.Title, a.Description, a.OwnerUserId, a.DueDate, a.Status, a.IsOverdue(today),
            a.CompletedAtUtc, a.CompletedByUserId, a.Note, a.LockVersion);
    }
}

public sealed record ActionItemQuery(Guid? OwnerUserId, ActionItemStatus? Status, DateOnly? DueBefore, bool ActiveOnly, int Skip, int Take);

/// <summary>Persistence port. Implementations must only return actions of the current tenant.</summary>
public interface IActionItemRepository
{
    Task<ActionItem?> GetAsync(Guid meetingId, Guid actionId, CancellationToken cancellationToken);

    Task<IReadOnlyList<ActionItem>> ListForMeetingAsync(Guid meetingId, CancellationToken cancellationToken);

    Task<IReadOnlyList<ActionItem>> QueryAsync(ActionItemQuery query, CancellationToken cancellationToken);

    Task AddAsync(ActionItem item, CancellationToken cancellationToken);

    Task SaveChangesAsync(CancellationToken cancellationToken);
}

public sealed class ActionItemService(
    IActionItemRepository actions,
    IMeetingRepository meetings,
    IResolutionRepository resolutions,
    IMembershipStore memberships,
    Notifications.NotificationService notifications,
    ITenantContext tenant,
    ICurrentUser user,
    TimeProvider clock)
{
    public const int MaxPageSize = 200;

    /// <summary>Today in the organisation's time zone; due days are calendar days of that zone.</summary>
    public DateOnly Today() => TenantClock.Today(tenant.Tenant?.TimeZoneId, clock);

    public async Task<IReadOnlyList<ActionItemDto>?> ListForMeetingAsync(Guid meetingId, CancellationToken cancellationToken)
    {
        if (await meetings.GetAsync(meetingId, cancellationToken) is null)
        {
            return null;
        }

        var today = Today();
        return (await actions.ListForMeetingAsync(meetingId, cancellationToken)).Select(a => ActionItemDto.From(a, today)).ToList();
    }

    /// <summary>Actions across the tenant. With ActiveOnly and DueBefore this is the overdue report.</summary>
    public async Task<IReadOnlyList<ActionItemDto>> QueryAsync(ActionItemQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        var bounded = query with { Skip = Math.Max(0, query.Skip), Take = Math.Clamp(query.Take, 1, MaxPageSize) };
        var today = Today();
        return (await actions.QueryAsync(bounded, cancellationToken)).Select(a => ActionItemDto.From(a, today)).ToList();
    }

    /// <summary>Every active action whose due day is before today, oldest first.</summary>
    public Task<IReadOnlyList<ActionItemDto>> OverdueAsync(Guid? ownerUserId, int skip, int take, CancellationToken cancellationToken) =>
        QueryAsync(new ActionItemQuery(ownerUserId, null, Today(), ActiveOnly: true, skip, take), cancellationToken);

    public async Task<ActionItemDto?> CreateAsync(Guid meetingId, ActionItemInput input, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);

        if (await meetings.GetAsync(meetingId, cancellationToken) is not { } meeting)
        {
            return null;
        }

        if (meeting.Status == MeetingStatus.Cancelled)
        {
            throw new MeetingDomainException("A cancelled meeting cannot have actions.");
        }

        var today = Today();
        if (input.DueDate < today)
        {
            throw new MeetingDomainException("The due date cannot be in the past.");
        }

        await EnsureOwnerAsync(input.OwnerUserId, cancellationToken);
        await EnsureResolutionAsync(meetingId, input.ResolutionId, cancellationToken);
        var item = new ActionItem(Guid.NewGuid(), tenant.RequiredTenantId, meetingId, input.ResolutionId, input.Title, input.Description, input.OwnerUserId, input.DueDate);
        await actions.AddAsync(item, cancellationToken);
        await notifications.ActionAssignedAsync(item, cancellationToken);
        await actions.SaveChangesAsync(cancellationToken);
        return ActionItemDto.From(item, today);
    }

    public Task<ActionItemDto?> UpdateAsync(Guid meetingId, Guid actionId, long expectedLockVersion, ActionItemInput input, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);

        return ChangeAsync(meetingId, actionId, expectedLockVersion, canManage: true, requireOwnerOrManager: false, async a =>
        {
            await EnsureOwnerAsync(input.OwnerUserId, cancellationToken);
            var previousOwner = a.OwnerUserId;
            a.Update(input.Title, input.Description, input.OwnerUserId, input.DueDate);
            if (a.OwnerUserId != previousOwner)
            {
                await notifications.ActionAssignedAsync(a, cancellationToken);
            }
        }, cancellationToken);
    }

    /// <summary>The owner (or a manager) starts work on the action.</summary>
    public Task<ActionItemDto?> StartAsync(Guid meetingId, Guid actionId, long expectedLockVersion, bool canManage, CancellationToken cancellationToken) =>
        ChangeAsync(meetingId, actionId, expectedLockVersion, canManage, requireOwnerOrManager: true, a =>
        {
            a.Start();
            return Task.CompletedTask;
        }, cancellationToken);

    /// <summary>Records completion with who completed it and when.</summary>
    public Task<ActionItemDto?> CompleteAsync(Guid meetingId, Guid actionId, long expectedLockVersion, string? note, bool canManage, CancellationToken cancellationToken) =>
        ChangeAsync(meetingId, actionId, expectedLockVersion, canManage, requireOwnerOrManager: true, a =>
        {
            a.Complete(user.RequiredUserId, note, clock.GetUtcNow());
            return Task.CompletedTask;
        }, cancellationToken);

    public Task<ActionItemDto?> CancelAsync(Guid meetingId, Guid actionId, long expectedLockVersion, string reason, CancellationToken cancellationToken) =>
        ChangeAsync(meetingId, actionId, expectedLockVersion, canManage: true, requireOwnerOrManager: false, a =>
        {
            a.Cancel(reason);
            return Task.CompletedTask;
        }, cancellationToken);

    private async Task<ActionItemDto?> ChangeAsync(
        Guid meetingId,
        Guid actionId,
        long expectedLockVersion,
        bool canManage,
        bool requireOwnerOrManager,
        Func<ActionItem, Task> change,
        CancellationToken cancellationToken)
    {
        if (await actions.GetAsync(meetingId, actionId, cancellationToken) is not { } item)
        {
            return null;
        }

        // Someone who is neither the owner nor a manager must not learn the action exists.
        if (requireOwnerOrManager && !canManage && item.OwnerUserId != user.RequiredUserId)
        {
            return null;
        }

        if (item.LockVersion != expectedLockVersion)
        {
            throw new MeetingConcurrencyException("The action was changed by someone else; reload and retry.");
        }

        await change(item);
        await actions.SaveChangesAsync(cancellationToken);
        return ActionItemDto.From(item, Today());
    }

    private async Task EnsureOwnerAsync(Guid ownerUserId, CancellationToken cancellationToken)
    {
        if (await memberships.FindAsync(tenant.RequiredTenantId, ownerUserId, cancellationToken) is not { IsActive: true })
        {
            throw new MeetingDomainException("The owner must be an active member of this organisation.");
        }
    }

    private async Task EnsureResolutionAsync(Guid meetingId, Guid? resolutionId, CancellationToken cancellationToken)
    {
        if (resolutionId is { } id && await resolutions.GetAsync(meetingId, id, cancellationToken) is null)
        {
            throw new MeetingDomainException("The resolution does not belong to this meeting.");
        }
    }
}

/// <summary>Resolves "today" in a tenant's time zone, falling back to UTC when the zone id is unknown on this machine.</summary>
public static class TenantClock
{
    public static DateOnly Today(string? timeZoneId, TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(clock);

        var zone = TimeZoneInfo.Utc;
        if (!string.IsNullOrWhiteSpace(timeZoneId) && TimeZoneInfo.TryFindSystemTimeZoneById(timeZoneId, out var found))
        {
            zone = found;
        }

        return DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(clock.GetUtcNow(), zone).DateTime);
    }
}
