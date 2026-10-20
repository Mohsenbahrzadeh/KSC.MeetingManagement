using KSC.MeetingManagement.Calendar;

namespace KSC.MeetingManagement.UnitTests;

public class JalaliCalendarTests
{
    private static readonly KscJalaliCalendarService Calendar = new();

    [Theory]
    [InlineData(2025, 3, 21, 1404, 1, 1)]
    [InlineData(2025, 3, 20, 1403, 12, 30)]
    [InlineData(2024, 3, 20, 1403, 1, 1)]
    [InlineData(2026, 3, 21, 1405, 1, 1)]
    [InlineData(2025, 9, 23, 1404, 7, 1)]
    [InlineData(2025, 9, 22, 1404, 6, 31)]
    [InlineData(1979, 2, 11, 1357, 11, 22)]
    [InlineData(2026, 3, 20, 1404, 12, 29)]
    public void Known_dates_convert_both_ways(int gy, int gm, int gd, int jy, int jm, int jd)
    {
        var gregorian = new DateOnly(gy, gm, gd);
        var jalali = JalaliDate.Create(jy, jm, jd);

        Assert.Equal(jalali, Calendar.FromGregorian(gregorian));
        Assert.Equal(gregorian, Calendar.ToGregorian(jalali));
    }

    [Theory]
    [InlineData(1375)]
    [InlineData(1379)]
    [InlineData(1395)]
    [InlineData(1399)]
    [InlineData(1403)]
    [InlineData(1408)]
    public void Official_leap_years(int year) => Assert.True(Calendar.IsLeapYear(year));

    [Theory]
    [InlineData(1400)]
    [InlineData(1401)]
    [InlineData(1402)]
    [InlineData(1404)]
    [InlineData(1405)]
    [InlineData(1407)]
    public void Common_years(int year) => Assert.False(Calendar.IsLeapYear(year));

    [Theory]
    [InlineData(1404, 1, 31)]
    [InlineData(1404, 6, 31)]
    [InlineData(1404, 7, 30)]
    [InlineData(1404, 11, 30)]
    [InlineData(1404, 12, 29)]
    [InlineData(1403, 12, 30)]
    public void Month_lengths(int year, int month, int expected) =>
        Assert.Equal(expected, Calendar.DaysInMonth(year, month));

    [Fact]
    public void Year_lengths_are_365_or_366_and_every_day_round_trips()
    {
        var day = Calendar.ToGregorian(JalaliDate.Create(1300, 1, 1));
        var end = Calendar.ToGregorian(JalaliDate.Create(1500, 12, 29));
        var previous = Calendar.FromGregorian(day);
        day = day.AddDays(1);

        for (; day <= end; day = day.AddDays(1))
        {
            var current = Calendar.FromGregorian(day);
            Assert.Equal(day, Calendar.ToGregorian(current));
            Assert.True(current > previous);

            // Consecutive days are either the next day of the month or the 1st of the next month/year.
            var isNextDay = current.Year == previous.Year && current.Month == previous.Month && current.Day == previous.Day + 1;
            var isNextMonth = current.Day == 1 && previous.Day == Calendar.DaysInMonth(previous.Year, previous.Month)
                && ((current.Year == previous.Year && current.Month == previous.Month + 1)
                    || (current.Year == previous.Year + 1 && current.Month == 1 && previous.Month == 12));
            Assert.True(isNextDay || isNextMonth, $"{previous} -> {current}");
            previous = current;
        }
    }

    [Fact]
    public void Weekday_uses_saturday_as_first_day()
    {
        Assert.Equal(PersianWeekday.Friday, Calendar.GetWeekday(JalaliDate.Create(1404, 1, 1)));
        Assert.Equal(PersianWeekday.Saturday, Calendar.GetWeekday(JalaliDate.Create(1404, 1, 2)));
        Assert.Equal(PersianWeekday.Wednesday, Calendar.GetWeekday(JalaliDate.Create(1404, 1, 6)));
    }

    [Fact]
    public void Week_range_runs_saturday_to_friday_across_year_boundary()
    {
        var week = Calendar.WeekRange(JalaliDate.Create(1404, 1, 1));

        Assert.Equal(JalaliDate.Create(1403, 12, 25), week.Start);
        Assert.Equal(JalaliDate.Create(1404, 1, 1), week.End);
        Assert.Equal(PersianWeekday.Saturday, Calendar.GetWeekday(week.Start));
    }

    [Fact]
    public void Week_range_of_a_saturday_starts_that_day()
    {
        var saturday = JalaliDate.Create(1404, 1, 2);

        Assert.Equal(saturday, Calendar.WeekRange(saturday).Start);
    }

    [Fact]
    public void Month_grid_covers_whole_weeks_around_the_month()
    {
        var grid = Calendar.MonthGrid(1404, 1);

        Assert.Equal(6, grid.Count);
        Assert.Equal(JalaliDate.Create(1403, 12, 25), grid[0].Start);
        Assert.Equal(new DateOnly(2025, 4, 25), Calendar.ToGregorian(grid[^1].End));
        Assert.All(grid, w => Assert.Equal(PersianWeekday.Saturday, Calendar.GetWeekday(w.Start)));
    }

    [Fact]
    public void Month_grid_for_a_month_that_fits_in_four_weeks_is_never_shorter_than_the_month()
    {
        foreach (var month in Enumerable.Range(1, 12))
        {
            var grid = Calendar.MonthGrid(1404, month);
            var days = grid.Sum(w => Calendar.ToGregorianRange(w).DayCount);
            Assert.Equal(0, days % 7);
            Assert.True(days >= Calendar.DaysInMonth(1404, month));
        }
    }

    [Fact]
    public void Gregorian_range_is_half_open()
    {
        var range = Calendar.ToGregorianRange(Calendar.MonthRange(1403, 12));

        Assert.Equal(new DateOnly(2025, 2, 19), range.Start);
        Assert.Equal(new DateOnly(2025, 3, 21), range.EndExclusive);
        Assert.Equal(30, range.DayCount);
    }

    [Fact]
    public void Day_bounds_in_utc_follow_the_time_zone()
    {
        var tehran = TimeZoneInfo.CreateCustomTimeZone("test-tehran", TimeSpan.FromHours(3.5), "Tehran", "Tehran");

        var (start, end) = Calendar.DayBoundsUtc(JalaliDate.Create(1404, 1, 1), tehran);

        Assert.Equal(new DateTimeOffset(2025, 3, 20, 20, 30, 0, TimeSpan.Zero), start);
        Assert.Equal(new DateTimeOffset(2025, 3, 21, 20, 30, 0, TimeSpan.Zero), end);
    }

    [Theory]
    [InlineData("1404/01/01", true)]
    [InlineData("1404-1-1", true)]
    [InlineData("۱۴۰۴/۰۱/۰۱", true)]
    [InlineData("١٤٠٤/٠١/٠١", true)]
    [InlineData(" 1403/12/30 ", true)]
    [InlineData("1404/12/30", false)]
    [InlineData("1404/13/01", false)]
    [InlineData("1404/00/10", false)]
    [InlineData("1404/07/31", false)]
    [InlineData("1177/12/29", false)]
    [InlineData("1634/01/01", false)]
    [InlineData("1404/01", false)]
    [InlineData("-1404/01/01", false)]
    [InlineData("abc", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Parsing_validates_input(string? text, bool valid) =>
        Assert.Equal(valid, JalaliDate.TryParse(text, out _));

    [Fact]
    public void Parsed_date_formats_back_with_ascii_digits()
    {
        Assert.True(JalaliDate.TryParse("۱۴۰۴/۱/۵", out var date));
        Assert.Equal("1404/01/05", date.ToString());
    }

    [Fact]
    public void Invalid_dates_cannot_be_created()
    {
        Assert.False(JalaliDate.TryCreate(1404, 12, 30, out _));
        Assert.Throws<ArgumentOutOfRangeException>(() => JalaliDate.Create(1404, 2, 32));
        Assert.Throws<ArgumentOutOfRangeException>(() => Calendar.DaysInMonth(2000, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => Calendar.DaysInMonth(1404, 13));
    }

    [Fact]
    public void Ranges_reject_inverted_bounds_and_support_contains()
    {
        var a = JalaliDate.Create(1404, 1, 1);
        var b = JalaliDate.Create(1404, 1, 10);

        Assert.Throws<ArgumentException>(() => new JalaliRange(b, a));
        Assert.True(new JalaliRange(a, b).Contains(b));
        Assert.False(new JalaliRange(a, a).Contains(b));
    }

    [Fact]
    public void Names_are_persian()
    {
        Assert.Equal("فروردین", Calendar.MonthName(1));
        Assert.Equal("اسفند", Calendar.MonthName(12));
        Assert.Equal("شنبه", Calendar.WeekdayName(PersianWeekday.Saturday));
        Assert.Equal("جمعه", Calendar.WeekdayName(PersianWeekday.Friday));
    }
}
