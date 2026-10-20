namespace KSC.MeetingManagement.Calendar;

public enum CalendarViewMode
{
    Month = 1,
    Week = 2,
    Day = 3,
    Agenda = 4,
}

/// <summary>What the calendar currently shows: a mode and the day it is anchored on.</summary>
public readonly record struct CalendarView(CalendarViewMode Mode, JalaliDate Anchor);

/// <summary>
/// Pure navigation logic shared by the toolbar and the views, so every control resolves to the same visible range.
/// Navigation that would leave the supported year range returns the view unchanged.
/// </summary>
public sealed class CalendarNavigator(IKscJalaliCalendar calendar)
{
    private const int NavMinYear = JalaliDate.MinYear + 1;
    private const int NavMaxYear = JalaliDate.MaxYear - 1;

    public CalendarNavigator()
        : this(KscJalaliCalendarService.Instance)
    {
    }

    public IKscJalaliCalendar Calendar { get; } = calendar;

    /// <summary>The Jalali days that must be loaded and drawn for the view (month view includes the leading and trailing days of neighbour months).</summary>
    public JalaliRange VisibleRange(CalendarView view)
    {
        var anchor = view.Anchor;
        switch (view.Mode)
        {
            case CalendarViewMode.Month:
                var grid = Calendar.MonthGrid(anchor.Year, anchor.Month);
                return new JalaliRange(grid[0].Start, grid[^1].End);
            case CalendarViewMode.Week:
                return Calendar.WeekRange(anchor);
            case CalendarViewMode.Day:
                return new JalaliRange(anchor, anchor);
            default:
                return Calendar.MonthRange(anchor.Year, anchor.Month);
        }
    }

    public CalendarView Previous(CalendarView view) => Move(view, -1);

    public CalendarView Next(CalendarView view) => Move(view, 1);

    public static CalendarView Today(CalendarView view, JalaliDate today) => GoTo(view, today);

    public static CalendarView WithMode(CalendarView view, CalendarViewMode mode) => view with { Mode = mode };

    public static CalendarView GoTo(CalendarView view, JalaliDate date) =>
        date.Year is < NavMinYear or > NavMaxYear ? view : view with { Anchor = date };

    public bool CanMove(CalendarView view, int direction) => Move(view, direction) != view;

    public string Title(CalendarView view)
    {
        var a = view.Anchor;
        switch (view.Mode)
        {
            case CalendarViewMode.Week:
                var week = Calendar.WeekRange(a);
                var sameYear = week.Start.Year == week.End.Year;
                var start = sameYear
                    ? $"{PersianText.Digits(week.Start.Day)} {Calendar.MonthName(week.Start.Month)}"
                    : PersianText.FormatLong(week.Start, Calendar);
                return $"{start} – {PersianText.FormatLong(week.End, Calendar)}";
            case CalendarViewMode.Day:
                return $"{Calendar.WeekdayName(Calendar.GetWeekday(a))} {PersianText.FormatLong(a, Calendar)}";
            default:
                return $"{Calendar.MonthName(a.Month)} {PersianText.Digits(a.Year)}";
        }
    }

    private CalendarView Move(CalendarView view, int direction)
    {
        var a = view.Anchor;
        switch (view.Mode)
        {
            case CalendarViewMode.Week:
            case CalendarViewMode.Day:
                var step = (view.Mode == CalendarViewMode.Week ? 7 : 1) * direction;
                var moved = Calendar.FromGregorianSafe(Calendar.ToGregorian(a).AddDays(step));
                return moved is { } m ? GoTo(view, m) : view;
            default:
                var index = (a.Year * 12) + (a.Month - 1) + direction;
                var year = index / 12;
                var month = (index % 12) + 1;
                if (year is < NavMinYear or > NavMaxYear)
                {
                    return view;
                }

                var day = Math.Min(a.Day, Calendar.DaysInMonth(year, month));
                return view with { Anchor = JalaliDate.Create(year, month, day) };
        }
    }
}

internal static class CalendarExtensions
{
    /// <summary>Converts a Gregorian day, returning null when it falls outside the supported Jalali years.</summary>
    public static JalaliDate? FromGregorianSafe(this IKscJalaliCalendar calendar, DateOnly day)
    {
        try
        {
            return calendar.FromGregorian(day);
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }
}
