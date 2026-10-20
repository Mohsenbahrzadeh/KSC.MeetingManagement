using Bunit;
using KSC.MeetingManagement.Calendar;
using KSC.MeetingManagement.Calendar.Components;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace KSC.MeetingManagement.UnitTests;

public sealed class CalendarViewTests : BunitContext
{
    private static readonly TimeZoneInfo Tehran = TimeZoneInfo.CreateCustomTimeZone("t", TimeSpan.FromHours(3.5), "Tehran", "Tehran");
    private static readonly CalendarLayout Layout = new(Tehran);
    private static readonly JalaliDate Today = JalaliDate.Create(1404, 1, 10);

    public CalendarViewTests() => Services.AddSingleton(TimeProvider.System);

    // Local Tehran time on a Gregorian day, as UTC.
    private static DateTimeOffset Local(int month, int day, int hour, int minute = 0) =>
        new DateTimeOffset(2025, month, day, hour, minute, 0, TimeSpan.FromHours(3.5)).ToUniversalTime();

    private static CalendarItem Item(DateTimeOffset start, DateTimeOffset end, string title = "جلسه", CalendarItemStatus status = CalendarItemStatus.Scheduled) =>
        new(Guid.NewGuid(), title, start, end, status);

    private IRenderedComponent<KscMonthView> RenderMonth(IReadOnlyList<CalendarItem> items, Action<CalendarItem>? onItem = null, Action<CalendarSlot>? onSlot = null, Action<JalaliDate>? onDay = null) =>
        Render<KscMonthView>(p => p
            .Add(c => c.Anchor, JalaliDate.Create(1404, 1, 15))
            .Add(c => c.Layout, Layout)
            .Add(c => c.Items, items)
            .Add(c => c.Today, Today)
            .Add(c => c.OnItemSelected, EventCallback.Factory.Create<CalendarItem>(this, onItem ?? (_ => { })))
            .Add(c => c.OnSlotSelected, EventCallback.Factory.Create<CalendarSlot>(this, onSlot ?? (_ => { })))
            .Add(c => c.OnDaySelected, EventCallback.Factory.Create<JalaliDate>(this, onDay ?? (_ => { }))));

    [Fact]
    public void Month_grid_shows_six_whole_weeks_with_correct_year_boundary()
    {
        var cut = RenderMonth([]);

        var cells = cut.FindAll(".ksc-month-cell");

        Assert.Equal(42, cells.Count);
        Assert.Contains("۲۵ اسفند ۱۴۰۳", cells[0].GetAttribute("aria-label"), StringComparison.Ordinal);
        Assert.Contains("is-outside", cells[0].ClassName, StringComparison.Ordinal);
        Assert.Contains("۱ فروردین ۱۴۰۴", cells[6].GetAttribute("aria-label"), StringComparison.Ordinal);
        Assert.DoesNotContain("is-outside", cells[6].ClassName, StringComparison.Ordinal);
        Assert.Contains("۵ اردیبهشت ۱۴۰۴", cells[41].GetAttribute("aria-label"), StringComparison.Ordinal);
        Assert.Equal(7, cut.FindAll(".ksc-month-weekday").Count);
        Assert.Equal("شنبه", cut.FindAll(".ksc-month-weekday")[0].TextContent);
        Assert.Equal("جمعه", cut.FindAll(".ksc-month-weekday")[6].TextContent);
    }

    [Fact]
    public void Today_is_highlighted_and_first_of_month_shows_the_month_name()
    {
        var cut = RenderMonth([]);

        var today = cut.FindAll(".ksc-month-cell.is-today").Single();
        Assert.Contains("۱۰ فروردین ۱۴۰۴", today.GetAttribute("aria-label"), StringComparison.Ordinal);
        Assert.Contains("۱ فروردین", cut.FindAll(".ksc-month-cell")[6].QuerySelector(".ksc-month-daynum")!.TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public void A_late_evening_meeting_is_shown_on_its_local_day_only()
    {
        var late = Item(Local(3, 21, 23), Local(3, 21, 23, 30), "شبانه");
        var cut = RenderMonth([late]);

        var cells = cut.FindAll(".ksc-month-cell");

        Assert.Contains("شبانه", cells[6].TextContent, StringComparison.Ordinal);
        Assert.DoesNotContain("شبانه", cells[7].TextContent, StringComparison.Ordinal);
        Assert.Contains("۲۳:۰۰", cells[6].TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public void A_multi_day_meeting_appears_on_every_day_it_covers()
    {
        var cut = RenderMonth([Item(Local(3, 21, 22), Local(3, 23, 2), "طولانی")]);

        var withItem = cut.FindAll(".ksc-month-cell").Where(c => c.TextContent.Contains("طولانی", StringComparison.Ordinal)).ToList();

        Assert.Equal(3, withItem.Count);
    }

    [Fact]
    public void Status_is_conveyed_as_text_and_style()
    {
        var cut = RenderMonth([Item(Local(3, 22, 9), Local(3, 22, 10), "لغو", CalendarItemStatus.Cancelled)]);

        var chip = cut.Find(".ksc-chip");

        Assert.Contains("status-cancelled", chip.ClassName, StringComparison.Ordinal);
        Assert.Contains("لغوشده", chip.TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public void Busy_days_show_three_chips_and_a_more_link_that_opens_the_day()
    {
        JalaliDate? opened = null;
        var items = Enumerable.Range(0, 5).Select(i => Item(Local(3, 22, 8 + i), Local(3, 22, 9 + i), $"m{i}")).ToList();
        var cut = RenderMonth(items, onDay: d => opened = d);

        var cell = cut.FindAll(".ksc-month-cell")[7];
        Assert.Equal(3, cell.QuerySelectorAll(".ksc-chip").Length);
        var more = cell.QuerySelector(".ksc-more")!;
        Assert.Equal("+۲ بیشتر", more.TextContent);

        cut.FindAll(".ksc-month-cell")[7].QuerySelector(".ksc-more")!.Click();

        Assert.Equal(JalaliDate.Create(1404, 1, 2), opened);
    }

    [Fact]
    public void Clicking_a_meeting_selects_it_and_clicking_a_day_gives_a_correct_utc_slot()
    {
        CalendarItem? picked = null;
        CalendarSlot? slot = null;
        var item = Item(Local(3, 22, 9), Local(3, 22, 10), "x");
        var cut = RenderMonth([item], onItem: i => picked = i, onSlot: s => slot = s);

        cut.Find(".ksc-chip").Click();
        cut.FindAll(".ksc-month-cell")[6].QuerySelector(".ksc-month-daynum")!.Click();

        Assert.Equal(item, picked);
        Assert.Equal(new DateTimeOffset(2025, 3, 21, 5, 30, 0, TimeSpan.Zero), slot!.StartUtc);
        Assert.Equal(TimeSpan.FromHours(1), slot.EndUtc - slot.StartUtc);
    }

    private IRenderedComponent<KscTimeGridView> RenderGrid(int days, IReadOnlyList<CalendarItem> items, Action<CalendarSlot>? onSlot = null)
    {
        var first = JalaliDate.Create(1404, 1, 1);
        var list = Layout.DaysOf(new JalaliRange(first, KscJalaliCalendarService.Instance.FromGregorian(new DateOnly(2025, 3, 21).AddDays(days - 1))));
        return Render<KscTimeGridView>(p => p
            .Add(c => c.Days, list)
            .Add(c => c.Layout, Layout)
            .Add(c => c.Items, items)
            .Add(c => c.Today, Today)
            .Add(c => c.OnSlotSelected, EventCallback.Factory.Create<CalendarSlot>(this, onSlot ?? (_ => { }))));
    }

    [Fact]
    public void Week_grid_has_one_column_per_day_with_24_slots_each()
    {
        var cut = RenderGrid(7, []);

        Assert.Equal(7, cut.FindAll(".ksc-grid-col").Count);
        Assert.Equal(7 * 24, cut.FindAll(".ksc-grid-slot").Count);
        Assert.Equal(7, cut.FindAll(".ksc-grid-dayhead").Count);
        Assert.Equal(24, cut.FindAll(".ksc-grid-hour").Count);
    }

    [Fact]
    public void Event_position_and_size_follow_local_time()
    {
        var cut = RenderGrid(1, [Item(Local(3, 21, 9), Local(3, 21, 10, 30))]);

        var style = cut.Find(".ksc-event").GetAttribute("style")!;

        Assert.Contains("top:37.5%", style, StringComparison.Ordinal);
        Assert.Contains("height:6.25%", style, StringComparison.Ordinal);
        Assert.Contains("inline-size:100%", style, StringComparison.Ordinal);
    }

    [Fact]
    public void Overlapping_events_share_the_column_width()
    {
        var cut = RenderGrid(1, [Item(Local(3, 21, 9), Local(3, 21, 11), "a"), Item(Local(3, 21, 10), Local(3, 21, 12), "b")]);

        var styles = cut.FindAll(".ksc-event").Select(e => e.GetAttribute("style")!).ToList();

        Assert.All(styles, s => Assert.Contains("inline-size:50%", s, StringComparison.Ordinal));
        Assert.Contains(styles, s => s.Contains("inset-inline-start:0%", StringComparison.Ordinal));
        Assert.Contains(styles, s => s.Contains("inset-inline-start:50%", StringComparison.Ordinal));
    }

    [Fact]
    public void An_overnight_meeting_is_drawn_in_both_columns()
    {
        var cut = RenderGrid(2, [Item(Local(3, 21, 22), Local(3, 22, 2), "شب")]);

        var columns = cut.FindAll(".ksc-grid-col");

        Assert.Single(columns[0].QuerySelectorAll(".ksc-event"));
        Assert.Single(columns[1].QuerySelectorAll(".ksc-event"));
        Assert.Contains("continues-after", columns[0].QuerySelector(".ksc-event")!.ClassName, StringComparison.Ordinal);
        Assert.Contains("continues-before", columns[1].QuerySelector(".ksc-event")!.ClassName, StringComparison.Ordinal);
    }

    [Fact]
    public void Clicking_an_empty_hour_gives_a_one_hour_utc_slot()
    {
        CalendarSlot? slot = null;
        var cut = RenderGrid(1, [], onSlot: s => slot = s);

        cut.FindAll(".ksc-grid-slot")[14].Click();

        Assert.Equal(Local(3, 21, 14), slot!.StartUtc);
        Assert.Equal(Local(3, 21, 15), slot.EndUtc);
    }

    [Fact]
    public void Agenda_groups_meetings_by_local_day_and_shows_an_empty_state()
    {
        var items = new[] { Item(Local(3, 22, 9), Local(3, 22, 10), "الف"), Item(Local(3, 21, 23), Local(3, 21, 23, 30), "ب") };
        var range = new JalaliRange(JalaliDate.Create(1404, 1, 1), JalaliDate.Create(1404, 1, 31));

        var cut = Render<KscAgendaView>(p => p.Add(c => c.Range, range).Add(c => c.Layout, Layout).Add(c => c.Items, items));
        var empty = Render<KscAgendaView>(p => p.Add(c => c.Range, range).Add(c => c.Layout, Layout));

        var sections = cut.FindAll(".ksc-agenda-day");
        Assert.Equal(2, sections.Count);
        Assert.Contains("ب", sections[0].TextContent, StringComparison.Ordinal);
        Assert.Contains("الف", sections[1].TextContent, StringComparison.Ordinal);
        Assert.Contains("برنامه‌ریزی‌شده", sections[1].TextContent, StringComparison.Ordinal);
        Assert.Contains("جلسه‌ای در این بازه نیست", empty.Markup, StringComparison.Ordinal);
    }

    // ---- container ----

    private sealed class FakeProvider : IMeetingCalendarDataProvider
    {
        public List<(DateTimeOffset From, DateTimeOffset To, CalendarFilter Filter)> Requests { get; } = [];

        public Func<TaskCompletionSource<IReadOnlyList<CalendarItem>>?> Next { get; set; } = () => null;

        public IReadOnlyList<CalendarItem> Result { get; set; } = [];

        public bool Fail { get; set; }

        public Task<IReadOnlyList<CalendarItem>> GetItemsAsync(DateTimeOffset fromUtc, DateTimeOffset toUtc, CalendarFilter filter, CancellationToken cancellationToken)
        {
            Requests.Add((fromUtc, toUtc, filter));
            if (Fail)
            {
                throw new InvalidOperationException("secret connection string");
            }

            return Next()?.Task ?? Task.FromResult(Result);
        }
    }

    private IRenderedComponent<KscCalendar> RenderCalendar(FakeProvider provider, CalendarViewMode mode = CalendarViewMode.Month, Action<CalendarSlot>? onSlot = null) =>
        Render<KscCalendar>(p => p
            .Add(c => c.View, new CalendarView(mode, JalaliDate.Create(1404, 1, 15)))
            .Add(c => c.Provider, provider)
            .Add(c => c.TimeZone, Tehran)
            .Add(c => c.Today, Today)
            .Add(c => c.OnSlotSelected, EventCallback.Factory.Create<CalendarSlot>(this, onSlot ?? (_ => { }))));

    [Fact]
    public void Calendar_requests_exactly_the_visible_window()
    {
        var provider = new FakeProvider();

        RenderCalendar(provider);

        var request = Assert.Single(provider.Requests);
        Assert.Equal(new DateTimeOffset(2025, 3, 14, 20, 30, 0, TimeSpan.Zero), request.From);
        Assert.Equal(new DateTimeOffset(2025, 4, 25, 20, 30, 0, TimeSpan.Zero), request.To);
    }

    [Fact]
    public void Switching_view_or_month_loads_only_the_new_window()
    {
        var provider = new FakeProvider();
        var cut = RenderCalendar(provider);

        cut.Find(".ksc-toolbar-next").Click();
        cut.FindAll(".ksc-toolbar-view").Single(b => b.TextContent == "روز").Click();

        Assert.Equal(3, provider.Requests.Count);
        Assert.Equal(TimeSpan.FromDays(1), provider.Requests[2].To - provider.Requests[2].From);
        Assert.True(provider.Requests[1].From > provider.Requests[0].From);
    }

    [Fact]
    public void Week_view_requests_seven_days_saturday_to_friday()
    {
        var provider = new FakeProvider();

        RenderCalendar(provider, CalendarViewMode.Week);

        var request = Assert.Single(provider.Requests);
        Assert.Equal(TimeSpan.FromDays(7), request.To - request.From);
        Assert.Equal(new DateTimeOffset(2025, 3, 28, 20, 30, 0, TimeSpan.Zero), request.From);
    }

    [Fact]
    public void Loading_empty_and_loaded_states_are_announced()
    {
        var provider = new FakeProvider();
        var pending = new TaskCompletionSource<IReadOnlyList<CalendarItem>>();
        provider.Next = () => pending;

        var cut = RenderCalendar(provider);
        Assert.Contains("در حال بارگیری", cut.Find(".ksc-loading").TextContent, StringComparison.Ordinal);
        Assert.Equal("true", cut.Find(".ksc-calendar").GetAttribute("aria-busy"));

        pending.SetResult([]);
        cut.WaitForAssertion(() => Assert.Contains("جلسه‌ای در این بازه نیست", cut.Markup, StringComparison.Ordinal));
        Assert.Equal("false", cut.Find(".ksc-calendar").GetAttribute("aria-busy"));
    }

    [Fact]
    public void Provider_failure_shows_a_safe_message_and_retry_recovers()
    {
        var provider = new FakeProvider { Fail = true };

        var cut = RenderCalendar(provider);

        var alert = cut.Find(".ksc-error");
        Assert.Contains("بارگیری جلسات ناموفق بود", alert.TextContent, StringComparison.Ordinal);
        Assert.DoesNotContain("secret", cut.Markup, StringComparison.Ordinal);

        provider.Fail = false;
        provider.Result = [Item(Local(3, 22, 9), Local(3, 22, 10), "بعد از خطا")];
        cut.Find(".ksc-retry").Click();

        cut.WaitForAssertion(() => Assert.Contains("بعد از خطا", cut.Markup, StringComparison.Ordinal));
        Assert.Empty(cut.FindAll(".ksc-error"));
    }

    [Fact]
    public void A_late_answer_for_an_old_window_does_not_overwrite_the_current_one()
    {
        var provider = new FakeProvider();
        var first = new TaskCompletionSource<IReadOnlyList<CalendarItem>>();
        var second = new TaskCompletionSource<IReadOnlyList<CalendarItem>>();
        var queue = new Queue<TaskCompletionSource<IReadOnlyList<CalendarItem>>>([first, second]);
        provider.Next = () => queue.Dequeue();

        var cut = RenderCalendar(provider);
        cut.Find(".ksc-toolbar-next").Click();
        second.SetResult([Item(Local(4, 25, 9), Local(4, 25, 10), "جدید")]);
        first.SetResult([Item(Local(3, 22, 9), Local(3, 22, 10), "قدیمی")]);

        cut.WaitForAssertion(() => Assert.Contains("جدید", cut.Markup, StringComparison.Ordinal));
        Assert.DoesNotContain("قدیمی", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public void Status_filter_is_sent_to_the_provider()
    {
        var provider = new FakeProvider();
        var cut = RenderCalendar(provider);

        cut.Find(".ksc-filter-select").Change(((int)CalendarItemStatus.Cancelled).ToString(System.Globalization.CultureInfo.InvariantCulture));

        Assert.Equal(CalendarItemStatus.Cancelled, provider.Requests[^1].Filter.Status);
        cut.Find(".ksc-filter-select").Change(string.Empty);
        Assert.Null(provider.Requests[^1].Filter.Status);
    }

    [Fact]
    public void Items_outside_the_window_returned_by_a_provider_are_not_drawn()
    {
        var provider = new FakeProvider { Result = [Item(Local(8, 1, 9), Local(8, 1, 10), "بیرون از بازه")] };

        var cut = RenderCalendar(provider);

        Assert.DoesNotContain("بیرون از بازه", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public void Slot_selection_from_the_week_view_reaches_the_host_as_utc()
    {
        CalendarSlot? slot = null;
        var cut = RenderCalendar(new FakeProvider(), CalendarViewMode.Week, s => slot = s);

        cut.FindAll(".ksc-grid-slot")[9].Click();

        Assert.NotNull(slot);
        Assert.Equal(TimeSpan.FromHours(1), slot.EndUtc - slot.StartUtc);
        Assert.Equal(new TimeOnly(9, 0), Layout.TimeOf(slot.StartUtc));
    }

    // ---- drag and drop ----

    private IRenderedComponent<KscCalendar> RenderMovable(FakeProvider provider, Func<CalendarItem, CalendarSlot, Task<CalendarMoveResult>> mover, CalendarViewMode mode = CalendarViewMode.Month) =>
        Render<KscCalendar>(p => p
            .Add(c => c.View, new CalendarView(mode, JalaliDate.Create(1404, 1, 1)))
            .Add(c => c.Provider, provider)
            .Add(c => c.TimeZone, Tehran)
            .Add(c => c.Today, Today)
            .Add(c => c.OnItemMoved, mover));

    private static AngleSharp.Dom.IElement CellOf(IRenderedComponent<KscCalendar> cut, string label) =>
        cut.FindAll(".ksc-month-cell").Single(c => c.GetAttribute("aria-label")!.StartsWith(label, StringComparison.Ordinal));

    [Fact]
    public void Dropping_on_another_day_keeps_time_of_day_and_duration_and_waits_for_the_backend()
    {
        var item = Item(Local(3, 22, 9), Local(3, 22, 10, 30), "منتقل");
        var provider = new FakeProvider { Result = [item] };
        var pending = new TaskCompletionSource<CalendarMoveResult>();
        CalendarSlot? asked = null;
        var cut = RenderMovable(provider, (_, slot) =>
        {
            asked = slot;
            return pending.Task;
        });

        cut.Find(".ksc-chip").DragStart();
        CellOf(cut, "۵ فروردین").Drop();

        Assert.Equal(Local(3, 25, 9), asked!.StartUtc);
        Assert.Equal(TimeSpan.FromMinutes(90), asked.EndUtc - asked.StartUtc);
        Assert.Contains("منتقل", CellOf(cut, "۲ فروردین").TextContent, StringComparison.Ordinal);
        Assert.DoesNotContain("منتقل", CellOf(cut, "۵ فروردین").TextContent, StringComparison.Ordinal);
        Assert.Contains("is-pending", cut.Find(".ksc-chip").ClassName, StringComparison.Ordinal);

        provider.Result = [item with { StartUtc = asked.StartUtc, EndUtc = asked.EndUtc }];
        pending.SetResult(CalendarMoveResult.Ok);

        cut.WaitForAssertion(() => Assert.Contains("منتقل", CellOf(cut, "۵ فروردین").TextContent, StringComparison.Ordinal));
        Assert.DoesNotContain("منتقل", CellOf(cut, "۲ فروردین").TextContent, StringComparison.Ordinal);
        Assert.Contains("زمان جلسه تغییر کرد", cut.Find(".ksc-notice").TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public void A_rejected_move_leaves_the_calendar_unchanged_and_shows_the_reason()
    {
        var provider = new FakeProvider { Result = [Item(Local(3, 22, 9), Local(3, 22, 10), "ثابت")] };
        var cut = RenderMovable(provider, (_, _) => Task.FromResult(CalendarMoveResult.Fail("این زمان با جلسهٔ دیگری تداخل دارد.")));
        var requestsBefore = provider.Requests.Count;

        cut.Find(".ksc-chip").DragStart();
        CellOf(cut, "۵ فروردین").Drop();

        cut.WaitForAssertion(() => Assert.Contains("تداخل", cut.Find(".ksc-notice").TextContent, StringComparison.Ordinal));
        Assert.Equal("alert", cut.Find(".ksc-notice").GetAttribute("role"));
        Assert.Contains("ثابت", CellOf(cut, "۲ فروردین").TextContent, StringComparison.Ordinal);
        Assert.DoesNotContain("ثابت", CellOf(cut, "۵ فروردین").TextContent, StringComparison.Ordinal);
        Assert.Equal(requestsBefore, provider.Requests.Count);
        Assert.DoesNotContain("is-pending", cut.Find(".ksc-chip").ClassName, StringComparison.Ordinal);
    }

    [Fact]
    public void A_throwing_mover_shows_a_generic_error_without_details()
    {
        var provider = new FakeProvider { Result = [Item(Local(3, 22, 9), Local(3, 22, 10), "x")] };
        var cut = RenderMovable(provider, (_, _) => throw new InvalidOperationException("db password"));

        cut.Find(".ksc-chip").DragStart();
        CellOf(cut, "۵ فروردین").Drop();

        cut.WaitForAssertion(() => Assert.Contains("ناموفق", cut.Find(".ksc-notice").TextContent, StringComparison.Ordinal));
        Assert.DoesNotContain("db password", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public void Only_movable_meetings_are_draggable_and_only_when_a_mover_is_set()
    {
        var provider = new FakeProvider
        {
            Result =
            [
                Item(Local(3, 22, 9), Local(3, 22, 10), "الف", CalendarItemStatus.Scheduled),
                Item(Local(3, 22, 11), Local(3, 22, 12), "ب", CalendarItemStatus.Cancelled),
                Item(Local(3, 22, 13), Local(3, 22, 14), "ج", CalendarItemStatus.InProgress),
            ],
        };

        var movable = RenderMovable(provider, (_, _) => Task.FromResult(CalendarMoveResult.Ok));
        var chips = movable.FindAll(".ksc-chip");
        var readOnly = RenderCalendar(provider);

        Assert.Equal("true", chips[0].GetAttribute("draggable"));
        Assert.Null(chips[1].GetAttribute("draggable"));
        Assert.Null(chips[2].GetAttribute("draggable"));
        Assert.All(readOnly.FindAll(".ksc-chip"), c => Assert.Null(c.GetAttribute("draggable")));
    }

    [Fact]
    public void Dropping_a_cancelled_meeting_does_nothing()
    {
        var calls = 0;
        var provider = new FakeProvider { Result = [Item(Local(3, 22, 9), Local(3, 22, 10), "لغو", CalendarItemStatus.Cancelled)] };
        var cut = RenderMovable(provider, (_, _) =>
        {
            calls++;
            return Task.FromResult(CalendarMoveResult.Ok);
        });

        cut.Find(".ksc-chip").DragStart();
        CellOf(cut, "۵ فروردین").Drop();

        Assert.Equal(0, calls);
    }

    [Fact]
    public void Dropping_on_the_same_place_does_not_call_the_backend()
    {
        var calls = 0;
        var provider = new FakeProvider { Result = [Item(Local(3, 22, 9), Local(3, 22, 10), "هم‌جا")] };
        var cut = RenderMovable(provider, (_, _) =>
        {
            calls++;
            return Task.FromResult(CalendarMoveResult.Ok);
        });

        cut.Find(".ksc-chip").DragStart();
        CellOf(cut, "۲ فروردین").Drop();

        Assert.Equal(0, calls);
    }

    [Fact]
    public void Dropping_on_an_hour_slot_in_the_week_view_moves_to_that_hour()
    {
        var provider = new FakeProvider { Result = [Item(Local(3, 21, 9), Local(3, 21, 10, 30), "ساعتی")] };
        CalendarSlot? asked = null;
        var cut = RenderMovable(
            provider,
            (_, slot) =>
            {
                asked = slot;
                return Task.FromResult(CalendarMoveResult.Fail("نه"));
            },
            CalendarViewMode.Week);

        cut.Find(".ksc-event").DragStart();
        // Week of 1404/01/01 (Friday) is Sat 25 Esfand to Fri 1 Farvardin; column 5 is Thursday 2025-03-20.
        cut.FindAll(".ksc-grid-col")[5].QuerySelectorAll(".ksc-grid-slot")[15].Drop();

        Assert.Equal(Local(3, 20, 15), asked!.StartUtc);
        Assert.Equal(TimeSpan.FromMinutes(90), asked.EndUtc - asked.StartUtc);
    }
}
