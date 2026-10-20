using System.Net;
using System.Net.Http.Json;
using KSC.MeetingManagement.Application.Audit;
using KSC.MeetingManagement.Application.Chat;
using KSC.MeetingManagement.Application.Meetings;
using KSC.MeetingManagement.Domain.Identity;
using KSC.MeetingManagement.Domain.Meetings;
using KSC.MeetingManagement.Domain.Tenants;
using KSC.MeetingManagement.Infrastructure.Tenants;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace KSC.MeetingManagement.IntegrationTests;

/// <summary>TASK-051: room chat for members; people outside the room never receive its messages.</summary>
public sealed class ChatApiTests : IClassFixture<WebApplicationFactory<Program>>
{
    private static readonly System.Text.Json.JsonSerializerOptions Json = new(System.Text.Json.JsonSerializerDefaults.Web)
    {
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
    };

    private readonly WebApplicationFactory<Program> _baseFactory;
    private readonly Clock _clock = new(new DateTimeOffset(2026, 5, 1, 10, 0, 0, TimeSpan.Zero));
    private readonly DateTimeOffset _start = new(2026, 5, 1, 10, 0, 0, TimeSpan.Zero);
    private readonly Tenant _tenant = new(Guid.NewGuid(), "a", "A", "UTC");
    private readonly Tenant _tenantB = new(Guid.NewGuid(), "b", "B", "UTC");
    private readonly Guid _host = Guid.NewGuid();
    private readonly Guid _alice = Guid.NewGuid();
    private readonly Guid _bob = Guid.NewGuid();
    private readonly Guid _outsider = Guid.NewGuid();
    private readonly Guid _auditor = Guid.NewGuid();
    private readonly Guid _hostB = Guid.NewGuid();
    private readonly HttpClient _client;

    public ChatApiTests(WebApplicationFactory<Program> factory)
    {
        _baseFactory = factory;
        _client = Create(new());
    }

    private sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;

        public override DateTimeOffset GetUtcNow() => Now;
    }

    private HttpClient Create(Dictionary<string, string?> settings)
    {
        var app = _baseFactory.WithWebHostBuilder(b =>
        {
            foreach (var (key, value) in settings)
            {
                b.UseSetting(key, value);
            }

            b.ConfigureServices(s =>
            {
                s.UseInMemoryKscDatabase(Guid.NewGuid().ToString());
                s.AddSingleton<TimeProvider>(_clock);
                s.AddAuthentication(TestAuthHandler.Scheme)
                    .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(TestAuthHandler.Scheme, _ => { });
            });
        });

        app.Services.GetRequiredService<InMemoryTenantStore>().Add(_tenant);
        app.Services.GetRequiredService<InMemoryTenantStore>().Add(_tenantB);
        var members = app.Services.GetRequiredService<InMemoryMembershipStore>();
        members.Add(new Membership(_tenant.Id, _host, [Roles.MeetingOrganizer]));
        members.Add(new Membership(_tenant.Id, _alice, [Roles.Participant]));
        members.Add(new Membership(_tenant.Id, _bob, [Roles.Participant]));
        members.Add(new Membership(_tenant.Id, _outsider, [Roles.Participant]));
        members.Add(new Membership(_tenant.Id, _auditor, [Roles.Auditor]));
        members.Add(new Membership(_tenantB.Id, _hostB, [Roles.MeetingOrganizer]));
        return app.CreateClient(new() { AllowAutoRedirect = false });
    }

    private Task<HttpResponseMessage> Send(HttpMethod method, string url, Tenant tenant, Guid? user, object? body = null, HttpClient? client = null)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.Add("X-Tenant-Id", tenant.Id.ToString());
        if (user is { } u)
        {
            request.Headers.Add("X-Test-User", u.ToString());
        }

        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        return (client ?? _client).SendAsync(request);
    }

    private Task<HttpResponseMessage> Send(HttpMethod method, string url, Guid? user, object? body = null) => Send(method, url, _tenant, user, body);

    private async Task<MeetingDto> Meeting(string mode = "Online")
    {
        var response = await Send(HttpMethod.Post, "/api/v1/meetings", _host, new { title = "x", mode, start = _start, end = _start.AddHours(1), allowConflicts = true });
        var meeting = (await response.Content.ReadFromJsonAsync<MeetingDto>(Json))!;
        foreach (var member in new[] { _alice, _bob })
        {
            await Send(HttpMethod.Post, $"/api/v1/meetings/{meeting.Id}/participants/internal", _host, new { userId = member });
        }

        return meeting;
    }

    private static string Url(Guid meetingId, string suffix = "") => $"/api/v1/meetings/{meetingId}/chat{suffix}";

    private async Task<ChatMessageDto> Post(Guid meetingId, Guid user, string text)
    {
        var response = await Send(HttpMethod.Post, Url(meetingId), user, new { text });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<ChatMessageDto>(Json))!;
    }

    private async Task<List<ChatMessageDto>> Read(Guid meetingId, Guid user, string query = "")
    {
        var response = await Send(HttpMethod.Get, Url(meetingId) + query, user);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<List<ChatMessageDto>>(Json))!;
    }

    [Fact]
    public async Task Members_exchange_messages_in_order_and_can_ask_for_only_the_new_ones()
    {
        var meeting = await Meeting();

        var first = await Post(meeting.Id, _alice, "سلام");
        var second = await Post(meeting.Id, _bob, "درود");
        var third = await Post(meeting.Id, _host, "شروع کنیم");

        Assert.Equal([1L, 2L, 3L], new[] { first.Sequence, second.Sequence, third.Sequence });
        Assert.Equal(["سلام", "درود", "شروع کنیم"], (await Read(meeting.Id, _bob)).Select(m => m.Text));
        Assert.Equal(["شروع کنیم"], (await Read(meeting.Id, _alice, "?afterSequence=2")).Select(m => m.Text));
        Assert.Empty(await Read(meeting.Id, _alice, "?afterSequence=3"));
        Assert.Equal([_alice, _bob, _host], (await Read(meeting.Id, _host)).Select(m => m.SenderUserId));
    }

    [Fact]
    public async Task People_outside_the_room_can_neither_read_nor_write_nor_delete()
    {
        var meeting = await Meeting();
        var message = await Post(meeting.Id, _alice, "خصوصی");

        var read = await Send(HttpMethod.Get, Url(meeting.Id), _outsider);
        var write = await Send(HttpMethod.Post, Url(meeting.Id), _outsider, new { text = "من هم" });
        var delete = await Send(HttpMethod.Delete, Url(meeting.Id, $"/{message.Id}"), _outsider);
        var auditor = await Send(HttpMethod.Get, Url(meeting.Id), _auditor);
        var anonymous = await Send(HttpMethod.Get, Url(meeting.Id), null);

        Assert.Equal(HttpStatusCode.Forbidden, read.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, write.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, delete.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, auditor.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        Assert.DoesNotContain("خصوصی", await read.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Another_organisation_cannot_see_the_chat_at_all()
    {
        var meeting = await Meeting();
        await Post(meeting.Id, _alice, "داخلی");

        var read = await Send(HttpMethod.Get, Url(meeting.Id), _tenantB, _hostB);
        var write = await Send(HttpMethod.Post, Url(meeting.Id), _tenantB, _hostB, new { text = "x" });

        Assert.Equal(HttpStatusCode.NotFound, read.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, write.StatusCode);
    }

    [Fact]
    public async Task A_revoked_or_removed_member_loses_access_to_the_chat()
    {
        var meeting = await Meeting();
        await Post(meeting.Id, _alice, "قبل از حذف");
        var participants = (await (await Send(HttpMethod.Get, $"/api/v1/meetings/{meeting.Id}/participants", _host)).Content
            .ReadFromJsonAsync<List<KSC.MeetingManagement.Application.Participants.ParticipantDto>>(Json))!;
        var bob = participants.Single(p => p.UserId == _bob);

        await Send(HttpMethod.Post, $"/api/v1/meetings/{meeting.Id}/participants/{bob.Id}/revoke", _host);
        await Send(HttpMethod.Post, $"/api/v1/meetings/{meeting.Id}/room/participants/u-{_alice:N}/remove", _host);

        Assert.Equal(HttpStatusCode.Forbidden, (await Send(HttpMethod.Get, Url(meeting.Id), _bob)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Send(HttpMethod.Get, Url(meeting.Id), _alice)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Send(HttpMethod.Post, Url(meeting.Id), _alice, new { text = "x" })).StatusCode);
        Assert.Single(await Read(meeting.Id, _host));
    }

    [Fact]
    public async Task Messages_can_only_be_sent_while_the_room_is_open_but_history_stays_readable()
    {
        var meeting = await Meeting();
        _clock.Now = _start.AddMinutes(-30);
        var early = await Send(HttpMethod.Post, Url(meeting.Id), _alice, new { text = "زود است" });

        _clock.Now = _start;
        await Post(meeting.Id, _alice, "حالا");
        var current = (await (await Send(HttpMethod.Get, $"/api/v1/meetings/{meeting.Id}", _host)).Content.ReadFromJsonAsync<MeetingDto>(Json))!;
        var ended = await Send(HttpMethod.Post, $"/api/v1/meetings/{meeting.Id}/end", _host, new { expectedVersion = current.Version });
        var afterEnd = await Send(HttpMethod.Post, Url(meeting.Id), _alice, new { text = "دیر است" });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, early.StatusCode);
        Assert.Equal(HttpStatusCode.OK, ended.StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, afterEnd.StatusCode);
        Assert.Equal(["حالا"], (await Read(meeting.Id, _bob)).Select(m => m.Text));
    }

    [Fact]
    public async Task An_in_person_meeting_has_no_chat()
    {
        var meeting = await Meeting("InPerson");

        var write = await Send(HttpMethod.Post, Url(meeting.Id), _alice, new { text = "x" });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, write.StatusCode);
    }

    [Fact]
    public async Task Senders_delete_their_own_messages_and_the_host_can_delete_any_and_deleted_text_is_never_returned()
    {
        var meeting = await Meeting();
        var mine = await Post(meeting.Id, _alice, "پیام من");
        var bobs = await Post(meeting.Id, _bob, "پیام باب");
        var unwanted = await Post(meeting.Id, _bob, "نامناسب");

        var others = await Send(HttpMethod.Delete, Url(meeting.Id, $"/{bobs.Id}"), _alice);
        var own = await Send(HttpMethod.Delete, Url(meeting.Id, $"/{mine.Id}"), _alice);
        var moderated = await Send(HttpMethod.Delete, Url(meeting.Id, $"/{unwanted.Id}"), _host);
        var twice = await Send(HttpMethod.Delete, Url(meeting.Id, $"/{mine.Id}"), _alice);
        var missing = await Send(HttpMethod.Delete, Url(meeting.Id, $"/{Guid.NewGuid()}"), _host);
        var list = await Read(meeting.Id, _bob);

        Assert.Equal(HttpStatusCode.Forbidden, others.StatusCode);
        Assert.Equal(HttpStatusCode.OK, own.StatusCode);
        Assert.Equal(HttpStatusCode.OK, moderated.StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, twice.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        Assert.Equal(["پیام باب"], list.Where(m => m.Status == ChatMessageStatus.Visible).Select(m => m.Text));
        Assert.All(list.Where(m => m.Status == ChatMessageStatus.Deleted), m => Assert.Null(m.Text));
        Assert.Equal(3, list.Count);
    }

    [Fact]
    public async Task Invalid_messages_are_rejected_and_control_characters_are_removed()
    {
        var meeting = await Meeting();

        var blank = await Send(HttpMethod.Post, Url(meeting.Id), _alice, new { text = "  \t " });
        var tooLong = await Send(HttpMethod.Post, Url(meeting.Id), _alice, new { text = new string('x', ChatMessage.MaxTextLength + 1) });
        var cleaned = await Post(meeting.Id, _alice, "خط اول\nخط دوم\u0007\u0000");

        Assert.Equal(HttpStatusCode.UnprocessableEntity, blank.StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, tooLong.StatusCode);
        Assert.Equal("خط اول\nخط دوم", cleaned.Text);
    }

    [Fact]
    public async Task Sending_is_rate_limited_per_user()
    {
        var client = Create(new() { ["RateLimit:SensitivePerMinute"] = "3" });
        var meetingResponse = await Send(HttpMethod.Post, "/api/v1/meetings", _tenant, _host, new { title = "x", mode = "Online", start = _start, end = _start.AddHours(1) }, client);
        var meeting = (await meetingResponse.Content.ReadFromJsonAsync<MeetingDto>(Json))!;
        await Send(HttpMethod.Post, $"/api/v1/meetings/{meeting.Id}/participants/internal", _tenant, _host, new { userId = _alice }, client);
        await Send(HttpMethod.Post, $"/api/v1/meetings/{meeting.Id}/participants/internal", _tenant, _host, new { userId = _bob }, client);

        var codes = new List<HttpStatusCode>();
        for (var i = 0; i < 5; i++)
        {
            codes.Add((await Send(HttpMethod.Post, Url(meeting.Id), _tenant, _alice, new { text = "m" + i }, client)).StatusCode);
        }

        var other = await Send(HttpMethod.Post, Url(meeting.Id), _tenant, _bob, new { text = "من" }, client);

        Assert.Equal([HttpStatusCode.Created, HttpStatusCode.Created, HttpStatusCode.Created, HttpStatusCode.TooManyRequests, HttpStatusCode.TooManyRequests], codes);
        Assert.Equal(HttpStatusCode.Created, other.StatusCode);
    }

    [Fact]
    public async Task The_audit_log_records_that_a_message_was_sent_but_never_its_text()
    {
        var meeting = await Meeting();
        await Post(meeting.Id, _alice, "متن خیلی خصوصی");

        var response = await Send(HttpMethod.Get, "/api/v1/audit?action=chat.send", _auditor);
        var text = await response.Content.ReadAsStringAsync();
        var events = System.Text.Json.JsonSerializer.Deserialize<List<AuditEventDto>>(text, Json)!;

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(_alice, Assert.Single(events).ActorId);
        Assert.DoesNotContain("خیلی خصوصی", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Paging_is_capped()
    {
        var meeting = await Meeting();
        await Post(meeting.Id, _alice, "یک");
        await Post(meeting.Id, _alice, "دو");

        var one = await Read(meeting.Id, _alice, "?take=1");
        var huge = await Read(meeting.Id, _alice, "?take=1000000");
        var negative = await Read(meeting.Id, _alice, "?afterSequence=-5&take=0");

        Assert.Single(one);
        Assert.Equal(2, huge.Count);
        Assert.Single(negative);
    }
}
