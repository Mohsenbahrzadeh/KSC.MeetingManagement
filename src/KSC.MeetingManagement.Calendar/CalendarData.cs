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
    CalendarItemStatus Status,
    long Version = 0)
{
    /// <summary>Only meetings that are not running, finished or cancelled can be moved.</summary>
    public bool IsMovable => Status is CalendarItemStatus.Draft or CalendarItemStatus.Scheduled or CalendarItemStatus.Postponed;
}

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

/// <summary>Outcome of asking the backend to move a meeting. The calendar only shows the new time after a successful result.</summary>
public sealed record CalendarMoveResult(bool Succeeded, string? Error = null)
{
    public static CalendarMoveResult Ok { get; } = new(true);

    public static CalendarMoveResult Fail(string error) => new(false, error);
}

/// <summary>Where a dragged meeting was dropped: a day (month view) or a day and hour (week/day view).</summary>
public sealed record CalendarDrop(CalendarItem Item, JalaliDate Day, int? Hour);

/// <summary>The meeting currently being dragged; shared between the container and its views.</summary>
public sealed class CalendarDragState
{
    public CalendarItem? Item { get; set; }
}
