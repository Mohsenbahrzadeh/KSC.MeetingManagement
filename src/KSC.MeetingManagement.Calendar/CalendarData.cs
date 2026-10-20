namespace KSC.MeetingManagement.Calendar;

public enum CalendarItemStatus
{
    Draft = 1,
    Scheduled = 2,
    InProgress = 3,
    Completed = 4,
    Cancelled = 5,
    Postponed = 6,
}

/// <summary>A meeting as the calendar needs it. Times are UTC instants; the views convert them to the display zone.</summary>
public sealed record CalendarItem(
    Guid Id,
    string Title,
    DateTimeOffset StartUtc,
    DateTimeOffset EndUtc,
    CalendarItemStatus Status);

/// <summary>Optional narrowing applied by the data provider, on the server where possible.</summary>
public sealed record CalendarFilter(CalendarItemStatus? Status = null);

/// <summary>
/// Source of calendar data (ADR-0001: the only dependency of the calendar library). The calendar always asks for
/// exactly the visible window, as the half-open UTC range [fromUtc, toUtc), and never loads more.
/// </summary>
public interface IMeetingCalendarDataProvider
{
    Task<IReadOnlyList<CalendarItem>> GetItemsAsync(
        DateTimeOffset fromUtc, DateTimeOffset toUtc, CalendarFilter filter, CancellationToken cancellationToken);
}

/// <summary>A selected time span, for example when the user picks an empty slot to create a meeting. Always UTC instants.</summary>
public sealed record CalendarSlot(DateTimeOffset StartUtc, DateTimeOffset EndUtc);
