using KSC.MeetingManagement.Application.Actions;
using KSC.MeetingManagement.Application.Notifications;
using KSC.MeetingManagement.Application.Participants;
using KSC.MeetingManagement.Application.Tenants;
using KSC.MeetingManagement.Domain.Meetings;
using KSC.MeetingManagement.Domain.Notifications;
using KSC.MeetingManagement.Domain.Tenants;

namespace KSC.MeetingManagement.UnitTests;

public class ReminderAndTemplateTests
{
    private static readonly DateTimeOffset Now = new(2026, 5, 1, 9, 0, 0, TimeSpan.Zero);
    private static readonly Tenant TenantA = new(Guid.NewGuid(), "a", "A", "UTC");

    private sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;

        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class Outbox : IOutboxRepository
    {
        public List<OutboxMessage> Messages { get; } = [];

        public Task<bool> AddAsync(OutboxMessage message, CancellationToken cancellationToken)
        {
            if (Messages.Any(m => m.TenantId == message.TenantId && m.DedupeKey == message.DedupeKey))
            {
                return Task.FromResult(false);
            }

            Messages.Add(message);
            return Task.FromResult(true);
        }

        public Task<IReadOnlyList<OutboxMessage>> ClaimDueAsync(DateTimeOffset nowUtc, TimeSpan lease, int batchSize, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<OutboxMessage>>([]);

        public Task SaveChangesAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class Participants(List<MeetingParticipant> all) : IParticipantRepository
    {
        public Task<MeetingParticipant?> GetAsync(Guid meetingId, Guid participantId, CancellationToken cancellationToken) =>
            Task.FromResult(all.FirstOrDefault(p => p.MeetingId == meetingId && p.Id == participantId));

        public Task<IReadOnlyList<MeetingParticipant>> ListAsync(Guid meetingId, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<MeetingParticipant>>(all.Where(p => p.MeetingId == meetingId).ToList());

        public Task<bool> HasInternalAsync(Guid meetingId, Guid userId, CancellationToken cancellationToken) => Task.FromResult(false);

        public Task AddAsync(MeetingParticipant participant, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task SaveChangesAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class Source(List<Meeting> meetings, List<MeetingParticipant> participants, List<ActionItem> actions) : IReminderSource
    {
        public Task<IReadOnlyList<Meeting>> MeetingsStartingBetweenAsync(DateTimeOffset fromUtc, DateTimeOffset toUtc, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<Meeting>>(meetings.Where(m => m.StartUtc > fromUtc && m.StartUtc <= toUtc && m.Status == MeetingStatus.Scheduled).ToList());

        public Task<IReadOnlyList<MeetingParticipant>> ParticipantsOfAsync(IReadOnlyCollection<Guid> meetingIds, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<MeetingParticipant>>(participants.Where(p => meetingIds.Contains(p.MeetingId)).ToList());

        public Task<IReadOnlyList<ActionItem>> ActiveActionsDueUntilAsync(DateOnly dueUntil, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<ActionItem>>(actions.Where(a => a.IsActive && a.DueDate <= dueUntil).ToList());
    }

    private sealed class Tenants(params Tenant[] known) : ITenantStore
    {
        public Task<Tenant?> FindByIdAsync(Guid tenantId, CancellationToken cancellationToken) => Task.FromResult(known.FirstOrDefault(t => t.Id == tenantId));
    }

    private sealed class RequestTenant(Tenant? tenant) : ITenantContext
    {
        public Tenant? Tenant => tenant;

        public Guid RequiredTenantId => tenant?.Id ?? throw new TenantNotResolvedException();
    }

    private sealed class Plain : ISecretProtector
    {
        public string Protect(string plainText) => "P:" + plainText;

        public string Unprotect(string protectedText) => protectedText[2..];
    }

    private sealed class Dates : INotificationDateFormatter
    {
        public string Format(DateTimeOffset instantUtc, string? timeZoneId) => $"{instantUtc:yyyy-MM-dd HH:mm}@{timeZoneId}";
    }

    private static Meeting ScheduledMeeting(Tenant tenant, DateTimeOffset start, string title = "هیئت مدیره", Guid? organizer = null)
    {
        var m = new Meeting(Guid.NewGuid(), tenant.Id, title, MeetingMode.Online, organizer ?? Guid.NewGuid());
        m.Schedule(start, start.AddHours(1));
        return m;
    }

    private sealed class Fixture
    {
        public Clock Clock { get; } = new(Now);

        public Outbox Outbox { get; } = new();

        public List<Meeting> Meetings { get; } = [];

        public List<MeetingParticipant> Participants { get; } = [];

        public List<ActionItem> Actions { get; } = [];

        public NotificationService Service => new(Outbox, new Participants(Participants), new Plain(), new Dates(), new RequestTenant(TenantA), Clock);

        public ReminderService Reminders(params Tenant[] tenants) =>
            new(new Source(Meetings, Participants, Actions), Service, Outbox, new Tenants(tenants.Length == 0 ? [TenantA] : tenants), Clock);

        public static string Body(OutboxMessage m) => m.ProtectedBody[2..];
    }

    [Fact]
    public async Task A_meeting_within_a_day_gets_one_reminder_per_person_and_a_second_one_within_the_hour()
    {
        var f = new Fixture();
        var organizer = Guid.NewGuid();
        var member = Guid.NewGuid();
        var meeting = ScheduledMeeting(TenantA, Now.AddHours(20), organizer: organizer);
        f.Meetings.Add(meeting);
        f.Participants.Add(MeetingParticipant.Internal(Guid.NewGuid(), TenantA.Id, meeting.Id, member));
        f.Participants.Add(MeetingParticipant.Guest(Guid.NewGuid(), TenantA.Id, meeting.Id, "مهمان", "g@example.com", Now.AddDays(3), Now).Participant);

        var first = await f.Reminders().RunAsync(CancellationToken.None);
        var repeated = await f.Reminders().RunAsync(CancellationToken.None);
        f.Clock.Now = Now.AddHours(19).AddMinutes(30);
        await f.Reminders().RunAsync(CancellationToken.None);

        Assert.Equal(1, first);
        Assert.Equal(1, repeated);
        Assert.Equal(6, f.Outbox.Messages.Count);
        Assert.Equal(3, f.Outbox.Messages.Count(m => m.DedupeKey.Contains(":24h:", StringComparison.Ordinal)));
        Assert.Equal(3, f.Outbox.Messages.Count(m => m.DedupeKey.Contains(":1h:", StringComparison.Ordinal)));
        Assert.Contains(f.Outbox.Messages, m => m.RecipientType == RecipientType.Guest && m.RecipientAddress == "g@example.com");
    }

    [Fact]
    public async Task Meetings_outside_the_window_cancelled_ones_and_removed_people_get_nothing()
    {
        var f = new Fixture();
        var far = ScheduledMeeting(TenantA, Now.AddDays(3));
        var cancelled = ScheduledMeeting(TenantA, Now.AddHours(5));
        cancelled.Cancel("x");
        var near = ScheduledMeeting(TenantA, Now.AddHours(5), organizer: Guid.NewGuid());
        f.Meetings.AddRange([far, cancelled, near]);
        var declined = MeetingParticipant.Internal(Guid.NewGuid(), TenantA.Id, near.Id, Guid.NewGuid());
        declined.Respond(false);
        var revoked = MeetingParticipant.Internal(Guid.NewGuid(), TenantA.Id, near.Id, Guid.NewGuid());
        revoked.Revoke();
        var expired = MeetingParticipant.Guest(Guid.NewGuid(), TenantA.Id, near.Id, "g", "old@example.com", Now.AddMinutes(1), Now.AddMinutes(-5)).Participant;
        f.Clock.Now = Now.AddMinutes(2);
        f.Participants.AddRange([declined, revoked, expired]);

        await f.Reminders().RunAsync(CancellationToken.None);

        var message = Assert.Single(f.Outbox.Messages);
        Assert.Equal(near.OrganizerId, message.RecipientUserId);
    }

    [Fact]
    public async Task A_moved_meeting_is_reminded_again_for_its_new_time()
    {
        var f = new Fixture();
        var meeting = ScheduledMeeting(TenantA, Now.AddHours(10));
        f.Meetings.Add(meeting);
        await f.Reminders().RunAsync(CancellationToken.None);

        meeting.Schedule(Now.AddHours(12), Now.AddHours(13));
        await f.Reminders().RunAsync(CancellationToken.None);

        Assert.Equal(2, f.Outbox.Messages.Count);
    }

    [Fact]
    public async Task Actions_get_a_due_soon_reminder_once_and_an_overdue_reminder_once_per_day()
    {
        var f = new Fixture();
        var owner = Guid.NewGuid();
        var today = new DateOnly(2026, 5, 1);
        var soon = new ActionItem(Guid.NewGuid(), TenantA.Id, Guid.NewGuid(), null, "نزدیک", null, owner, today.AddDays(1));
        var late = new ActionItem(Guid.NewGuid(), TenantA.Id, Guid.NewGuid(), null, "معوق", null, owner, today.AddDays(-2));
        var future = new ActionItem(Guid.NewGuid(), TenantA.Id, Guid.NewGuid(), null, "دور", null, owner, today.AddDays(10));
        var done = new ActionItem(Guid.NewGuid(), TenantA.Id, Guid.NewGuid(), null, "تمام", null, owner, today.AddDays(-5));
        done.Complete(owner, null, Now);
        f.Actions.AddRange([soon, late, future, done]);

        await f.Reminders().RunAsync(CancellationToken.None);
        await f.Reminders().RunAsync(CancellationToken.None);
        Assert.Equal(2, f.Outbox.Messages.Count);

        f.Clock.Now = Now.AddDays(1);
        await f.Reminders().RunAsync(CancellationToken.None);

        // On the due day the action is still "due soon" (same key, no repeat); only the already overdue one is reminded again.
        Assert.Equal(3, f.Outbox.Messages.Count);
        Assert.Equal(2, f.Outbox.Messages.Count(m => m.DedupeKey.StartsWith("action-overdue:", StringComparison.Ordinal)));
        Assert.Equal(1, f.Outbox.Messages.Count(m => m.DedupeKey.StartsWith("action-due-soon:", StringComparison.Ordinal) && m.DedupeKey.Contains(soon.Id.ToString(), StringComparison.Ordinal)));
    }

    [Fact]
    public async Task Overdue_is_decided_in_each_tenants_own_time_zone()
    {
        var f = new Fixture();
        var tehran = new Tenant(Guid.NewGuid(), "t", "T", "Asia/Tehran");
        if (!TimeZoneInfo.TryFindSystemTimeZoneById("Asia/Tehran", out _) && !TimeZoneInfo.TryFindSystemTimeZoneById("Iran Standard Time", out _))
        {
            return;
        }

        var owner = Guid.NewGuid();

        // At 21:00 UTC on 30 April it is already 1 May (00:30) in Tehran, so an action due 30 April is overdue there but not in UTC.
        f.Clock.Now = new DateTimeOffset(2026, 4, 30, 21, 0, 0, TimeSpan.Zero);
        var dueApril30 = new DateOnly(2026, 4, 30);
        f.Actions.Add(new ActionItem(Guid.NewGuid(), tehran.Id, Guid.NewGuid(), null, "تهران", null, owner, dueApril30));
        f.Actions.Add(new ActionItem(Guid.NewGuid(), TenantA.Id, Guid.NewGuid(), null, "یوتی‌سی", null, owner, dueApril30));

        await f.Reminders(TenantA, tehran).RunAsync(CancellationToken.None);

        Assert.Equal(2, f.Outbox.Messages.Count);
        Assert.Single(f.Outbox.Messages, m => m.DedupeKey.StartsWith("action-overdue:", StringComparison.Ordinal));
        Assert.Single(f.Outbox.Messages, m => m.DedupeKey.StartsWith("action-due-soon:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Texts_are_persian_use_the_formatted_time_and_are_stored_protected()
    {
        var f = new Fixture();
        var meeting = ScheduledMeeting(TenantA, Now.AddHours(30), title: "بودجه");
        meeting.AssignNumber("2026-0007");
        var member = Guid.NewGuid();

        await f.Service.InvitedAsync(meeting, member, CancellationToken.None);

        var message = Assert.Single(f.Outbox.Messages);
        var body = Fixture.Body(message);
        Assert.StartsWith("P:", message.ProtectedBody, StringComparison.Ordinal);
        Assert.Equal("دعوت به جلسه", message.Subject);
        Assert.Contains("«بودجه»", body, StringComparison.Ordinal);
        Assert.Contains("2026-05-02 15:00@", body, StringComparison.Ordinal);
        Assert.Contains("2026-0007", body, StringComparison.Ordinal);
        Assert.Equal(member, message.RecipientUserId);
    }

    [Fact]
    public async Task A_guest_invitation_carries_the_personal_code_and_link_only_inside_the_protected_text()
    {
        var f = new Fixture();
        var meeting = ScheduledMeeting(TenantA, Now.AddHours(30));
        var (participant, token) = MeetingParticipant.Guest(Guid.NewGuid(), TenantA.Id, meeting.Id, "مهمان", "g@example.com", Now.AddDays(3), Now);
        var service = f.Service;
        service.PublicBaseUrl = "https://meetings.example.org/";

        await service.GuestInvitedAsync(meeting, new GuestRecipient(participant.Id, "g@example.com", "مهمان"), token, CancellationToken.None);

        var message = Assert.Single(f.Outbox.Messages);
        var body = Fixture.Body(message);
        Assert.Contains(token, body, StringComparison.Ordinal);
        Assert.Contains($"https://meetings.example.org/join/{meeting.Id}/{participant.Id}?t=", body, StringComparison.Ordinal);
        Assert.DoesNotContain(token, message.Subject, StringComparison.Ordinal);
        Assert.Equal(("g@example.com", RecipientType.Guest), (message.RecipientAddress, message.RecipientType));
    }

    [Fact]
    public async Task Changes_and_cancellations_go_to_everyone_except_the_person_who_made_them()
    {
        var f = new Fixture();
        var actor = Guid.NewGuid();
        var other = Guid.NewGuid();
        var meeting = ScheduledMeeting(TenantA, Now.AddDays(2), organizer: actor);
        f.Participants.Add(MeetingParticipant.Internal(Guid.NewGuid(), TenantA.Id, meeting.Id, other));
        f.Participants.Add(MeetingParticipant.Internal(Guid.NewGuid(), TenantA.Id, meeting.Id, actor));
        meeting.Cancel("نصاب نشد");

        await f.Service.MeetingChangedAsync(meeting, actor, CancellationToken.None);
        await f.Service.MeetingCancelledAsync(meeting, actor, CancellationToken.None);

        Assert.All(f.Outbox.Messages, m => Assert.Equal(other, m.RecipientUserId));
        Assert.Equal(2, f.Outbox.Messages.Count);
        Assert.Contains("نصاب نشد", Fixture.Body(f.Outbox.Messages.Single(m => m.Kind == NotificationKind.MeetingCancelled)), StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_same_change_is_never_queued_twice_but_a_new_version_is()
    {
        var f = new Fixture();
        var meeting = ScheduledMeeting(TenantA, Now.AddDays(2));

        await f.Service.MeetingChangedAsync(meeting, null, CancellationToken.None);
        await f.Service.MeetingChangedAsync(meeting, null, CancellationToken.None);
        meeting.UpdateDetails("عنوان جدید", null, MeetingMode.Online);
        await f.Service.MeetingChangedAsync(meeting, null, CancellationToken.None);

        Assert.Equal(2, f.Outbox.Messages.Count);
    }

    [Fact]
    public async Task When_notifications_are_not_configured_nothing_is_queued()
    {
        var f = new Fixture();
        var service = f.Service;
        service.Enabled = false;
        var meeting = ScheduledMeeting(TenantA, Now.AddDays(2));

        await service.InvitedAsync(meeting, Guid.NewGuid(), CancellationToken.None);
        await service.MeetingCancelledAsync(meeting, null, CancellationToken.None);

        Assert.Empty(f.Outbox.Messages);
    }
}
