using KSC.MeetingManagement.Application.Actions;
using KSC.MeetingManagement.Domain.Meetings;

namespace KSC.MeetingManagement.UnitTests;

public class ActionItemTests
{
    private static readonly DateTimeOffset Now = new(2026, 5, 1, 10, 0, 0, TimeSpan.Zero);
    private static readonly DateOnly Due = new(2026, 5, 10);
    private static readonly Guid Owner = Guid.NewGuid();

    private static ActionItem New(DateOnly? due = null) =>
        new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), null, "پیگیری", null, Owner, due ?? Due);

    [Fact]
    public void New_actions_are_open_with_owner_and_due_day()
    {
        var a = New();

        Assert.Equal(ActionItemStatus.Open, a.Status);
        Assert.Equal(Owner, a.OwnerUserId);
        Assert.Equal(Due, a.DueDate);
        Assert.True(a.IsActive);
    }

    [Fact]
    public void Overdue_means_active_and_strictly_past_the_due_day()
    {
        var a = New();

        Assert.False(a.IsOverdue(Due.AddDays(-1)));
        Assert.False(a.IsOverdue(Due));
        Assert.True(a.IsOverdue(Due.AddDays(1)));
    }

    [Fact]
    public void Finished_actions_are_never_overdue()
    {
        var done = New();
        done.Complete(Owner, "تمام", Now);
        var cancelled = New();
        cancelled.Cancel("لازم نیست");

        Assert.False(done.IsOverdue(Due.AddDays(30)));
        Assert.False(cancelled.IsOverdue(Due.AddDays(30)));
    }

    [Fact]
    public void Completion_records_who_when_and_the_note()
    {
        var a = New();
        a.Start();

        a.Complete(Owner, "  انجام شد  ", Now);

        Assert.Equal(ActionItemStatus.Done, a.Status);
        Assert.Equal(Now, a.CompletedAtUtc);
        Assert.Equal(Owner, a.CompletedByUserId);
        Assert.Equal("انجام شد", a.Note);
    }

    [Fact]
    public void Finished_actions_cannot_change_again()
    {
        var done = New();
        done.Complete(Owner, null, Now);
        var cancelled = New();
        cancelled.Cancel("x");

        Assert.Throws<MeetingDomainException>(() => done.Complete(Owner, null, Now));
        Assert.Throws<MeetingDomainException>(() => done.Start());
        Assert.Throws<MeetingDomainException>(() => done.Update("x", null, Owner, Due));
        Assert.Throws<MeetingDomainException>(() => done.Cancel("x"));
        Assert.Throws<MeetingDomainException>(() => cancelled.Complete(Owner, null, Now));
    }

    [Fact]
    public void Cancel_needs_a_reason_and_start_only_works_from_open()
    {
        var a = New();

        Assert.Throws<MeetingDomainException>(() => a.Cancel(" "));
        Assert.Equal(ActionItemStatus.Open, a.Status);
        Assert.Throws<MeetingDomainException>(() => a.Complete(Owner, new string('n', ActionItem.MaxNoteLength + 1), Now));
        Assert.Equal(ActionItemStatus.Open, a.Status);
        a.Start();
        Assert.Throws<MeetingDomainException>(a.Start);
        Assert.Equal(ActionItemStatus.InProgress, a.Status);
    }

    [Fact]
    public void Update_changes_fields_and_bumps_the_lock_version()
    {
        var a = New();
        var newOwner = Guid.NewGuid();

        a.Update("عنوان جدید", "شرح", newOwner, Due.AddDays(5));

        Assert.Equal((newOwner, Due.AddDays(5), "عنوان جدید", 1L), (a.OwnerUserId, a.DueDate, a.Title, a.LockVersion));
    }

    [Fact]
    public void Validation_rules()
    {
        Assert.Throws<MeetingDomainException>(() => new ActionItem(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), null, " ", null, Owner, Due));
        Assert.Throws<MeetingDomainException>(() => new ActionItem(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), null, "t", null, Guid.Empty, Due));
        Assert.Throws<MeetingDomainException>(() => new ActionItem(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.Empty, "t", null, Owner, Due));
        Assert.Throws<MeetingDomainException>(() => new ActionItem(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), null, new string('x', ActionItem.MaxTitleLength + 1), null, Owner, Due));
        Assert.Throws<MeetingDomainException>(() => New().Complete(Guid.Empty, null, Now));
    }

    private static readonly string[] ZoneIds = ["Asia/Tehran", "Iran Standard Time"];

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    [Fact]
    public void Tenant_clock_uses_the_tenant_zone_for_the_calendar_day()
    {
        var zoneId = ZoneIds.FirstOrDefault(id => TimeZoneInfo.TryFindSystemTimeZoneById(id, out _));
        if (zoneId is null)
        {
            return;
        }

        // 21:00 UTC on 20 March is already 21 March (00:30) in Tehran.
        var clock = new FixedClock(new DateTimeOffset(2026, 3, 20, 21, 0, 0, TimeSpan.Zero));

        Assert.Equal(new DateOnly(2026, 3, 21), TenantClock.Today(zoneId, clock));
        Assert.Equal(new DateOnly(2026, 3, 20), TenantClock.Today("UTC", clock));
        Assert.Equal(new DateOnly(2026, 3, 20), TenantClock.Today("No/Such_Zone", clock));
        Assert.Equal(new DateOnly(2026, 3, 20), TenantClock.Today(null, clock));
    }
}
