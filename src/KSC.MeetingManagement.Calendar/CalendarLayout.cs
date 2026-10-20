namespace KSC.MeetingManagement.Calendar;

/// <summary>An item placed inside one day column of a time grid.</summary>
/// <param name="Item">The meeting.</param>
/// <param name="StartMinute">Minutes from the start of the day (0..1440), clipped to the day.</param>
/// <param name="EndMinute">Minutes from the start of the day (0..1440), clipped to the day.</param>
/// <param name="Lane">Zero-based side-by-side lane within its overlap cluster.</param>
/// <param name="LaneCount">Number of lanes of the overlap cluster, so width = 1 / LaneCount.</param>
/// <param name="ContinuesBefore">The meeting started on an earlier day.</param>
/// <param name="ContinuesAfter">The meeting ends on a later day.</param>
public sealed record PlacedItem(
    CalendarItem Item, int StartMinute, int EndMinute, int Lane, int LaneCount, bool ContinuesBefore, bool ContinuesAfter);

/// <summary>Pure placement logic used by all views, kept free of UI so day and time correctness can be tested directly.</summary>
public sealed class CalendarLayout(IKscJalaliCalendar calendar, TimeZoneInfo timeZone)
{
    public const int MinutesPerDay = 24 * 60;

    /// <summary>Shortest height an item is given in a time grid so very short meetings stay clickable.</summary>
    public const int MinVisibleMinutes = 20;

    public CalendarLayout(TimeZoneInfo timeZone)
        : this(KscJalaliCalendarService.Instance, timeZone)
    {
    }

    public TimeZoneInfo TimeZone { get; } = timeZone ?? throw new ArgumentNullException(nameof(timeZone));

    /// <summary>The UTC window [from, to) covering every day of the range in the display zone; this is what a provider is asked for.</summary>
    public (DateTimeOffset FromUtc, DateTimeOffset ToUtc) UtcWindow(JalaliRange range) =>
        (calendar.DayBoundsUtc(range.Start, TimeZone).StartUtc, calendar.DayBoundsUtc(range.End, TimeZone).EndUtc);

    /// <summary>Every day of the range, in order.</summary>
    public IReadOnlyList<JalaliDate> DaysOf(JalaliRange range)
    {
        var first = calendar.ToGregorian(range.Start);
        var count = calendar.ToGregorian(range.End).DayNumber - first.DayNumber + 1;
        return Enumerable.Range(0, count).Select(i => calendar.FromGregorian(first.AddDays(i))).ToList();
    }

    /// <summary>Local calendar day of an instant in the display zone.</summary>
    public JalaliDate DayOf(DateTimeOffset instantUtc) =>
        calendar.FromGregorian(DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(instantUtc, TimeZone).DateTime));

    /// <summary>Local clock time of an instant in the display zone.</summary>
    public TimeOnly TimeOf(DateTimeOffset instantUtc) =>
        TimeOnly.FromDateTime(TimeZoneInfo.ConvertTime(instantUtc, TimeZone).DateTime);

    /// <summary>Items that overlap the given day, ordered by start then end.</summary>
    public IReadOnlyList<CalendarItem> ItemsOn(IEnumerable<CalendarItem> items, JalaliDate day)
    {
        ArgumentNullException.ThrowIfNull(items);

        var (start, end) = calendar.DayBoundsUtc(day, TimeZone);
        return items
            .Where(i => i.StartUtc < end && (i.EndUtc > start || i.StartUtc == i.EndUtc && i.StartUtc >= start))
            .OrderBy(i => i.StartUtc)
            .ThenBy(i => i.EndUtc)
            .ToList();
    }

    /// <summary>Places the day's items on a minute scale and assigns side-by-side lanes to overlapping ones.</summary>
    public IReadOnlyList<PlacedItem> PlaceOn(IEnumerable<CalendarItem> items, JalaliDate day)
    {
        var (dayStart, dayEnd) = calendar.DayBoundsUtc(day, TimeZone);
        var dayMinutes = (int)Math.Round((dayEnd - dayStart).TotalMinutes);

        var raw = ItemsOn(items, day).Select(i =>
        {
            var from = (int)Math.Round((Max(i.StartUtc, dayStart) - dayStart).TotalMinutes);
            var to = (int)Math.Round((Min(i.EndUtc, dayEnd) - dayStart).TotalMinutes);
            to = Math.Max(to, from + MinVisibleMinutes);
            return (Item: i, From: from, To: Math.Min(to, Math.Max(dayMinutes, MinutesPerDay)), Before: i.StartUtc < dayStart, After: i.EndUtc > dayEnd);
        }).ToList();

        var placed = new List<PlacedItem>();
        var cluster = new List<(CalendarItem Item, int From, int To, bool Before, bool After, int Lane)>();
        var laneEnds = new List<int>();
        var clusterEnd = int.MinValue;

        void Flush()
        {
            var lanes = Math.Max(1, laneEnds.Count);
            placed.AddRange(cluster.Select(c => new PlacedItem(c.Item, c.From, c.To, c.Lane, lanes, c.Before, c.After)));
            cluster.Clear();
            laneEnds.Clear();
        }

        foreach (var r in raw)
        {
            if (cluster.Count > 0 && r.From >= clusterEnd)
            {
                Flush();
            }

            var lane = laneEnds.FindIndex(end => end <= r.From);
            if (lane < 0)
            {
                lane = laneEnds.Count;
                laneEnds.Add(r.To);
            }
            else
            {
                laneEnds[lane] = r.To;
            }

            clusterEnd = cluster.Count == 0 ? r.To : Math.Max(clusterEnd, r.To);
            cluster.Add((r.Item, r.From, r.To, r.Before, r.After, lane));
        }

        Flush();
        return placed;
    }

    /// <summary>The instants [start, start + length) of a slot on the given day starting at a local time, with no manual offset maths.</summary>
    public CalendarSlot SlotAt(JalaliDate day, TimeOnly localStart, TimeSpan length)
    {
        if (length <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(length), "Slot length must be positive.");
        }

        var local = calendar.ToGregorian(day).ToDateTime(localStart, DateTimeKind.Unspecified);
        if (TimeZone.IsInvalidTime(local))
        {
            local = local.AddHours(1);
        }

        var offset = TimeZone.IsAmbiguousTime(local) ? TimeZone.GetAmbiguousTimeOffsets(local).Max() : TimeZone.GetUtcOffset(local);
        var start = new DateTimeOffset(local, offset).ToUniversalTime();
        return new CalendarSlot(start, start + length);
    }

    /// <summary>Moves an item to another day keeping its local start time and its duration.</summary>
    public CalendarSlot MoveToDay(CalendarItem item, JalaliDate day)
    {
        ArgumentNullException.ThrowIfNull(item);
        return SlotAt(day, TimeOf(item.StartUtc), DurationOf(item));
    }

    /// <summary>Moves an item to the start of an hour on a day keeping its duration.</summary>
    public CalendarSlot MoveToHour(CalendarItem item, JalaliDate day, int hour)
    {
        ArgumentNullException.ThrowIfNull(item);
        return SlotAt(day, new TimeOnly(hour, 0), DurationOf(item));
    }

    private static TimeSpan DurationOf(CalendarItem item) =>
        item.EndUtc > item.StartUtc ? item.EndUtc - item.StartUtc : TimeSpan.FromHours(1);

    private static DateTimeOffset Max(DateTimeOffset a, DateTimeOffset b) => a >= b ? a : b;

    private static DateTimeOffset Min(DateTimeOffset a, DateTimeOffset b) => a <= b ? a : b;
}
