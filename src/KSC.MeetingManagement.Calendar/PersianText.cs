using System.Globalization;

namespace KSC.MeetingManagement.Calendar;

/// <summary>Formatting helpers for showing dates and numbers to Persian-speaking users.</summary>
public static class PersianText
{
    public static string Digits(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        var chars = text.ToCharArray();
        for (var i = 0; i < chars.Length; i++)
        {
            if (chars[i] is >= '0' and <= '9')
            {
                chars[i] = (char)('۰' + (chars[i] - '0'));
            }
        }

        return new string(chars);
    }

    public static string Digits(int number) => Digits(number.ToString(CultureInfo.InvariantCulture));

    /// <summary>"۱۴۰۴/۰۱/۰۱"</summary>
    public static string Format(JalaliDate date) => Digits(date.ToString());

    /// <summary>"۱ فروردین ۱۴۰۴"</summary>
    public static string FormatLong(JalaliDate date, IKscJalaliCalendar calendar)
    {
        ArgumentNullException.ThrowIfNull(calendar);
        return $"{Digits(date.Day)} {calendar.MonthName(date.Month)} {Digits(date.Year)}";
    }
}

/// <summary>Today's date in the organisation's time zone (Iran by default), independent of the server's zone.</summary>
public static class TehranClock
{
    private static readonly TimeZoneInfo Zone = Resolve();

    public static TimeZoneInfo TimeZone => Zone;

    public static JalaliDate Today(TimeProvider? clock = null, IKscJalaliCalendar? calendar = null)
    {
        var now = TimeZoneInfo.ConvertTime((clock ?? TimeProvider.System).GetUtcNow(), Zone);
        return (calendar ?? KscJalaliCalendarService.Instance).FromGregorian(DateOnly.FromDateTime(now.DateTime));
    }

    private static TimeZoneInfo Resolve()
    {
        foreach (var id in new[] { "Asia/Tehran", "Iran Standard Time" })
        {
            if (TimeZoneInfo.TryFindSystemTimeZoneById(id, out var zone))
            {
                return zone;
            }
        }

        return TimeZoneInfo.CreateCustomTimeZone("Asia/Tehran", TimeSpan.FromHours(3.5), "Tehran", "Tehran");
    }
}
