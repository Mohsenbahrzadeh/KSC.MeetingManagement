using Bunit;
using KSC.MeetingManagement.Calendar;
using KSC.MeetingManagement.Calendar.Components;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace KSC.MeetingManagement.UnitTests;

public sealed class CalendarComponentTests : BunitContext
{
    private static readonly JalaliDate Today = JalaliDate.Create(1404, 1, 10);

    public CalendarComponentTests() => Services.AddSingleton(TimeProvider.System);

    private IRenderedComponent<KscJalaliDatePicker> RenderPicker(
        JalaliDate? value, Action<JalaliDate?> onChanged, bool required = false, JalaliDate? min = null, JalaliDate? max = null) =>
        Render<KscJalaliDatePicker>(p => p
            .Add(c => c.Value, value)
            .Add(c => c.Today, Today)
            .Add(c => c.Required, required)
            .Add(c => c.Min, min)
            .Add(c => c.Max, max)
            .Add(c => c.ValueChanged, EventCallback.Factory.Create<JalaliDate?>(this, onChanged)));

    [Fact]
    public void Picker_shows_the_value_with_persian_digits_in_rtl()
    {
        var cut = RenderPicker(JalaliDate.Create(1404, 1, 5), _ => { });

        Assert.Equal("۱۴۰۴/۰۱/۰۵", cut.Find("input").GetAttribute("value"));
        Assert.Equal("rtl", cut.Find(".ksc-datepicker").GetAttribute("dir"));
    }

    [Theory]
    [InlineData("1404/01/05")]
    [InlineData("۱۴۰۴/۰۱/۰۵")]
    [InlineData("1404-1-5")]
    public void Valid_typed_input_is_accepted_and_normalised(string typed)
    {
        JalaliDate? received = null;
        var cut = RenderPicker(null, v => received = v);

        cut.Find("input").Change(typed);

        Assert.Equal(JalaliDate.Create(1404, 1, 5), received);
        Assert.Equal("۱۴۰۴/۰۱/۰۵", cut.Find("input").GetAttribute("value"));
        Assert.Empty(cut.FindAll(".ksc-datepicker-error"));
    }

    [Theory]
    [InlineData("1404/12/30")]
    [InlineData("1404/13/01")]
    [InlineData("1404/02/32")]
    [InlineData("hello")]
    [InlineData("1404/1")]
    public void Invalid_typed_input_shows_an_error_and_does_not_notify(string typed)
    {
        var calls = 0;
        var cut = RenderPicker(null, _ => calls++);

        cut.Find("input").Change(typed);

        Assert.Equal(0, calls);
        Assert.Equal("alert", cut.Find(".ksc-datepicker-error").GetAttribute("role"));
        Assert.Equal("true", cut.Find("input").GetAttribute("aria-invalid"));
    }

    [Fact]
    public void Leap_day_is_valid_only_in_leap_years()
    {
        JalaliDate? received = null;
        var cut = RenderPicker(null, v => received = v);

        cut.Find("input").Change("1403/12/30");
        Assert.Equal(JalaliDate.Create(1403, 12, 30), received);

        received = null;
        cut.Find("input").Change("1404/12/30");
        Assert.Null(received);
        Assert.NotEmpty(cut.FindAll(".ksc-datepicker-error"));
    }

    [Fact]
    public void Empty_input_clears_when_optional_and_errors_when_required()
    {
        var calls = new List<JalaliDate?>();
        var optional = RenderPicker(JalaliDate.Create(1404, 1, 5), calls.Add);
        optional.Find("input").Change(string.Empty);
        Assert.Equal([null], calls);

        calls.Clear();
        var required = RenderPicker(JalaliDate.Create(1404, 1, 5), calls.Add, required: true);
        required.Find("input").Change(" ");
        Assert.Empty(calls);
        Assert.Contains("الزامی", required.Find(".ksc-datepicker-error").TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public void Min_and_max_are_enforced_for_typing_and_for_the_grid()
    {
        var calls = 0;
        var min = JalaliDate.Create(1404, 1, 5);
        var max = JalaliDate.Create(1404, 1, 20);
        var cut = RenderPicker(JalaliDate.Create(1404, 1, 10), _ => calls++, min: min, max: max);

        cut.Find("input").Change("1404/01/04");
        Assert.Equal(0, calls);
        Assert.NotEmpty(cut.FindAll(".ksc-datepicker-error"));

        cut.Find("input").Change("1404/01/21");
        Assert.Equal(0, calls);

        cut.Find(".ksc-datepicker-toggle").Click();
        var disabled = cut.FindAll(".ksc-datepicker-day").Count(b => b.HasAttribute("disabled"));
        Assert.True(disabled > 0);
        Assert.True(cut.Find("button[aria-label='۴ فروردین ۱۴۰۴']").HasAttribute("disabled"));
        Assert.False(cut.Find("button[aria-label='۵ فروردین ۱۴۰۴']").HasAttribute("disabled"));
    }

    [Fact]
    public void Popup_shows_whole_saturday_first_weeks_and_picking_a_day_selects_it()
    {
        JalaliDate? received = null;
        var cut = RenderPicker(JalaliDate.Create(1404, 1, 5), v => received = v);

        cut.Find(".ksc-datepicker-toggle").Click();

        Assert.Equal("فروردین ۱۴۰۴", cut.Find(".ksc-datepicker-title").TextContent.Trim());
        Assert.Equal(42, cut.FindAll(".ksc-datepicker-day").Count);
        Assert.Equal("ش", cut.FindAll("th")[0].TextContent);
        Assert.Equal("ج", cut.FindAll("th")[6].TextContent);

        cut.Find("button[aria-label='۱۵ فروردین ۱۴۰۴']").Click();

        Assert.Equal(JalaliDate.Create(1404, 1, 15), received);
        Assert.Empty(cut.FindAll(".ksc-datepicker-popup"));
        Assert.Equal("۱۴۰۴/۰۱/۱۵", cut.Find("input").GetAttribute("value"));
    }

    [Fact]
    public void Popup_marks_today_and_selected_and_navigates_across_the_year_boundary()
    {
        var cut = RenderPicker(JalaliDate.Create(1404, 1, 5), _ => { });
        cut.Find(".ksc-datepicker-toggle").Click();

        Assert.Equal("true", cut.Find("button[aria-label='۵ فروردین ۱۴۰۴']").GetAttribute("aria-pressed"));
        Assert.Equal("date", cut.Find("button[aria-label='۱۰ فروردین ۱۴۰۴']").GetAttribute("aria-current"));

        cut.Find("button[aria-label='ماه قبل']").Click();
        Assert.Equal("اسفند ۱۴۰۳", cut.Find(".ksc-datepicker-title").TextContent.Trim());

        cut.Find("button[aria-label='ماه بعد']").Click();
        Assert.Equal("فروردین ۱۴۰۴", cut.Find(".ksc-datepicker-title").TextContent.Trim());
    }

    [Fact]
    public void Escape_closes_the_popup_and_clear_is_hidden_for_required_fields()
    {
        var cut = RenderPicker(JalaliDate.Create(1404, 1, 5), _ => { }, required: true);

        cut.Find(".ksc-datepicker-toggle").Click();
        Assert.Empty(cut.FindAll(".ksc-datepicker-clear"));

        cut.Find(".ksc-datepicker").KeyDown(new Microsoft.AspNetCore.Components.Web.KeyboardEventArgs { Key = "Escape" });
        Assert.Empty(cut.FindAll(".ksc-datepicker-popup"));
    }

    private IRenderedComponent<KscCalendarToolbar> RenderToolbar(CalendarView view, Action<CalendarView> onChanged) =>
        Render<KscCalendarToolbar>(p => p
            .Add(c => c.View, view)
            .Add(c => c.Today, Today)
            .Add(c => c.ViewChanged, EventCallback.Factory.Create<CalendarView>(this, onChanged)));

    [Fact]
    public void Toolbar_previous_and_next_move_the_visible_range()
    {
        CalendarView? received = null;
        var cut = RenderToolbar(new CalendarView(CalendarViewMode.Month, JalaliDate.Create(1404, 1, 15)), v => received = v);

        cut.Find(".ksc-toolbar-prev").Click();
        Assert.Equal(new CalendarView(CalendarViewMode.Month, JalaliDate.Create(1403, 12, 15)), received);
        Assert.Equal("اسفند ۱۴۰۳", cut.Find(".ksc-toolbar-title").TextContent);

        cut.Find(".ksc-toolbar-next").Click();
        Assert.Equal(JalaliDate.Create(1404, 1, 15), received!.Value.Anchor);
    }

    [Fact]
    public void Toolbar_today_jumps_to_today_keeping_the_mode()
    {
        CalendarView? received = null;
        var cut = RenderToolbar(new CalendarView(CalendarViewMode.Week, JalaliDate.Create(1403, 5, 5)), v => received = v);

        cut.Find(".ksc-toolbar-today").Click();

        Assert.Equal(new CalendarView(CalendarViewMode.Week, Today), received);
    }

    [Fact]
    public void Toolbar_view_buttons_switch_mode_and_reflect_the_active_one()
    {
        CalendarView? received = null;
        var cut = RenderToolbar(new CalendarView(CalendarViewMode.Month, Today), v => received = v);

        Assert.Equal("true", cut.FindAll(".ksc-toolbar-view")[0].GetAttribute("aria-pressed"));
        cut.FindAll(".ksc-toolbar-view").Single(b => b.TextContent == "هفته").Click();

        Assert.Equal(CalendarViewMode.Week, received!.Value.Mode);
        Assert.Equal(Today, received.Value.Anchor);
        Assert.Equal("true", cut.FindAll(".ksc-toolbar-view").Single(b => b.TextContent == "هفته").GetAttribute("aria-pressed"));
        Assert.Equal("هفته قبل", cut.Find(".ksc-toolbar-prev").GetAttribute("aria-label"));
    }

    [Fact]
    public void Toolbar_jump_to_date_uses_the_picker()
    {
        CalendarView? received = null;
        var cut = RenderToolbar(new CalendarView(CalendarViewMode.Day, Today), v => received = v);

        cut.Find(".ksc-datepicker input").Change("1405/02/03");

        Assert.Equal(new CalendarView(CalendarViewMode.Day, JalaliDate.Create(1405, 2, 3)), received);
    }
}
