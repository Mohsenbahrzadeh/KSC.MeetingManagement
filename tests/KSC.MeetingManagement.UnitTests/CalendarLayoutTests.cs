using KSC.MeetingManagement.Calendar;

namespace KSC.MeetingManagement.UnitTests;

public class CalendarLayoutTests
{
    private static readonly TimeZoneInfo Tehran = TimeZoneInfo.CreateCustomTimeZone("t", TimeSpan.FromHours(3.5), "Tehran", "Tehran");
    private static readonly CalendarLayout Layout = new(Tehran);
    private static readonly JalaliDate Day = JalaliDate.Create(1404, 1, 1); // 2025-03-21

    // Local Tehran time on 2025-03-21 expressed as UTC.
    private static DateTimeOffset At(int hour, int minute = 0, int dayOffset = 0) =>
        new DateTimeOffset(2025, 3, 21, hour, minute, 0, TimeSpan.FromHours(3.5)).AddDays(dayOffset).ToUniversalTime();

    private static CalendarItem Item(DateTimeOffset start, DateTimeOffset end, string title = "m") =>
        new(Guid.NewGuid(), title, start, end, CalendarItemStatus.Scheduled);

    [Fact]
    public void Utc_window_covers_exactly_the_days_in_the_display_zone()
    {
        var (from, to) = Layout.UtcWindow(new JalaliRange(Day, JalaliDate.Create(1404, 1, 2)));

        Assert.Equal(new DateTimeOffset(2025, 3, 20, 20, 30, 0, TimeSpan.Zero), from);
        Assert.Equal(new DateTimeOffset(2025, 3, 22, 20, 30, 0, TimeSpan.Zero), to);
    }

    [Fact]
    public void A_late_evening_meeting_belongs_to_the_local_day_not_the_utc_day()
    {
        // 23:00 Tehran on 21 March is 19:30 UTC the same day; 01:00 Tehran on 22 March is 21:30 UTC on 21 March.
        var late = Item(At(23), At(23, 30));
        var after = Item(At(1, 0, 1), At(2, 0, 1));

        Assert.Equal(Day, Layout.DayOf(late.StartUtc));
        Assert.Equal(JalaliDate.Create(1404, 1, 2), Layout.DayOf(after.StartUtc));
        Assert.Single(Layout.ItemsOn([late, after], Day));
        Assert.Single(Layout.ItemsOn([late, after], JalaliDate.Create(1404, 1, 2)));
    }

    [Fact]
    public void Placement_uses_local_minutes()
    {
        var placed = Layout.PlaceOn([Item(At(9), At(10, 30))], Day).Single();

        Assert.Equal(9 * 60, placed.StartMinute);
        Assert.Equal((10 * 60) + 30, placed.EndMinute);
        Assert.False(placed.ContinuesBefore);
        Assert.False(placed.ContinuesAfter);
        Assert.Equal(1, placed.LaneCount);
    }

    [Fact]
    public void A_meeting_over_midnight_is_clipped_on_both_days_and_flagged()
    {
        var overnight = Item(At(22), At(2, 0, 1));

        var first = Layout.PlaceOn([overnight], Day).Single();
        var second = Layout.PlaceOn([overnight], JalaliDate.Create(1404, 1, 2)).Single();

        Assert.Equal((22 * 60, 24 * 60, false, true), (first.StartMinute, first.EndMinute, first.ContinuesBefore, first.ContinuesAfter));
        Assert.Equal((0, 2 * 60, true, false), (second.StartMinute, second.EndMinute, second.ContinuesBefore, second.ContinuesAfter));
    }

    [Fact]
    public void Meetings_touching_the_day_border_do_not_leak_into_the_next_day()
    {
        var endsAtMidnight = Item(At(23), At(0, 0, 1));

        Assert.Single(Layout.ItemsOn([endsAtMidnight], Day));
        Assert.Empty(Layout.ItemsOn([endsAtMidnight], JalaliDate.Create(1404, 1, 2)));
    }

    [Fact]
    public void Overlapping_meetings_get_side_by_side_lanes_and_sequential_ones_do_not()
    {
        var a = Item(At(9), At(11), "a");
        var b = Item(At(10), At(12), "b");
        var c = Item(At(10, 30), At(11, 30), "c");
        var later = Item(At(14), At(15), "later");

        var placed = Layout.PlaceOn([later, c, b, a], Day).ToDictionary(p => p.Item.Title);

        Assert.Equal(3, placed["a"].LaneCount);
        Assert.Equal((0, 1, 2), (placed["a"].Lane, placed["b"].Lane, placed["c"].Lane));
        Assert.Equal(1, placed["later"].LaneCount);
        Assert.Equal(0, placed["later"].Lane);
    }

    [Fact]
    public void Lanes_are_reused_after_a_meeting_ends()
    {
        var a = Item(At(9), At(10), "a");
        var b = Item(At(9, 30), At(11), "b");
        var c = Item(At(10), At(10, 30), "c");

        var placed = Layout.PlaceOn([a, b, c], Day).ToDictionary(p => p.Item.Title);

        Assert.Equal(2, placed["a"].LaneCount);
        Assert.Equal(0, placed["c"].Lane);
    }

    [Fact]
    public void Very_short_meetings_keep_a_minimum_visible_height()
    {
        var placed = Layout.PlaceOn([Item(At(9), At(9, 5))], Day).Single();

        Assert.Equal(CalendarLayout.MinVisibleMinutes, placed.EndMinute - placed.StartMinute);
    }

    [Fact]
    public void Slot_conversion_goes_from_local_time_to_the_correct_utc_instants()
    {
        var slot = Layout.SlotAt(Day, new TimeOnly(9, 0), TimeSpan.FromHours(1));

        Assert.Equal(new DateTimeOffset(2025, 3, 21, 5, 30, 0, TimeSpan.Zero), slot.StartUtc);
        Assert.Equal(new DateTimeOffset(2025, 3, 21, 6, 30, 0, TimeSpan.Zero), slot.EndUtc);
        Assert.Equal(Day, Layout.DayOf(slot.StartUtc));
        Assert.Equal(new TimeOnly(9, 0), Layout.TimeOf(slot.StartUtc));
        Assert.Throws<ArgumentOutOfRangeException>(() => Layout.SlotAt(Day, new TimeOnly(9, 0), TimeSpan.Zero));
    }

    [Fact]
    public void Dst_zones_keep_day_windows_and_slots_correct()
    {
        // A zone with a spring-forward gap: 2025-03-09 is 23 hours long in US Eastern time.
        if (!TimeZoneInfo.TryFindSystemTimeZoneById("America/New_York", out var ny) &&
            !TimeZoneInfo.TryFindSystemTimeZoneById("Eastern Standard Time", out ny))
        {
            return;
        }

        var layout = new CalendarLayout(ny);
        var day = KscJalaliCalendarService.Instance.FromGregorian(new DateOnly(2025, 3, 9));

        var (from, to) = layout.UtcWindow(new JalaliRange(day, day));
        var slot = layout.SlotAt(day, new TimeOnly(9, 0), TimeSpan.FromHours(1));

        Assert.Equal(TimeSpan.FromHours(23), to - from);
        Assert.Equal(new TimeOnly(9, 0), layout.TimeOf(slot.StartUtc));
    }
}
