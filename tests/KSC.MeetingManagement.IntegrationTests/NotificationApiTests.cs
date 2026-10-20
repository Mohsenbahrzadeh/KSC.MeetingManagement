using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using KSC.MeetingManagement.Api.Notifications;
using KSC.MeetingManagement.Application.Actions;
using KSC.MeetingManagement.Application.Meetings;
using KSC.MeetingManagement.Application.Notifications;
using KSC.MeetingManagement.Application.Participants;
using KSC.MeetingManagement.Domain.Identity;
using KSC.MeetingManagement.Domain.Notifications;
using KSC.MeetingManagement.Domain.Tenants;
using KSC.MeetingManagement.Infrastructure.Persistence;
using KSC.MeetingManagement.Infrastructure.Tenants;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace KSC.MeetingManagement.IntegrationTests;

public sealed class NotificationApiTests : IClassFixture<WebApplicationFactory<Program>>
{
    private static readonly System.Text.Json.JsonSerializerOptions Json = new(System.Text.Json.JsonSerializerDefaults.Web)
    {
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
    };

    private static readonly string PayloadKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

    private readonly Clock _clock = new(new DateTimeOffset(2026, 5, 1, 9, 0, 0, TimeSpan.Zero));
    private readonly Capture _channel = new();
    private readonly Tenant _tenant = new(Guid.NewGuid(), "a", "A", "UTC");
    private readonly Guid _organizer = Guid.NewGuid();
    private readonly Guid _member = Guid.NewGuid();
    private readonly Guid _member2 = Guid.NewGuid();
    private readonly Guid _secretary = Guid.NewGuid();
    private readonly Guid _noAddress = Guid.NewGuid();
    private readonly WebApplicationFactory<Program> _baseFactory;
    private readonly WebApplicationFactory<Program> _factory;
    private readonly HttpClient _client;
    private readonly string _dbName = Guid.NewGuid().ToString();

    public NotificationApiTests(WebApplicationFactory<Program> factory)
    {
        _baseFactory = factory;
        _factory = Build(factory, configured: true);
        _client = _factory.CreateClient(new() { AllowAutoRedirect = false });
    }

    private sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;

        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class Capture : INotificationChannel
    {
        public List<OutgoingNotification> Sent { get; } = [];

        public Func<OutgoingNotification, DeliveryResult> Respond { get; set; } = _ => DeliveryResult.Delivered;

        public Task<DeliveryResult> SendAsync(OutgoingNotification notification, CancellationToken cancellationToken)
        {
            Sent.Add(notification);
            return Task.FromResult(Respond(notification));
        }
    }

    private sealed class Directory(Dictionary<Guid, Contact> contacts) : IContactDirectory
    {
        public Task<Contact?> FindAsync(Guid tenantId, Guid userId, CancellationToken cancellationToken) =>
            Task.FromResult(contacts.TryGetValue(userId, out var c) ? c : null);
    }

    private WebApplicationFactory<Program> Build(WebApplicationFactory<Program> factory, bool configured) =>
        factory.WithWebHostBuilder(b =>
        {
            if (configured)
            {
                b.UseSetting("Notifications:PayloadKey", PayloadKey);
                b.UseSetting("Notifications:PublicBaseUrl", "https://meetings.example.org");
            }

            b.UseSetting("Notifications:PollSeconds", "3600");
            b.UseSetting("Notifications:RetryBaseSeconds", "60");
            b.ConfigureServices(s =>
            {
                s.UseInMemoryKscDatabase(_dbName);
                s.AddSingleton<TimeProvider>(_clock);
                s.AddSingleton<INotificationChannel>(_channel);
                s.AddSingleton<IContactDirectory>(new Directory(new()
                {
                    [_organizer] = new Contact("organizer@example.com", "برگزارکننده"),
                    [_member] = new Contact("member@example.com", "عضو"),
                    [_member2] = new Contact("member2@example.com", "عضو دوم"),
                }));
                s.AddAuthentication(TestAuthHandler.Scheme)
                    .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(TestAuthHandler.Scheme, _ => { });
            });
        });

    private WebApplicationFactory<Program> SeedMembers(WebApplicationFactory<Program> app)
    {
        app.Services.GetRequiredService<InMemoryTenantStore>().Add(_tenant);
        var members = app.Services.GetRequiredService<InMemoryMembershipStore>();
        members.Add(new Membership(_tenant.Id, _organizer, [Roles.MeetingOrganizer]));
        members.Add(new Membership(_tenant.Id, _member, [Roles.Participant]));
        members.Add(new Membership(_tenant.Id, _member2, [Roles.Participant]));
        members.Add(new Membership(_tenant.Id, _secretary, [Roles.Secretary]));
        members.Add(new Membership(_tenant.Id, _noAddress, [Roles.Participant]));
        return app;
    }

    private HttpClient Client => _seeded ? _client : Seed();

    private bool _seeded;

    private HttpClient Seed()
    {
        SeedMembers(_factory);
        _seeded = true;
        return _client;
    }

    private Task<HttpResponseMessage> Send(HttpMethod method, string url, Guid user, object? body = null, HttpClient? client = null)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.Add("X-Tenant-Id", _tenant.Id.ToString());
        request.Headers.Add("X-Test-User", user.ToString());
        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        return (client ?? Client).SendAsync(request);
    }

    private async Task<MeetingDto> Meeting(bool scheduled = true, string title = "هیئت مدیره")
    {
        var start = _clock.Now.AddDays(2);
        var body = scheduled
            ? (object)new { title, mode = "Online", start, end = start.AddHours(1), allowConflicts = true }
            : new { title, mode = "Online" };
        var response = await Send(HttpMethod.Post, "/api/v1/meetings", _organizer, body);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<MeetingDto>(Json))!;
    }

    private async Task<List<OutboxMessage>> Outbox(WebApplicationFactory<Program>? app = null)
    {
        using var scope = (app ?? _factory).Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<KscDbContext>();
        return await db.OutboxMessages.IgnoreQueryFilters().AsNoTracking().OrderBy(m => m.CreatedAtUtc).ToListAsync();
    }

    private async Task Deliver(bool reminders = false)
    {
        Seed();
        var worker = _factory.Services.GetServices<IHostedService>().OfType<OutboxWorker>().Single();
        await worker.RunOnceAsync(reminders, CancellationToken.None);
    }

    private static string Decrypt(OutboxMessage message)
    {
        var protector = new KSC.MeetingManagement.Infrastructure.Notifications.AesGcmSecretProtector(Convert.FromBase64String(PayloadKey));
        return protector.Unprotect(message.ProtectedBody);
    }

    [Fact]
    public async Task Adding_a_member_queues_an_invitation_that_is_delivered_to_their_address()
    {
        var meeting = await Meeting(title: "بودجه ۱۴۰۵");

        var add = await Send(HttpMethod.Post, $"/api/v1/meetings/{meeting.Id}/participants/internal", _organizer, new { userId = _member });
        var queued = await Outbox();
        await Deliver();

        Assert.Equal(HttpStatusCode.Created, add.StatusCode);
        var message = Assert.Single(queued);
        Assert.Equal((NotificationKind.MeetingInvitation, RecipientType.Internal, _member), (message.Kind, message.RecipientType, message.RecipientUserId));
        var sent = Assert.Single(_channel.Sent);
        Assert.Equal(("member@example.com", "دعوت به جلسه"), (sent.ToAddress, sent.Subject));
        Assert.Contains("«بودجه ۱۴۰۵»", sent.Body, StringComparison.Ordinal);
        Assert.Equal(OutboxStatus.Sent, (await Outbox()).Single().Status);
    }

    [Fact]
    public async Task A_guest_invitation_reaches_the_guest_with_the_working_code_and_the_database_never_holds_it_in_clear()
    {
        var meeting = await Meeting();

        var response = await Send(HttpMethod.Post, $"/api/v1/meetings/{meeting.Id}/participants/guests", _organizer,
            new { name = "مهمان", email = "guest@example.com", expiresUtc = _clock.Now.AddDays(5) });
        var invite = (await response.Content.ReadFromJsonAsync<GuestInvitationDto>(Json))!;
        var queued = (await Outbox()).Single();
        await Deliver();

        Assert.DoesNotContain(invite.Token, queued.ProtectedBody, StringComparison.Ordinal);
        Assert.DoesNotContain("guest@example.com", queued.ProtectedBody, StringComparison.Ordinal);
        var sent = Assert.Single(_channel.Sent);
        Assert.Equal("guest@example.com", sent.ToAddress);
        Assert.Contains(invite.Token, sent.Body, StringComparison.Ordinal);
        Assert.Contains($"https://meetings.example.org/join/{meeting.Id}/{invite.Participant.Id}?t=", sent.Body, StringComparison.Ordinal);

        // The delivered code really admits the guest.
        var redeem = await Send(HttpMethod.Post, $"/api/v1/meetings/{meeting.Id}/participants/{invite.Participant.Id}/redeem", Guid.NewGuid(), new { token = invite.Token });
        Assert.Equal(HttpStatusCode.OK, redeem.StatusCode);
    }

    [Fact]
    public async Task Changing_the_time_notifies_the_others_but_not_the_person_who_changed_it()
    {
        var meeting = await Meeting();
        await Send(HttpMethod.Post, $"/api/v1/meetings/{meeting.Id}/participants/internal", _organizer, new { userId = _member });
        await Deliver();
        _channel.Sent.Clear();

        var start = _clock.Now.AddDays(3);
        var update = await Send(HttpMethod.Post, $"/api/v1/meetings/{meeting.Id}/reschedule", _organizer,
            new { expectedVersion = meeting.Version, start, end = start.AddHours(1), allowConflicts = true });
        await Deliver();

        Assert.Equal(HttpStatusCode.OK, update.StatusCode);
        var sent = Assert.Single(_channel.Sent);
        Assert.Equal(("member@example.com", "تغییر در جلسه"), (sent.ToAddress, sent.Subject));
    }

    [Fact]
    public async Task Cancelling_notifies_everyone_invited_with_the_reason()
    {
        var meeting = await Meeting();
        await Send(HttpMethod.Post, $"/api/v1/meetings/{meeting.Id}/participants/internal", _organizer, new { userId = _member });
        await Send(HttpMethod.Post, $"/api/v1/meetings/{meeting.Id}/participants/guests", _organizer,
            new { name = "مهمان", email = "guest@example.com", expiresUtc = _clock.Now.AddDays(5) });
        await Deliver();
        _channel.Sent.Clear();

        await Send(HttpMethod.Post, $"/api/v1/meetings/{meeting.Id}/cancel", _organizer, new { expectedVersion = meeting.Version, reason = "عدم حضور اعضا" });
        await Deliver();

        Assert.Equal(["guest@example.com", "member@example.com"], _channel.Sent.Select(n => n.ToAddress).Order());
        Assert.All(_channel.Sent, n => Assert.Contains("عدم حضور اعضا", n.Body, StringComparison.Ordinal));
        Assert.All(_channel.Sent, n => Assert.Equal("لغو جلسه", n.Subject));
    }

    [Fact]
    public async Task Changes_to_a_draft_meeting_are_not_announced()
    {
        var draft = await Meeting(scheduled: false);
        await Send(HttpMethod.Post, $"/api/v1/meetings/{draft.Id}/participants/internal", _organizer, new { userId = _member });
        await Deliver();
        _channel.Sent.Clear();

        await Send(HttpMethod.Put, $"/api/v1/meetings/{draft.Id}", _organizer, new { expectedVersion = draft.Version, title = "عنوان جدید", mode = "Online" });
        await Deliver();

        Assert.Empty(_channel.Sent);
    }

    [Fact]
    public async Task A_new_action_is_announced_to_its_owner_and_reassignment_to_the_new_owner()
    {
        var meeting = await Meeting();
        var due = DateOnly.FromDateTime(_clock.Now.UtcDateTime).AddDays(5);

        var created = (await (await Send(HttpMethod.Post, $"/api/v1/meetings/{meeting.Id}/actions", _secretary,
            new { title = "تهیه گزارش", ownerUserId = _member, dueDate = due })).Content.ReadFromJsonAsync<ActionItemDto>(Json))!;
        await Deliver();
        Assert.Equal("member@example.com", Assert.Single(_channel.Sent).ToAddress);
        _channel.Sent.Clear();

        await Send(HttpMethod.Put, $"/api/v1/meetings/{meeting.Id}/actions/{created.Id}", _secretary,
            new { expectedLockVersion = created.LockVersion, title = "تهیه گزارش", ownerUserId = _member2, dueDate = due });
        await Deliver();

        var sent = Assert.Single(_channel.Sent);
        Assert.Equal(("member2@example.com", "اقدام جدید"), (sent.ToAddress, sent.Subject));
        Assert.Contains("تهیه گزارش", sent.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_temporary_channel_failure_loses_nothing_and_the_message_is_delivered_on_a_later_attempt()
    {
        var failures = 2;
        _channel.Respond = _ => failures-- > 0 ? DeliveryResult.Temporary("smtp 421") : DeliveryResult.Delivered;
        var meeting = await Meeting();
        await Send(HttpMethod.Post, $"/api/v1/meetings/{meeting.Id}/participants/internal", _organizer, new { userId = _member });

        await Deliver();
        var afterFirst = (await Outbox()).Single();
        await Deliver();
        var tooEarly = (await Outbox()).Single();
        _clock.Now = _clock.Now.AddMinutes(1);
        await Deliver();
        var afterSecond = (await Outbox()).Single();
        _clock.Now = _clock.Now.AddMinutes(2);
        await Deliver();
        var final = (await Outbox()).Single();

        Assert.Equal((OutboxStatus.Pending, 1), (afterFirst.Status, afterFirst.Attempts));
        Assert.Equal(1, tooEarly.Attempts);
        Assert.Equal((OutboxStatus.Pending, 2), (afterSecond.Status, afterSecond.Attempts));
        Assert.Equal((OutboxStatus.Sent, 3), (final.Status, final.Attempts));
        Assert.Equal(3, _channel.Sent.Count);
    }

    [Fact]
    public async Task A_member_without_a_known_address_is_skipped_not_retried()
    {
        var meeting = await Meeting();
        await Send(HttpMethod.Post, $"/api/v1/meetings/{meeting.Id}/participants/internal", _organizer, new { userId = _noAddress });

        await Deliver();
        _clock.Now = _clock.Now.AddDays(1);
        await Deliver();

        Assert.Empty(_channel.Sent);
        Assert.Equal(OutboxStatus.Skipped, (await Outbox()).Single().Status);
    }

    [Fact]
    public async Task A_rejected_business_change_leaves_no_notification_behind()
    {
        var meeting = await Meeting();
        var stranger = Guid.NewGuid();

        var notMember = await Send(HttpMethod.Post, $"/api/v1/meetings/{meeting.Id}/participants/internal", _organizer, new { userId = stranger });
        var stale = await Send(HttpMethod.Post, $"/api/v1/meetings/{meeting.Id}/cancel", _organizer, new { expectedVersion = meeting.Version + 9, reason = "x" });
        var noReason = await Send(HttpMethod.Post, $"/api/v1/meetings/{meeting.Id}/cancel", _organizer, new { expectedVersion = meeting.Version, reason = " " });
        var badAction = await Send(HttpMethod.Post, $"/api/v1/meetings/{meeting.Id}/actions", _secretary,
            new { title = "x", ownerUserId = stranger, dueDate = DateOnly.FromDateTime(_clock.Now.UtcDateTime).AddDays(2) });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, notMember.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, noReason.StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, badAction.StatusCode);
        Assert.Empty(await Outbox());
    }

    [Fact]
    public async Task Reminders_run_through_the_worker_and_repeat_runs_add_nothing()
    {
        var meeting = await Meeting();
        await Send(HttpMethod.Post, $"/api/v1/meetings/{meeting.Id}/participants/internal", _organizer, new { userId = _member });
        await Deliver();
        _channel.Sent.Clear();
        _clock.Now = meeting.StartUtc!.Value.AddHours(-20);

        await Deliver(reminders: true);
        await Deliver(reminders: true);

        Assert.Equal(["member@example.com", "organizer@example.com"], _channel.Sent.Select(n => n.ToAddress).Order());
        Assert.All(_channel.Sent, n => Assert.Equal("یادآوری جلسه", n.Subject));
        Assert.Equal(3, (await Outbox()).Count);
    }

    [Fact]
    public async Task Without_a_payload_key_notifications_are_off_and_business_actions_still_work()
    {
        var app = Build(_baseFactory, configured: false);
        SeedMembers(app);
        var client = app.CreateClient(new() { AllowAutoRedirect = false });
        var start = _clock.Now.AddDays(2);
        var created = await Send(HttpMethod.Post, "/api/v1/meetings", _organizer, new { title = "x", mode = "Online", start, end = start.AddHours(1) }, client);
        var meeting = (await created.Content.ReadFromJsonAsync<MeetingDto>(Json))!;

        var add = await Send(HttpMethod.Post, $"/api/v1/meetings/{meeting.Id}/participants/internal", _organizer, new { userId = _member }, client);
        var guest = await Send(HttpMethod.Post, $"/api/v1/meetings/{meeting.Id}/participants/guests", _organizer,
            new { name = "g", email = "g@example.com", expiresUtc = _clock.Now.AddDays(3) }, client);

        Assert.Equal(HttpStatusCode.Created, add.StatusCode);
        Assert.Equal(HttpStatusCode.Created, guest.StatusCode);
        Assert.Empty(await Outbox(app));
    }
}
