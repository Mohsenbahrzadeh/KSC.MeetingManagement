using KSC.MeetingManagement.Calendar;
using KSC.MeetingManagement.Domain.Meetings;

namespace KSC.MeetingManagement.Web.Components.Meetings;

/// <summary>
/// Editable state of the meeting form. The user works in Jalali date and local clock time; conversion to UTC instants
/// happens only here (through <see cref="CalendarLayout"/>) so a time chosen on the calendar reaches the API unchanged.
/// </summary>
public sealed class MeetingFormModel
{
    public const int MaxTitleLength = 300;
    public const int MaxDescriptionLength = 4000;

    public string Title { get; set; } = string.Empty;

    public string? Description { get; set; }

    public MeetingMode Mode { get; set; } = MeetingMode.InPerson;

    public JalaliDate? Date { get; set; }

    public TimeSpan? StartTime { get; set; }

    public TimeSpan? EndTime { get; set; }

    public static MeetingFormModel FromSlot(CalendarSlot slot, CalendarLayout layout)
    {
        ArgumentNullException.ThrowIfNull(slot);
        ArgumentNullException.ThrowIfNull(layout);

        var model = new MeetingFormModel
        {
            Date = layout.DayOf(slot.StartUtc),
            StartTime = layout.TimeOf(slot.StartUtc).ToTimeSpan(),
            EndTime = layout.TimeOf(slot.EndUtc).ToTimeSpan(),
        };

        // The form covers a single day: a slot that runs past midnight (for example 23:00 to 24:00) ends at 23:59.
        if (layout.DayOf(slot.EndUtc) != model.Date || model.EndTime <= model.StartTime)
        {
            model.EndTime = new TimeSpan(23, 59, 0);
        }

        return model;
    }

    public static MeetingFormModel FromMeeting(string title, string? description, MeetingMode mode, DateTimeOffset? startUtc, DateTimeOffset? endUtc, CalendarLayout layout)
    {
        ArgumentNullException.ThrowIfNull(layout);

        var model = new MeetingFormModel { Title = title, Description = description, Mode = mode };
        if (startUtc is { } start && endUtc is { } end)
        {
            model.Date = layout.DayOf(start);
            model.StartTime = layout.TimeOf(start).ToTimeSpan();
            model.EndTime = layout.TimeOf(end).ToTimeSpan();
        }

        return model;
    }

    /// <summary>Field name to message. Empty when the form can be submitted.</summary>
    public IReadOnlyDictionary<string, string> Validate()
    {
        var errors = new Dictionary<string, string>();

        if (string.IsNullOrWhiteSpace(Title))
        {
            errors[nameof(Title)] = "عنوان جلسه الزامی است.";
        }
        else if (Title.Trim().Length > MaxTitleLength)
        {
            errors[nameof(Title)] = $"عنوان نباید بیشتر از {PersianText.Digits(MaxTitleLength)} نویسه باشد.";
        }

        if (Description is { } d && d.Length > MaxDescriptionLength)
        {
            errors[nameof(Description)] = $"توضیحات نباید بیشتر از {PersianText.Digits(MaxDescriptionLength)} نویسه باشد.";
        }

        if (Date is null)
        {
            errors[nameof(Date)] = "تاریخ جلسه را انتخاب کنید.";
        }

        if (StartTime is null)
        {
            errors[nameof(StartTime)] = "ساعت شروع الزامی است.";
        }

        if (EndTime is null)
        {
            errors[nameof(EndTime)] = "ساعت پایان الزامی است.";
        }

        if (StartTime is { } s && EndTime is { } e && e <= s)
        {
            errors[nameof(EndTime)] = "ساعت پایان باید بعد از ساعت شروع باشد.";
        }

        return errors;
    }

    /// <summary>The chosen interval as UTC instants. Call only after <see cref="Validate"/> returned no errors.</summary>
    public CalendarSlot ToSlot(CalendarLayout layout)
    {
        ArgumentNullException.ThrowIfNull(layout);

        if (Validate().Count > 0 || Date is not { } date || StartTime is not { } start || EndTime is not { } end)
        {
            throw new InvalidOperationException("The form is not valid.");
        }

        var first = layout.SlotAt(date, TimeOnly.FromTimeSpan(start), end - start);
        return first;
    }
}
