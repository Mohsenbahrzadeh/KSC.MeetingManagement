using KSC.MeetingManagement.Application.Identity;
using KSC.MeetingManagement.Application.Meetings;
using KSC.MeetingManagement.Application.Tenants;
using KSC.MeetingManagement.Domain.Meetings;

namespace KSC.MeetingManagement.Application.Agenda;

public sealed record AgendaItemInput(string Title, string? Description, Guid? PresenterUserId, int? DurationMinutes);

public sealed record AgendaItemDto(Guid Id, int Order, string Title, string? Description, Guid? PresenterUserId, int? DurationMinutes)
{
    public static AgendaItemDto From(AgendaItem i)
    {
        ArgumentNullException.ThrowIfNull(i);
        return new(i.Id, i.Order, i.Title, i.Description, i.PresenterUserId, i.DurationMinutes);
    }
}

/// <summary>The agenda with its revision, which callers send back as the expected revision when they change it.</summary>
public sealed record AgendaDto(long Revision, IReadOnlyList<AgendaItemDto> Items);

/// <summary>Persistence port. Implementations must only return agenda items of the current tenant.</summary>
public interface IAgendaRepository
{
    /// <summary>Tracked items of the meeting ordered by <see cref="AgendaItem.Order"/>.</summary>
    Task<IReadOnlyList<AgendaItem>> ListAsync(Guid meetingId, CancellationToken cancellationToken);

    Task AddAsync(AgendaItem item, CancellationToken cancellationToken);

    void Remove(AgendaItem item);

    Task SaveChangesAsync(CancellationToken cancellationToken);
}

public sealed class AgendaService(
    IAgendaRepository agenda,
    IMeetingRepository meetings,
    IMembershipStore memberships,
    ITenantContext tenant,
    Resolutions.IResolutionRepository resolutions)
{
    public const int MaxItems = 100;

    public async Task<AgendaDto?> GetAsync(Guid meetingId, CancellationToken cancellationToken)
    {
        if (await meetings.GetAsync(meetingId, cancellationToken) is not { } meeting)
        {
            return null;
        }

        return ToDto(meeting, await agenda.ListAsync(meetingId, cancellationToken));
    }

    /// <summary>Appends an item at the end of the agenda.</summary>
    public Task<AgendaDto?> AddAsync(Guid meetingId, long expectedRevision, AgendaItemInput input, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);

        return MutateAsync(meetingId, expectedRevision, async (meeting, items) =>
        {
            if (items.Count >= MaxItems)
            {
                throw new MeetingDomainException($"An agenda cannot have more than {MaxItems} items.");
            }

            await EnsurePresenterAsync(input.PresenterUserId, cancellationToken);
            await agenda.AddAsync(
                new AgendaItem(Guid.NewGuid(), tenant.RequiredTenantId, meeting.Id, items.Count + 1, input.Title, input.Description, input.PresenterUserId, input.DurationMinutes),
                cancellationToken);
        }, cancellationToken);
    }

    public Task<AgendaDto?> UpdateAsync(Guid meetingId, Guid itemId, long expectedRevision, AgendaItemInput input, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);

        return MutateAsync(meetingId, expectedRevision, async (_, items) =>
        {
            var item = items.FirstOrDefault(i => i.Id == itemId) ?? throw new KeyNotFoundException("Agenda item not found.");
            await EnsurePresenterAsync(input.PresenterUserId, cancellationToken);
            item.Update(input.Title, input.Description, input.PresenterUserId, input.DurationMinutes);
        }, cancellationToken);
    }

    public Task<AgendaDto?> RemoveAsync(Guid meetingId, Guid itemId, long expectedRevision, CancellationToken cancellationToken) =>
        MutateAsync(meetingId, expectedRevision, async (_, items) =>
        {
            var item = items.FirstOrDefault(i => i.Id == itemId) ?? throw new KeyNotFoundException("Agenda item not found.");
            if (await resolutions.AnyForAgendaItemAsync(itemId, cancellationToken))
            {
                throw new MeetingDomainException("This agenda item has resolutions and cannot be removed.");
            }

            agenda.Remove(item);
            AgendaOrdering.Normalize(items.Where(i => i.Id != itemId));
        }, cancellationToken);

    /// <summary>Applies a complete new order; the ids must be exactly the current items.</summary>
    public Task<AgendaDto?> ReorderAsync(Guid meetingId, long expectedRevision, IReadOnlyList<Guid> orderedIds, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(orderedIds);

        return MutateAsync(meetingId, expectedRevision, (_, items) =>
        {
            AgendaOrdering.Reorder(items, orderedIds);
            return Task.CompletedTask;
        }, cancellationToken);
    }

    private async Task<AgendaDto?> MutateAsync(
        Guid meetingId, long expectedRevision, Func<Meeting, IReadOnlyList<AgendaItem>, Task> change, CancellationToken cancellationToken)
    {
        if (await meetings.GetAsync(meetingId, cancellationToken) is not { } meeting)
        {
            return null;
        }

        if (meeting.AgendaRevision != expectedRevision)
        {
            throw new MeetingConcurrencyException("The agenda was changed by someone else; reload and retry.");
        }

        var items = await agenda.ListAsync(meetingId, cancellationToken);
        meeting.TouchAgenda();
        await change(meeting, items);
        await agenda.SaveChangesAsync(cancellationToken);
        return ToDto(meeting, await agenda.ListAsync(meetingId, cancellationToken));
    }

    private async Task EnsurePresenterAsync(Guid? presenterUserId, CancellationToken cancellationToken)
    {
        if (presenterUserId is { } presenter
            && await memberships.FindAsync(tenant.RequiredTenantId, presenter, cancellationToken) is not { IsActive: true })
        {
            throw new MeetingDomainException("The presenter must be an active member of this organisation.");
        }
    }

    private static AgendaDto ToDto(Meeting meeting, IReadOnlyList<AgendaItem> items) =>
        new(meeting.AgendaRevision, items.OrderBy(i => i.Order).Select(AgendaItemDto.From).ToList());
}
