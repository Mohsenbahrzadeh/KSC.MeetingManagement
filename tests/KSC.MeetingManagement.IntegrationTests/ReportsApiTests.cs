using System.Net;
using System.Net.Http.Json;
using System.Text;
using KSC.MeetingManagement.Application.Actions;
using KSC.MeetingManagement.Application.Committees;
using KSC.MeetingManagement.Application.Meetings;
using KSC.MeetingManagement.Application.Participants;
using KSC.MeetingManagement.Application.Reports;
using KSC.MeetingManagement.Application.Resolutions;
using KSC.MeetingManagement.Domain.Identity;
using KSC.MeetingManagement.Domain.Meetings;
using KSC.MeetingManagement.Domain.Tenants;
using KSC.MeetingManagement.Infrastructure.Tenants;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace KSC.MeetingManagement.IntegrationTests;

public sealed class ReportsApiTests : IClassFixture<WebApplicationFactory<Program>>
{
    private static readonly System.Text.Json.JsonSerializerOptions Json = new(System.Text.Json.JsonSerializerDefaults.Web)
    {
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
    };

    private static readonly DateTimeOffset May1 = new(2026, 5, 1, 10, 0, 0, TimeSpan.Zero);
    private const string May = "from=2026-05-01T00:00:00Z&to=2026-06-01T00:00:00Z";

    private readonly Clock _clock = new(new DateTimeOffset(2026, 4, 20, 9, 0, 0, TimeSpan.Zero));
    private readonly Tenant _tenantA = new(Guid.NewGuid(), "a", "A", "UTC");
    private readonly Tenant _tenantB = new(Guid.NewGuid(), "b", "B", "UTC");
    private readonly Guid _org1 = Guid.NewGuid();
    private readonly Guid _org2 = Guid.NewGuid();
    private readonly Guid _admin = Guid.NewGuid();
    private readonly Guid _secretary = Guid.NewGuid();
    private readonly Guid _approver = Guid.NewGuid();
    private readonly Guid _member1 = Guid.NewGuid();
    private readonly Guid _member2 = Guid.NewGuid();
    private readonly Guid _participant = Guid.NewGuid();
    private readonly Guid _auditorB = Guid.NewGuid();
    private readonly HttpClient _client;

    public ReportsApiTests(WebApplicationFactory<Program> factory)
    {
        var app = factory.WithWebHostBuilder(b =>
        {
            b.ConfigureServices(s =>
            {
                s.UseInMemoryKscDatabase(Guid.NewGuid().ToString());
                s.AddSingleton<TimeProvider>(_clock);
                s.AddAuthentication(TestAuthHandler.Scheme)
                    .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(TestAuthHandler.Scheme, _ => { });
            });
        });

        app.Services.GetRequiredService<InMemoryTenantStore>().Add(_tenantA);
        app.Services.GetRequiredService<InMemoryTenantStore>().Add(_tenantB);
        var members = app.Services.GetRequiredService<InMemoryMembershipStore>();
        members.Add(new Membership(_tenantA.Id, _org1, [Roles.MeetingOrganizer]));
        members.Add(new Membership(_tenantA.Id, _org2, [Roles.MeetingOrganizer]));
        members.Add(new Membership(_tenantA.Id, _admin, [Roles.TenantAdministrator]));
        members.Add(new Membership(_tenantA.Id, _secretary, [Roles.Secretary]));
        members.Add(new Membership(_tenantA.Id, _approver, [Roles.Approver]));
        members.Add(new Membership(_tenantA.Id, _member1, [Roles.Participant]));
        members.Add(new Membership(_tenantA.Id, _member2, [Roles.Participant]));
        members.Add(new Membership(_tenantA.Id, _participant, [Roles.Participant]));
        members.Add(new Membership(_tenantB.Id, _auditorB, [Roles.Auditor]));

        _client = app.CreateClient(new() { AllowAutoRedirect = false });
    }

    private sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;

        public override DateTimeOffset GetUtcNow() => Now;
    }

    private Task<HttpResponseMessage> Send(HttpMethod method, string url, Tenant tenant, Guid user, object? body = null)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.Add("X-Tenant-Id", tenant.Id.ToString());
        request.Headers.Add("X-Test-User", user.ToString());
        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        return _client.SendAsync(request);
    }

    private async Task<T> Get<T>(string url, Guid? user = null, Tenant? tenant = null)
    {
        var response = await Send(HttpMethod.Get, url, tenant ?? _tenantA, user ?? _admin);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<T>(Json))!;
    }

    private async Task<MeetingDto> Meeting(Guid organizer, DateTimeOffset start, string title, Guid? committee = null)
    {
        var response = await Send(HttpMethod.Post, "/api/v1/meetings", _tenantA, organizer,
            new { title, mode = "Online", start, end = start.AddHours(1), allowConflicts = true, committeeId = committee });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<MeetingDto>(Json))!;
    }

    private async Task<CommitteeDto> Committee(string name, bool active = true)
    {
        var created = (await (await Send(HttpMethod.Post, "/api/v1/committees", _tenantA, _admin, new { name })).Content.ReadFromJsonAsync<CommitteeDto>(Json))!;
        if (!active)
        {
            await Send(HttpMethod.Put, $"/api/v1/committees/{created.Id}", _tenantA, _admin, new { name, isActive = false });
        }

        return created;
    }

    private async Task Cancel(MeetingDto m) =>
        await Send(HttpMethod.Post, $"/api/v1/meetings/{m.Id}/cancel", _tenantA, _org1, new { expectedVersion = m.Version, reason = "x" });

    private sealed record Scenario(CommitteeDto C1, CommitteeDto C2, MeetingDto M1, MeetingDto M2, MeetingDto M3, MeetingDto M4, MeetingDto Outside);

    private async Task<Scenario> Build()
    {
        var c1 = await Committee("کمیته یک");
        var c2 = await Committee("کمیته دو");
        var m1 = await Meeting(_org1, May1, "M1", c1.Id);
        var m2 = await Meeting(_org1, May1.AddDays(2), "M2");
        var m3 = await Meeting(_org1, May1.AddDays(4), "M3", c1.Id);
        var m4 = await Meeting(_org2, May1.AddDays(5), "M4", c2.Id);
        var outside = await Meeting(_org1, May1.AddDays(70), "Outside");
        await Cancel(m3);
        return new Scenario(c1, c2, m1, m2, m3, m4, outside);
    }

    [Fact]
    public async Task Meetings_report_totals_and_status_counts_match_the_reference_data()
    {
        var s = await Build();

        var report = await Get<MeetingReport>($"/api/v1/reports/meetings?{May}");

        Assert.Equal(4, report.TotalCount);
        Assert.Equal(3, report.ByStatus[MeetingStatus.Scheduled]);
        Assert.Equal(1, report.ByStatus[MeetingStatus.Cancelled]);
        Assert.Equal(new[] { s.M1.Id, s.M2.Id, s.M3.Id, s.M4.Id }, report.Rows.Select(r => r.Id));
        Assert.DoesNotContain(report.Rows, r => r.Id == s.Outside.Id);
    }

    [Fact]
    public async Task Meetings_report_filters_by_status_organizer_and_committee()
    {
        var s = await Build();

        var cancelled = await Get<MeetingReport>($"/api/v1/reports/meetings?{May}&status=Cancelled");
        var byOrg2 = await Get<MeetingReport>($"/api/v1/reports/meetings?{May}&organizerId={_org2}");
        var byCommittee = await Get<MeetingReport>($"/api/v1/reports/meetings?{May}&committeeId={s.C1.Id}");
        var combined = await Get<MeetingReport>($"/api/v1/reports/meetings?{May}&committeeId={s.C1.Id}&status=Scheduled");

        Assert.Equal([s.M3.Id], cancelled.Rows.Select(r => r.Id));
        Assert.Equal([s.M4.Id], byOrg2.Rows.Select(r => r.Id));
        Assert.Equal([s.M1.Id, s.M3.Id], byCommittee.Rows.Select(r => r.Id));
        Assert.Equal([s.M1.Id], combined.Rows.Select(r => r.Id));
        Assert.All(byCommittee.Rows, r => Assert.Equal(s.C1.Id, r.CommitteeId));
    }

    [Fact]
    public async Task Paging_limits_rows_but_not_the_totals()
    {
        await Build();

        var page = await Get<MeetingReport>($"/api/v1/reports/meetings?{May}&take=2&skip=1");

        Assert.Equal(2, page.Rows.Count);
        Assert.Equal(4, page.TotalCount);
        Assert.Equal(["M2", "M3"], page.Rows.Select(r => r.Title));
    }

    [Fact]
    public async Task Reports_need_the_report_permission_and_valid_ranges()
    {
        var participant = await Send(HttpMethod.Get, $"/api/v1/reports/meetings?{May}", _tenantA, _participant);
        var organizer = await Send(HttpMethod.Get, $"/api/v1/reports/meetings?{May}", _tenantA, _org1);
        var inverted = await Send(HttpMethod.Get, "/api/v1/reports/meetings?from=2026-06-01T00:00:00Z&to=2026-05-01T00:00:00Z", _tenantA, _admin);
        var tooLong = await Send(HttpMethod.Get, "/api/v1/reports/attendance?from=2024-01-01T00:00:00Z&to=2026-01-01T00:00:00Z", _tenantA, _admin);
        var noRange = await Send(HttpMethod.Get, "/api/v1/reports/resolutions", _tenantA, _admin);

        Assert.Equal(HttpStatusCode.Forbidden, participant.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, organizer.StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, inverted.StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, tooLong.StatusCode);
        Assert.NotEqual(HttpStatusCode.OK, noRange.StatusCode);
    }

    [Fact]
    public async Task Reports_never_include_another_tenants_data()
    {
        await Build();

        var other = await Get<MeetingReport>($"/api/v1/reports/meetings?{May}", _auditorB, _tenantB);
        var attendance = await Get<List<AttendanceRow>>($"/api/v1/reports/attendance?{May}", _auditorB, _tenantB);
        var actions = await Get<ActionsSummary>("/api/v1/reports/actions", _auditorB, _tenantB);
        var resolutions = await Get<ResolutionsSummary>($"/api/v1/reports/resolutions?{May}", _auditorB, _tenantB);

        Assert.Equal(0, other.TotalCount);
        Assert.Empty(attendance);
        Assert.Equal(0, actions.Total);
        Assert.Equal(0, resolutions.Total);
    }

    [Fact]
    public async Task Csv_export_has_a_header_one_row_per_meeting_and_neutralises_formulas()
    {
        await Meeting(_org1, May1, "=HYPERLINK(\"http://evil\")");
        await Meeting(_org1, May1.AddDays(1), "عادی, با ویرگول");

        var response = await Send(HttpMethod.Get, $"/api/v1/reports/meetings?{May}&format=csv", _tenantA, _admin);
        var bytes = await response.Content.ReadAsByteArrayAsync();
        var text = Encoding.UTF8.GetString(bytes);

        Assert.Equal("text/csv", response.Content.Headers.ContentType!.MediaType);
        Assert.Equal(0xEF, bytes[0]);
        Assert.StartsWith("﻿Number,Title,Status", text, StringComparison.Ordinal);
        Assert.Equal(3, text.Split("\r\n", StringSplitOptions.RemoveEmptyEntries).Length);
        Assert.Contains("\"'=HYPERLINK(\"\"http://evil\"\")\"", text, StringComparison.Ordinal);
        Assert.Contains("\"عادی, با ویرگول\"", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Attendance_counts_match_the_invitation_data()
    {
        var m = await Meeting(_org1, May1, "حضور");
        var url = $"/api/v1/meetings/{m.Id}/participants";
        var a = (await (await Send(HttpMethod.Post, $"{url}/internal", _tenantA, _org1, new { userId = _member1 })).Content.ReadFromJsonAsync<ParticipantDto>(Json))!;
        var b = (await (await Send(HttpMethod.Post, $"{url}/internal", _tenantA, _org1, new { userId = _member2 })).Content.ReadFromJsonAsync<ParticipantDto>(Json))!;
        var c = (await (await Send(HttpMethod.Post, $"{url}/internal", _tenantA, _org1, new { userId = _participant })).Content.ReadFromJsonAsync<ParticipantDto>(Json))!;
        await Send(HttpMethod.Post, $"{url}/guests", _tenantA, _org1, new { name = "g", email = "g@example.com", expiresUtc = _clock.Now.AddDays(5) });
        await Send(HttpMethod.Post, $"{url}/{a.Id}/respond", _tenantA, _member1, new { accept = true });
        await Send(HttpMethod.Post, $"{url}/{b.Id}/respond", _tenantA, _member2, new { accept = false });
        await Send(HttpMethod.Post, $"{url}/{c.Id}/revoke", _tenantA, _org1);

        var row = (await Get<List<AttendanceRow>>($"/api/v1/reports/attendance?{May}")).Single();

        Assert.Equal((1, 1, 1, 1), (row.Invited, row.Accepted, row.Declined, row.Revoked));
        Assert.Equal(1, row.Invited);
        Assert.Equal(1, row.Guests);
        Assert.Equal(m.Id, row.MeetingId);
    }

    [Fact]
    public async Task Actions_summary_matches_the_reference_data_and_overdue_uses_the_tenant_day()
    {
        var m = await Meeting(_org1, May1, "اقدامات");
        async Task<ActionItemDto> Create(Guid owner, DateOnly due) =>
            (await (await Send(HttpMethod.Post, $"/api/v1/meetings/{m.Id}/actions", _tenantA, _secretary, new { title = "t", ownerUserId = owner, dueDate = due })).Content.ReadFromJsonAsync<ActionItemDto>(Json))!;

        var today = new DateOnly(2026, 4, 20);
        var a1 = await Create(_member1, today.AddDays(1));
        var a2 = await Create(_member1, today.AddDays(3));
        var a3 = await Create(_member2, today.AddDays(2));
        var a4 = await Create(_member2, today.AddDays(10));
        await Send(HttpMethod.Post, $"/api/v1/meetings/{m.Id}/actions/{a3.Id}/complete", _tenantA, _member2, new { expectedLockVersion = a3.LockVersion });
        await Send(HttpMethod.Post, $"/api/v1/meetings/{m.Id}/actions/{a4.Id}/start", _tenantA, _member2, new { expectedLockVersion = a4.LockVersion });
        _clock.Now = _clock.Now.AddDays(5);

        var summary = await Get<ActionsSummary>("/api/v1/reports/actions");
        var windowed = await Get<ActionsSummary>("/api/v1/reports/actions?dueFrom=2026-04-21&dueTo=2026-04-22");
        var overdue = await Get<List<ActionItemDto>>("/api/v1/actions/overdue");

        Assert.Equal((4, 2, 1, 1, 0), (summary.Total, summary.Open, summary.InProgress, summary.Done, summary.Cancelled));
        Assert.Equal(overdue.Count, summary.Overdue);
        Assert.Equal([a1.Id, a2.Id], overdue.Select(a => a.Id));
        var owner1 = summary.ByOwner.Single(o => o.OwnerUserId == _member1);
        var owner2 = summary.ByOwner.Single(o => o.OwnerUserId == _member2);
        Assert.Equal((2, 2, 0), (owner1.Active, owner1.Overdue, owner1.Done));
        Assert.Equal((1, 0, 1), (owner2.Active, owner2.Overdue, owner2.Done));
        Assert.Equal(2, windowed.Total);
    }

    [Fact]
    public async Task Resolutions_summary_counts_by_status_for_meetings_in_the_range()
    {
        var inRange = await Meeting(_org1, May1, "داخل");
        var outside = await Meeting(_org1, May1.AddDays(80), "بیرون");
        async Task<ResolutionDto> Create(Guid meeting) =>
            (await (await Send(HttpMethod.Post, $"/api/v1/meetings/{meeting}/resolutions", _tenantA, _secretary, new { text = "x" })).Content.ReadFromJsonAsync<ResolutionDto>(Json))!;

        var r1 = await Create(inRange.Id);
        var r2 = await Create(inRange.Id);
        var r3 = await Create(inRange.Id);
        await Create(inRange.Id);
        await Create(outside.Id);
        await Send(HttpMethod.Post, $"/api/v1/meetings/{inRange.Id}/resolutions/{r1.Id}/adopt", _tenantA, _secretary, new { expectedLockVersion = r1.LockVersion });
        await Send(HttpMethod.Post, $"/api/v1/meetings/{inRange.Id}/resolutions/{r2.Id}/reject", _tenantA, _secretary, new { expectedLockVersion = r2.LockVersion });
        var adopted = (await (await Send(HttpMethod.Post, $"/api/v1/meetings/{inRange.Id}/resolutions/{r3.Id}/adopt", _tenantA, _secretary, new { expectedLockVersion = r3.LockVersion })).Content.ReadFromJsonAsync<ResolutionDto>(Json))!;
        await Send(HttpMethod.Post, $"/api/v1/meetings/{inRange.Id}/resolutions/{r3.Id}/revoke", _tenantA, _approver, new { expectedLockVersion = adopted.LockVersion, reason = "x" });

        var summary = await Get<ResolutionsSummary>($"/api/v1/reports/resolutions?{May}");

        Assert.Equal((4, 1, 1, 1, 1), (summary.Total, summary.Draft, summary.Adopted, summary.Rejected, summary.Revoked));
    }

    [Fact]
    public async Task A_meeting_can_be_linked_to_an_active_committee_only()
    {
        var active = await Committee("فعال");
        var inactive = await Committee("غیرفعال", active: false);
        var body = new { title = "x", mode = "Online", start = May1, end = May1.AddHours(1), allowConflicts = true };

        var ok = await Send(HttpMethod.Post, "/api/v1/meetings", _tenantA, _org1, new { body.title, body.mode, body.start, body.end, body.allowConflicts, committeeId = active.Id });
        var off = await Send(HttpMethod.Post, "/api/v1/meetings", _tenantA, _org1, new { body.title, body.mode, body.start, body.end, body.allowConflicts, committeeId = inactive.Id });
        var unknown = await Send(HttpMethod.Post, "/api/v1/meetings", _tenantA, _org1, new { body.title, body.mode, body.start, body.end, body.allowConflicts, committeeId = Guid.NewGuid() });

        Assert.Equal(HttpStatusCode.Created, ok.StatusCode);
        Assert.Equal(active.Id, (await ok.Content.ReadFromJsonAsync<MeetingDto>(Json))!.CommitteeId);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, off.StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, unknown.StatusCode);
    }

    [Fact]
    public async Task Another_tenants_committee_cannot_be_used_and_update_can_change_or_clear_it()
    {
        var committee = await Committee("الف");
        var m = await Meeting(_org1, May1, "x", committee.Id);
        var foreign = Guid.NewGuid();

        var cleared = await Send(HttpMethod.Put, $"/api/v1/meetings/{m.Id}", _tenantA, _org1,
            new { expectedVersion = m.Version, title = "x", mode = "Online", start = May1, end = May1.AddHours(1), allowConflicts = true });
        var bad = await Send(HttpMethod.Put, $"/api/v1/meetings/{m.Id}", _tenantA, _org1,
            new { expectedVersion = m.Version + 5, title = "x", mode = "Online", committeeId = foreign });

        Assert.Null((await cleared.Content.ReadFromJsonAsync<MeetingDto>(Json))!.CommitteeId);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, bad.StatusCode);
    }
}

public class ReportCsvTests
{
    [Theory]
    [InlineData("=1+1", "'=1+1")]
    [InlineData("+cmd", "'+cmd")]
    [InlineData("-2", "'-2")]
    [InlineData("@SUM", "'@SUM")]
    [InlineData("plain", "plain")]
    [InlineData("", "")]
    [InlineData("a,b", "\"a,b\"")]
    [InlineData("say \"hi\"", "\"say \"\"hi\"\"\"")]
    [InlineData("line1\nline2", "\"line1\nline2\"")]
    public void Cells_are_escaped_and_formula_safe(string input, string expected) =>
        Assert.Equal(expected, ReportCell(input));

    private static string ReportCell(string input)
    {
        var report = new MeetingReport(1, new Dictionary<MeetingStatus, int>(), [new MeetingReportRow(Guid.Empty, null, input, MeetingStatus.Draft, MeetingMode.Online, null, null, Guid.Empty, null)]);
        var line = ReportService.ToCsv(report).Split("\r\n")[1];
        var afterNumber = line[1..];
        var titleEnd = afterNumber.IndexOf(",Draft,", StringComparison.Ordinal);
        return afterNumber[..titleEnd];
    }
}
