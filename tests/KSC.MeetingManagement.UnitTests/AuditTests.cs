using KSC.MeetingManagement.Application.Audit;
using KSC.MeetingManagement.Domain.Audit;

namespace KSC.MeetingManagement.UnitTests;

public class AuditTests
{
    private static AuditEvent New(string action = "meeting.cancel", string resource = "meeting", string? detail = "status 200", string? correlation = "c1") =>
        new(Guid.NewGuid(), Guid.NewGuid(), DateTimeOffset.UtcNow, AuditActorType.User, Guid.NewGuid(), action, resource, Guid.NewGuid(), AuditOutcome.Success, correlation, detail);

    [Fact]
    public void An_event_keeps_everything_it_was_given()
    {
        var actor = Guid.NewGuid();
        var resource = Guid.NewGuid();
        var at = new DateTimeOffset(2026, 5, 1, 9, 0, 0, TimeSpan.Zero);

        var e = new AuditEvent(Guid.NewGuid(), Guid.NewGuid(), at, AuditActorType.Guest, actor, "room.join", "room", resource, AuditOutcome.Denied, "abc", "status 403");

        Assert.Equal((AuditActorType.Guest, actor, "room.join", "room", resource, AuditOutcome.Denied, "abc", "status 403", at),
            (e.ActorType, e.ActorId, e.Action, e.ResourceType, e.ResourceId, e.Outcome, e.CorrelationId, e.Detail, e.OccurredAtUtc));
    }

    [Fact]
    public void Required_fields_and_lengths_are_enforced_and_long_text_is_truncated()
    {
        Assert.Throws<ArgumentException>(() => New(action: " "));
        Assert.Throws<ArgumentException>(() => New(resource: ""));
        Assert.Throws<ArgumentException>(() => New(action: new string('a', AuditEvent.MaxActionLength + 1)));
        Assert.Throws<ArgumentException>(() => new AuditEvent(Guid.Empty, Guid.NewGuid(), DateTimeOffset.UtcNow, AuditActorType.System, null, "a", "b", null, AuditOutcome.Success, null, null));
        Assert.Throws<ArgumentException>(() => new AuditEvent(Guid.NewGuid(), Guid.Empty, DateTimeOffset.UtcNow, AuditActorType.System, null, "a", "b", null, AuditOutcome.Success, null, null));

        var e = New(detail: new string('d', 5000), correlation: new string('c', 200));

        Assert.Equal(AuditEvent.MaxDetailLength, e.Detail!.Length);
        Assert.Equal(64, e.CorrelationId!.Length);
    }

    private sealed class FakeQuery : IAuditQuery
    {
        public AuditQuery? Last { get; private set; }

        public Task<IReadOnlyList<AuditEvent>> QueryAsync(AuditQuery query, CancellationToken cancellationToken)
        {
            Last = query;
            return Task.FromResult<IReadOnlyList<AuditEvent>>([New()]);
        }
    }

    [Fact]
    public async Task Paging_is_bounded_before_reaching_storage()
    {
        var fake = new FakeQuery();
        var service = new AuditQueryService(fake);

        await service.QueryAsync(new AuditQuery(null, null, null, null, null, null, -10, 100000), CancellationToken.None);

        Assert.Equal((0, AuditQueryService.MaxPageSize), (fake.Last!.Skip, fake.Last.Take));
    }

    [Fact]
    public async Task Inverted_and_oversized_ranges_are_rejected()
    {
        var service = new AuditQueryService(new FakeQuery());
        var now = DateTimeOffset.UtcNow;

        await Assert.ThrowsAsync<ArgumentException>(() => service.QueryAsync(new AuditQuery(now, now.AddDays(-1), null, null, null, null, 0, 10), CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => service.QueryAsync(new AuditQuery(now.AddDays(-400), now, null, null, null, null, 0, 10), CancellationToken.None));
    }
}
