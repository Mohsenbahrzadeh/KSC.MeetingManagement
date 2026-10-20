using KSC.MeetingManagement.Calendar;

namespace KSC.MeetingManagement.UnitTests;

public class CalendarNavigatorTests
{
    private static readonly CalendarNavigator Navigator = new();

    private static CalendarView Month(int y, int m, int d) => new(CalendarViewMode.Month, JalaliDate.Create(y, m, d));

    [Fact]
    public void Month_navigation_crosses_the_year_boundary_both_ways()
    {
        Assert.Equal(JalaliDate.Create(1403, 12, 15), Navigator.Previous(Month(1404, 1, 15)).Anchor);
        Assert.Equal(JalaliDate.Create(1404, 1, 15), Navigator.Next(Month(1403, 12, 15)).Anchor);
    }

    [Fact]
    public void Month_navigation_clamps_the_day_to_the_target_month()
    {
        Assert.Equal(JalaliDate.Create(1403, 11, 30), Navigator.Previous(Month(1403, 12, 30)).Anchor);
        Assert.Equal(JalaliDate.Create(1404, 7, 30), Navigator.Next(Month(1404, 6, 31)).Anchor);
        Assert.Equal(JalaliDate.Create(1404, 12, 29), Navigator.Next(Month(1404, 11, 30)).Anchor);
    }

    [Fact]
    public void Week_and_day_navigation_step_by_days_across_months_and_years()
    {
        var week = new CalendarView(CalendarViewMode.Week, JalaliDate.Create(1403, 12, 28));
        var day = new CalendarView(CalendarViewMode.Day, JalaliDate.Create(1403, 12, 30));

        Assert.Equal(JalaliDate.Create(1404, 1, 5), Navigator.Next(week).Anchor);
        Assert.Equal(JalaliDate.Create(1403, 12, 21), Navigator.Previous(week).Anchor);
        Assert.Equal(JalaliDate.Create(1404, 1, 1), Navigator.Next(day).Anchor);
        Assert.Equal(JalaliDate.Create(1403, 12, 29), Navigator.Previous(day).Anchor);
    }

    [Fact]
    public void Visible_range_matches_the_mode()
    {
        var anchor = JalaliDate.Create(1404, 1, 10);

        var month = Navigator.VisibleRange(new CalendarView(CalendarViewMode.Month, anchor));
        var week = Navigator.VisibleRange(new CalendarView(CalendarViewMode.Week, anchor));
        var day = Navigator.VisibleRange(new CalendarView(CalendarViewMode.Day, anchor));
        var agenda = Navigator.VisibleRange(new CalendarView(CalendarViewMode.Agenda, anchor));

        Assert.Equal(JalaliDate.Create(1403, 12, 25), month.Start);
        Assert.Equal(JalaliDate.Create(1404, 1, 31), agenda.End);
        Assert.Equal(new DateOnly(2025, 4, 25), KscJalaliCalendarService.Instance.ToGregorian(month.End));
        Assert.Equal(JalaliDate.Create(1404, 1, 9), week.Start);
        Assert.Equal(JalaliDate.Create(1404, 1, 15), week.End);
        Assert.Equal(new JalaliRange(anchor, anchor), day);
        Assert.Equal(JalaliDate.Create(1404, 1, 1), agenda.Start);
    }

    [Fact]
    public void Today_and_goto_keep_the_mode_and_change_only_the_anchor()
    {
        var view = new CalendarView(CalendarViewMode.Week, JalaliDate.Create(1404, 1, 1));
        var today = JalaliDate.Create(1404, 6, 15);

        Assert.Equal(new CalendarView(CalendarViewMode.Week, today), CalendarNavigator.Today(view, today));
        Assert.Equal(CalendarViewMode.Day, CalendarNavigator.WithMode(view, CalendarViewMode.Day).Mode);
        Assert.Equal(view.Anchor, CalendarNavigator.WithMode(view, CalendarViewMode.Day).Anchor);
    }

    [Fact]
    public void Navigation_stops_at_the_edge_of_the_supported_years()
    {
        var first = Month(JalaliDate.MinYear + 1, 1, 1);
        var last = Month(JalaliDate.MaxYear - 1, 12, 1);

        Assert.Equal(first, Navigator.Previous(first));
        Assert.False(Navigator.CanMove(first, -1));
        Assert.Equal(last, Navigator.Next(last));
        Assert.True(Navigator.CanMove(first, 1));
        Assert.Equal(first, CalendarNavigator.GoTo(first, JalaliDate.Create(JalaliDate.MinYear, 5, 5)));
    }

    [Fact]
    public void Titles_are_persian()
    {
        Assert.Equal("فروردین ۱۴۰۴", Navigator.Title(Month(1404, 1, 15)));
        Assert.Equal("جمعه ۱ فروردین ۱۴۰۴", Navigator.Title(new CalendarView(CalendarViewMode.Day, JalaliDate.Create(1404, 1, 1))));
        Assert.Equal("۲۵ اسفند ۱۴۰۳ – ۱ فروردین ۱۴۰۴", Navigator.Title(new CalendarView(CalendarViewMode.Week, JalaliDate.Create(1404, 1, 1))));
        Assert.Equal("۲ فروردین – ۸ فروردین ۱۴۰۴", Navigator.Title(new CalendarView(CalendarViewMode.Week, JalaliDate.Create(1404, 1, 4))));
    }

    [Fact]
    public void Persian_digit_helpers()
    {
        Assert.Equal("۱۴۰۴/۰۱/۰۵", PersianText.Format(JalaliDate.Create(1404, 1, 5)));
        Assert.Equal("۰۱۲۳۴۵۶۷۸۹", PersianText.Digits("0123456789"));
    }

    [Fact]
    public void Tehran_today_follows_the_tehran_clock_not_utc()
    {
        // 2025-03-20 21:00 UTC is already 00:30 on 21 March in Tehran, i.e. 1 Farvardin 1404.
        var clock = new FixedClock(new DateTimeOffset(2025, 3, 20, 21, 0, 0, TimeSpan.Zero));

        Assert.Equal(JalaliDate.Create(1404, 1, 1), TehranClock.Today(clock));
        Assert.Equal(JalaliDate.Create(1403, 12, 30), TehranClock.Today(new FixedClock(new DateTimeOffset(2025, 3, 20, 20, 0, 0, TimeSpan.Zero))));
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
