using KSC.MeetingManagement.Domain.Meetings;

namespace KSC.MeetingManagement.UnitTests;

public sealed class MeetingTests
{
    private static readonly DateTimeOffset Start = new(2026, 3, 21, 9, 0, 0, TimeSpan.FromHours(3.5));

    private static Meeting NewDraft() => new(Guid.NewGuid(), Guid.NewGuid(), "Board", MeetingMode.Hybrid, Guid.NewGuid());

    private static Meeting NewScheduled()
    {
        var m = NewDraft();
        m.Schedule(Start, Start.AddHours(1));
        return m;
    }

    [Fact]
    public void New_meeting_is_draft() => Assert.Equal(MeetingStatus.Draft, NewDraft().Status);

    [Fact]
    public void Schedule_stores_times_as_utc()
    {
        var m = NewScheduled();

        Assert.Equal(MeetingStatus.Scheduled, m.Status);
        Assert.Equal(TimeSpan.Zero, m.StartUtc!.Value.Offset);
        Assert.Equal(Start.UtcDateTime, m.StartUtc.Value.UtcDateTime);
    }

    [Fact]
    public void Schedule_rejects_end_not_after_start() =>
        Assert.Throws<MeetingDomainException>(() => NewDraft().Schedule(Start, Start));

    [Fact]
    public void Scheduled_meeting_can_be_rescheduled_and_version_increases()
    {
        var m = NewScheduled();
        var before = m.Version;

        m.Schedule(Start.AddDays(1), Start.AddDays(1).AddHours(2));

        Assert.Equal(MeetingStatus.Scheduled, m.Status);
        Assert.True(m.Version > before);
    }

    [Fact]
    public void Postpone_clears_time_and_can_be_scheduled_again()
    {
        var m = NewScheduled();

        m.Postpone();
        Assert.Null(m.StartUtc);

        m.Schedule(Start.AddDays(7), Start.AddDays(7).AddHours(1));
        Assert.Equal(MeetingStatus.Scheduled, m.Status);
    }

    [Fact]
    public void Full_lifecycle_reaches_completed()
    {
        var m = NewScheduled();
        m.Start();
        m.Complete();

        Assert.Equal(MeetingStatus.Completed, m.Status);
    }

    [Fact]
    public void Cancel_requires_reason() =>
        Assert.Throws<MeetingDomainException>(() => NewScheduled().Cancel(" "));

    [Fact]
    public void Cancelled_meeting_is_terminal()
    {
        var m = NewScheduled();
        m.Cancel("No quorum");

        Assert.Equal("No quorum", m.CancellationReason);
        Assert.Throws<MeetingDomainException>(() => m.Schedule(Start, Start.AddHours(1)));
        Assert.Throws<MeetingDomainException>(m.Start);
    }

    [Fact]
    public void In_progress_meeting_cannot_be_cancelled_or_edited()
    {
        var m = NewScheduled();
        m.Start();

        Assert.Throws<MeetingDomainException>(() => m.Cancel("x"));
        Assert.Throws<MeetingDomainException>(() => m.UpdateDetails("New", null, MeetingMode.Online));
    }

    [Fact]
    public void Draft_cannot_start_directly() =>
        Assert.Throws<MeetingDomainException>(NewDraft().Start);

    [Theory]
    [InlineData(MeetingStatus.Draft, MeetingStatus.Scheduled, true)]
    [InlineData(MeetingStatus.Draft, MeetingStatus.Completed, false)]
    [InlineData(MeetingStatus.Scheduled, MeetingStatus.Postponed, true)]
    [InlineData(MeetingStatus.Postponed, MeetingStatus.InProgress, false)]
    [InlineData(MeetingStatus.Completed, MeetingStatus.Scheduled, false)]
    [InlineData(MeetingStatus.Cancelled, MeetingStatus.Scheduled, false)]
    public void Transition_table(MeetingStatus from, MeetingStatus to, bool allowed) =>
        Assert.Equal(allowed, Meeting.CanTransition(from, to));

    [Fact]
    public void Title_is_required() =>
        Assert.Throws<MeetingDomainException>(() => new Meeting(Guid.NewGuid(), Guid.NewGuid(), " ", MeetingMode.Online, Guid.NewGuid()));
}
