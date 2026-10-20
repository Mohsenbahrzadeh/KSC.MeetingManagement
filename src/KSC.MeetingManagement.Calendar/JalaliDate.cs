using System.Globalization;

namespace KSC.MeetingManagement.Calendar;

/// <summary>
/// A date in the Jalali (Solar Hijri) calendar. Always valid: the only ways to create one are
/// <see cref="TryCreate"/>, <see cref="Create"/> and <see cref="TryParse"/>, which check the supported
/// year range and the real month length (including leap years).
/// </summary>
public readonly record struct JalaliDate : IComparable<JalaliDate>
{
    public const int MinYear = 1178;
    public const int MaxYear = 1633;

    private JalaliDate(int year, int month, int day)
    {
        Year = year;
        Month = month;
        Day = day;
    }

    public int Year { get; }
    public int Month { get; }
    public int Day { get; }

    public static bool TryCreate(int year, int month, int day, out JalaliDate date)
    {
        date = default;
        if (year is < MinYear or > MaxYear || month is < 1 or > 12 || day < 1 || day > KscJalaliCalendarService.Instance.DaysInMonth(year, month))
        {
            return false;
        }

        date = new JalaliDate(year, month, day);
        return true;
    }

    public static JalaliDate Create(int year, int month, int day) =>
        TryCreate(year, month, day, out var date)
            ? date
            : throw new ArgumentOutOfRangeException(nameof(day), $"{year}/{month}/{day} is not a valid Jalali date.");

    /// <summary>Parses "yyyy/MM/dd" (also "-" separators and Persian or Arabic-Indic digits).</summary>
    public static bool TryParse(string? text, out JalaliDate date)
    {
        date = default;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var parts = NormalizeDigits(text.Trim()).Split('/', '-');
        return parts.Length == 3
            && int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var y)
            && int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var m)
            && int.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out var d)
            && TryCreate(y, m, d, out date);
    }

    public int CompareTo(JalaliDate other)
    {
        var byYear = Year.CompareTo(other.Year);
        if (byYear != 0)
        {
            return byYear;
        }

        var byMonth = Month.CompareTo(other.Month);
        return byMonth != 0 ? byMonth : Day.CompareTo(other.Day);
    }

    public static bool operator <(JalaliDate left, JalaliDate right) => left.CompareTo(right) < 0;

    public static bool operator >(JalaliDate left, JalaliDate right) => left.CompareTo(right) > 0;

    public static bool operator <=(JalaliDate left, JalaliDate right) => left.CompareTo(right) <= 0;

    public static bool operator >=(JalaliDate left, JalaliDate right) => left.CompareTo(right) >= 0;

    /// <summary>Invariant "yyyy/MM/dd" with ASCII digits (storage and exchange format).</summary>
    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture, $"{Year:D4}/{Month:D2}/{Day:D2}");

    internal static string NormalizeDigits(string text)
    {
        var chars = text.ToCharArray();
        for (var i = 0; i < chars.Length; i++)
        {
            chars[i] = chars[i] switch
            {
                >= '۰' and <= '۹' => (char)('0' + (chars[i] - '۰')),
                >= '٠' and <= '٩' => (char)('0' + (chars[i] - '٠')),
                '٫' or '٬' or '،' => '/',
                _ => chars[i],
            };
        }

        return new string(chars);
    }
}

/// <summary>An inclusive range of Jalali days.</summary>
public readonly record struct JalaliRange
{
    public JalaliRange(JalaliDate start, JalaliDate end)
    {
        if (end < start)
        {
            throw new ArgumentException("Range end cannot be before its start.", nameof(end));
        }

        Start = start;
        End = end;
    }

    public JalaliDate Start { get; }
    public JalaliDate End { get; }

    public bool Contains(JalaliDate date) => date >= Start && date <= End;
}

/// <summary>A half-open Gregorian day range [Start, EndExclusive), the form used to query storage.</summary>
public readonly record struct GregorianRange
{
    public GregorianRange(DateOnly start, DateOnly endExclusive)
    {
        if (endExclusive <= start)
        {
            throw new ArgumentException("Range end must be after its start.", nameof(endExclusive));
        }

        Start = start;
        EndExclusive = endExclusive;
    }

    public DateOnly Start { get; }
    public DateOnly EndExclusive { get; }

    public int DayCount => EndExclusive.DayNumber - Start.DayNumber;
}
