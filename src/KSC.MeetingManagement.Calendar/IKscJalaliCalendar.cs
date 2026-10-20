namespace KSC.MeetingManagement.Calendar;

/// <summary>Weekday in the Iranian week, which starts on Saturday.</summary>
public enum PersianWeekday
{
    Saturday = 0,
    Sunday = 1,
    Monday = 2,
    Tuesday = 3,
    Wednesday = 4,
    Thursday = 5,
    Friday = 6,
}

/// <summary>
/// Contract for Jalali calendar arithmetic. Independent of UI and storage so it can be tested alone.
/// Supported years: <see cref="JalaliDate.MinYear"/> to <see cref="JalaliDate.MaxYear"/>.
/// </summary>
public interface IKscJalaliCalendar
{
    bool IsLeapYear(int year);

    int DaysInMonth(int year, int month);

    JalaliDate FromGregorian(DateOnly day);

    DateOnly ToGregorian(JalaliDate day);

    PersianWeekday GetWeekday(JalaliDate day);

    /// <summary>First to last day of the given month.</summary>
    JalaliRange MonthRange(int year, int month);

    /// <summary>The Saturday-to-Friday week containing the date.</summary>
    JalaliRange WeekRange(JalaliDate day);

    /// <summary>
    /// Whole weeks (Saturday to Friday) needed to display the month: the first week starts on the Saturday
    /// on or before the 1st and the last ends on the Friday on or after the last day.
    /// </summary>
    IReadOnlyList<JalaliRange> MonthGrid(int year, int month);

    /// <summary>Gregorian half-open range of a Jalali range.</summary>
    GregorianRange ToGregorianRange(JalaliRange range);

    /// <summary>UTC instants [start, end) of one Jalali day in the given time zone (handles DST days of 23 or 25 hours).</summary>
    (DateTimeOffset StartUtc, DateTimeOffset EndUtc) DayBoundsUtc(JalaliDate day, TimeZoneInfo timeZone);

    string MonthName(int month);

    string WeekdayName(PersianWeekday weekday);
}
