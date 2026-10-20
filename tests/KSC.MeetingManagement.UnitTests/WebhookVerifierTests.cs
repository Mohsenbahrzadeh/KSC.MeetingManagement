using KSC.MeetingManagement.Application.Media;
using KSC.MeetingManagement.Domain.Meetings;
using KSC.MeetingManagement.Infrastructure.Media;

namespace KSC.MeetingManagement.UnitTests;

public class WebhookEventParsingTests
{
    private static MediaWebhookEvent? Parse(string json) => LiveKitWebhookVerifier.Parse(System.Text.Encoding.UTF8.GetBytes(json));

    [Fact]
    public void A_join_event_is_read_with_room_identity_name_and_time()
    {
        var evt = Parse("""{"event":"participant_joined","id":"EV_1","createdAt":"1777629600","room":{"name":"ksc-r"},"participant":{"identity":"u-1","name":"علی"}}""");

        Assert.NotNull(evt);
        Assert.Equal((MediaEventType.ParticipantJoined, "EV_1", "ksc-r", "u-1", "علی"), (evt.Type, evt.EventId, evt.Room, evt.Identity, evt.DisplayName));
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1777629600), evt.AtUtc);
    }

    [Fact]
    public void Numeric_times_and_room_finished_without_a_participant_are_accepted()
    {
        var evt = Parse("""{"event":"room_finished","id":"EV_2","createdAt":1777629600,"room":{"name":"ksc-r"}}""");

        Assert.NotNull(evt);
        Assert.Equal((MediaEventType.RoomFinished, null), (evt.Type, evt.Identity));
    }

    [Theory]
    [InlineData("""{"event":"track_published","id":"E","createdAt":"1","room":{"name":"r"}}""")]
    [InlineData("""{"event":"participant_joined","createdAt":"1","room":{"name":"r"}}""")]
    [InlineData("""{"event":"participant_joined","id":"E","createdAt":"x","room":{"name":"r"}}""")]
    [InlineData("""{"event":"participant_joined","id":"E","createdAt":"1"}""")]
    [InlineData("[]")]
    [InlineData("not json")]
    [InlineData("")]
    public void Other_or_malformed_events_are_not_parsed(string json) => Assert.Null(Parse(json));
}

public class PresenceRecordTests
{
    private static readonly DateTimeOffset Now = new(2026, 5, 1, 9, 0, 0, TimeSpan.Zero);

    private static PresenceRecord New() => new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "u-1", "علی", Now);

    [Fact]
    public void A_stay_is_open_until_it_is_closed_once()
    {
        var p = New();

        Assert.True(p.IsOpen);
        p.Leave(Now.AddMinutes(30));
        Assert.False(p.IsOpen);
        Assert.Equal(TimeSpan.FromMinutes(30), p.Duration(Now.AddDays(1)));
        Assert.Throws<MeetingDomainException>(() => p.Leave(Now.AddMinutes(31)));
    }

    [Fact]
    public void Open_stays_are_measured_to_the_given_time_and_never_negative()
    {
        var p = New();

        Assert.Equal(TimeSpan.FromMinutes(10), p.Duration(Now.AddMinutes(10)));
        Assert.Equal(TimeSpan.Zero, p.Duration(Now.AddMinutes(-10)));
        p.Leave(Now.AddMinutes(-5));
        Assert.Equal(Now, p.LeftAtUtc);
        Assert.Equal(TimeSpan.Zero, p.Duration(Now));
    }

    [Fact]
    public void Required_values_are_checked_and_names_cleaned()
    {
        Assert.Throws<MeetingDomainException>(() => new PresenceRecord(Guid.Empty, Guid.NewGuid(), Guid.NewGuid(), "u-1", "x", Now));
        Assert.Throws<MeetingDomainException>(() => new PresenceRecord(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), " ", "x", Now));
        Assert.Equal("participant", new PresenceRecord(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "u-1", " ", Now).DisplayName);
        Assert.Equal(RoomEntry.MaxNameLength, new PresenceRecord(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "u-1", new string('a', 500), Now).DisplayName.Length);
    }

    [Theory]
    [InlineData("ksc-0123456789abcdef0123456789abcdef-fedcba9876543210fedcba9876543210", true)]
    [InlineData("ksc-0123456789abcdef0123456789abcdef", false)]
    [InlineData("ksc-0123456789ABCDEF0123456789abcdef-fedcba9876543210fedcba9876543210", false)]
    [InlineData("other-0123456789abcdef0123456789abcdef-fedcba9876543210fedcba9876543210", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Only_our_own_room_names_are_understood(string? room, bool valid) =>
        Assert.Equal(valid, PresenceService.TryParseRoom(room, out _, out _));

    [Fact]
    public void A_room_name_round_trips_to_tenant_and_meeting()
    {
        var tenant = Guid.NewGuid();
        var meeting = Guid.NewGuid();

        Assert.True(PresenceService.TryParseRoom(MediaAccessService.RoomName(tenant, meeting), out var t, out var m));
        Assert.Equal((tenant, meeting), (t, m));
    }
}
