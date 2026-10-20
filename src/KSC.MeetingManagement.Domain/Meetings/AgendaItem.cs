namespace KSC.MeetingManagement.Domain.Meetings;

/// <summary>One topic of a meeting's agenda. <see cref="Order"/> is a gapless 1..n sequence maintained by <see cref="AgendaOrdering"/>.</summary>
public sealed class AgendaItem
{
    public const int MaxTitleLength = 300;
    public const int MaxDescriptionLength = 4000;
    public const int MaxDurationMinutes = 600;

    public AgendaItem(
        Guid id, Guid tenantId, Guid meetingId, int order, string title, string? description, Guid? presenterUserId, int? durationMinutes)
    {
        if (id == Guid.Empty || tenantId == Guid.Empty || meetingId == Guid.Empty)
        {
            throw new MeetingDomainException("Agenda item, tenant and meeting ids are required.");
        }

        if (order < 1)
        {
            throw new MeetingDomainException("Agenda order starts at 1.");
        }

        Id = id;
        TenantId = tenantId;
        MeetingId = meetingId;
        Order = order;
        Apply(title, description, presenterUserId, durationMinutes);
    }

    public Guid Id { get; }
    public Guid TenantId { get; }
    public Guid MeetingId { get; }
    public int Order { get; internal set; }
    public string Title { get; private set; } = string.Empty;
    public string? Description { get; private set; }
    public Guid? PresenterUserId { get; private set; }
    public int? DurationMinutes { get; private set; }

    public void Update(string title, string? description, Guid? presenterUserId, int? durationMinutes) =>
        Apply(title, description, presenterUserId, durationMinutes);

    private void Apply(string title, string? description, Guid? presenterUserId, int? durationMinutes)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            throw new MeetingDomainException("Agenda item title is required.");
        }

        if (title.Trim().Length > MaxTitleLength)
        {
            throw new MeetingDomainException($"Agenda item title cannot exceed {MaxTitleLength} characters.");
        }

        if (description is { Length: > MaxDescriptionLength })
        {
            throw new MeetingDomainException($"Agenda item description cannot exceed {MaxDescriptionLength} characters.");
        }

        if (durationMinutes is < 1 or > MaxDurationMinutes)
        {
            throw new MeetingDomainException($"Duration must be between 1 and {MaxDurationMinutes} minutes.");
        }

        if (presenterUserId == Guid.Empty)
        {
            throw new MeetingDomainException("Presenter id is invalid.");
        }

        Title = title.Trim();
        Description = string.IsNullOrWhiteSpace(description) ? null : description.Trim();
        PresenterUserId = presenterUserId;
        DurationMinutes = durationMinutes;
    }
}

/// <summary>Keeps agenda order a gapless, duplicate-free 1..n sequence.</summary>
public static class AgendaOrdering
{
    /// <summary>Applies a full new order. The ids must be exactly the current items, each once.</summary>
    public static void Reorder(IReadOnlyCollection<AgendaItem> items, IReadOnlyList<Guid> orderedIds)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(orderedIds);

        if (orderedIds.Count != items.Count || orderedIds.Distinct().Count() != orderedIds.Count
            || !items.Select(i => i.Id).ToHashSet().SetEquals(orderedIds))
        {
            throw new MeetingDomainException("The new order must list every agenda item exactly once.");
        }

        var byId = items.ToDictionary(i => i.Id);
        for (var index = 0; index < orderedIds.Count; index++)
        {
            byId[orderedIds[index]].Order = index + 1;
        }
    }

    /// <summary>Closes gaps after a removal, preserving relative order.</summary>
    public static void Normalize(IEnumerable<AgendaItem> items)
    {
        ArgumentNullException.ThrowIfNull(items);

        var order = 1;
        foreach (var item in items.OrderBy(i => i.Order))
        {
            item.Order = order++;
        }
    }
}
