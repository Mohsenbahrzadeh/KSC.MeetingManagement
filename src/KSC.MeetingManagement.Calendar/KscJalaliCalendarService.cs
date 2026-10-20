namespace KSC.MeetingManagement.Calendar;

/// <summary>
/// Jalali calendar arithmetic using the 33-year cycle algorithm with the break table of Borkowski (as used by jalaali-js),
/// which agrees with the official Iranian calendar for years 1178 to 1633. The .NET PersianCalendar is not used because
/// its leap-year rule is a different approximation that diverges from the official calendar in some years.
/// </summary>
public sealed class KscJalaliCalendarService : IKscJalaliCalendar
{
    private static readonly int[] Breaks =
        [-61, 9, 38, 199, 426, 686, 756, 818, 1111, 1181, 1210, 1635, 2060, 2097, 2192, 2262, 2324, 2394, 2456, 3178];

    private static readonly string[] MonthNames =
        ["فروردین", "اردیبهشت", "خرداد", "تیر", "مرداد", "شهریور", "مهر", "آبان", "آذر", "دی", "بهمن", "اسفند"];

    private static readonly string[] WeekdayNames =
        ["شنبه", "یکشنبه", "دوشنبه", "سه‌شنبه", "چهارشنبه", "پنجشنبه", "جمعه"];

    public static KscJalaliCalendarService Instance { get; } = new();

    public bool IsLeapYear(int year)
    {
        RequireYear(year);
        return Cycle(year).Leap == 0;
    }

    public int DaysInMonth(int year, int month)
    {
        RequireYear(year);
        if (month is < 1 or > 12)
        {
            throw new ArgumentOutOfRangeException(nameof(month), "Month must be between 1 and 12.");
        }

        return month <= 6 ? 31 : month <= 11 ? 30 : Cycle(year).Leap == 0 ? 30 : 29;
    }

    public JalaliDate FromGregorian(DateOnly day)
    {
        var gy = day.Year;
        var jy = gy - 621;
        var cycle = Cycle(jy);
        var k = day.DayNumber - new DateOnly(gy, 3, cycle.March).DayNumber;

        if (k >= 0)
        {
            if (k <= 185)
            {
                return JalaliDate.Create(jy, 1 + (k / 31), (k % 31) + 1);
            }

            k -= 186;
        }
        else
        {
            // Before Nowruz of this Gregorian year: belongs to the previous Jalali year.
            jy--;
            k += 179;
            if (cycle.Leap == 1)
            {
                k++;
            }
        }

        return JalaliDate.Create(jy, 7 + (k / 30), (k % 30) + 1);
    }

    public DateOnly ToGregorian(JalaliDate day)
    {
        var cycle = Cycle(day.Year);
        var nowruz = new DateOnly(cycle.GregorianYear, 3, cycle.March);
        var offset = ((day.Month - 1) * 31) - ((day.Month / 7) * (day.Month - 7)) + day.Day - 1;
        return nowruz.AddDays(offset);
    }

    public PersianWeekday GetWeekday(JalaliDate day) =>
        (PersianWeekday)(((int)ToGregorian(day).DayOfWeek + 1) % 7);

    public JalaliRange MonthRange(int year, int month) =>
        new(JalaliDate.Create(year, month, 1), JalaliDate.Create(year, month, DaysInMonth(year, month)));

    public JalaliRange WeekRange(JalaliDate day)
    {
        var gregorian = ToGregorian(day);
        var start = gregorian.AddDays(-(int)GetWeekday(day));
        return new JalaliRange(FromGregorian(start), FromGregorian(start.AddDays(6)));
    }

    public IReadOnlyList<JalaliRange> MonthGrid(int year, int month)
    {
        var month1 = MonthRange(year, month);
        var first = ToGregorian(WeekRange(month1.Start).Start);
        var lastWeekEnd = ToGregorian(WeekRange(month1.End).End);

        var weeks = new List<JalaliRange>();
        for (var day = first; day <= lastWeekEnd; day = day.AddDays(7))
        {
            weeks.Add(new JalaliRange(FromGregorian(day), FromGregorian(day.AddDays(6))));
        }

        return weeks;
    }

    public GregorianRange ToGregorianRange(JalaliRange range) =>
        new(ToGregorian(range.Start), ToGregorian(range.End).AddDays(1));

    public (DateTimeOffset StartUtc, DateTimeOffset EndUtc) DayBoundsUtc(JalaliDate day, TimeZoneInfo timeZone)
    {
        ArgumentNullException.ThrowIfNull(timeZone);

        var gregorianDay = ToGregorian(day);
        return (StartOfDayUtc(gregorianDay, timeZone), StartOfDayUtc(gregorianDay.AddDays(1), timeZone));
    }

    public string MonthName(int month) =>
        month is >= 1 and <= 12
            ? MonthNames[month - 1]
            : throw new ArgumentOutOfRangeException(nameof(month), "Month must be between 1 and 12.");

    public string WeekdayName(PersianWeekday weekday) => WeekdayNames[(int)weekday];

    private static DateTimeOffset StartOfDayUtc(DateOnly day, TimeZoneInfo timeZone)
    {
        var local = day.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified);
        if (timeZone.IsInvalidTime(local))
        {
            // Midnight does not exist (DST gap): the day starts right after the gap.
            local = local.AddHours(1);
        }

        var offset = timeZone.IsAmbiguousTime(local)
            ? timeZone.GetAmbiguousTimeOffsets(local).Max()
            : timeZone.GetUtcOffset(local);
        return new DateTimeOffset(local, offset).ToUniversalTime();
    }

    private static void RequireYear(int year)
    {
        if (year is < JalaliDate.MinYear or > JalaliDate.MaxYear)
        {
            throw new ArgumentOutOfRangeException(nameof(year), $"Year must be between {JalaliDate.MinYear} and {JalaliDate.MaxYear}.");
        }
    }

    /// <summary>
    /// Leap = 0 for a leap year, otherwise years since the last leap year (1..3).
    /// GregorianYear and March give the Gregorian date of 1 Farvardin.
    /// </summary>
    private static (int Leap, int GregorianYear, int March) Cycle(int jy)
    {
        var gy = jy + 621;
        var leapJ = -14;
        var jp = Breaks[0];
        var jump = 0;
        for (var i = 1; i < Breaks.Length; i++)
        {
            var jm = Breaks[i];
            jump = jm - jp;
            if (jy < jm)
            {
                break;
            }

            leapJ += (jump / 33 * 8) + (jump % 33 / 4);
            jp = jm;
        }

        var n = jy - jp;
        leapJ += (n / 33 * 8) + ((n % 33) + 3) / 4;
        if (jump % 33 == 4 && jump - n == 4)
        {
            leapJ++;
        }

        var leapG = (gy / 4) - ((((gy / 100) + 1) * 3) / 4) - 150;
        var march = 20 + leapJ - leapG;

        if (jump - n < 6)
        {
            n = n - jump + ((jump + 4) / 33 * 33);
        }

        var leap = (((n + 1) % 33) - 1) % 4;
        if (leap == -1)
        {
            leap = 4;
        }

        return (leap, gy, march);
    }
}
