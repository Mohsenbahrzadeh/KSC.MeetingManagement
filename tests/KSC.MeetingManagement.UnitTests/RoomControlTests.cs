using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using KSC.MeetingManagement.Application.Media;
using KSC.MeetingManagement.Domain.Meetings;
using KSC.MeetingManagement.Infrastructure.Media;

namespace KSC.MeetingManagement.UnitTests;

public class RoomEntryTests
{
    private static readonly DateTimeOffset Now = new(2026, 5, 1, 9, 0, 0, TimeSpan.Zero);
    private static readonly Guid Host = Guid.NewGuid();

    private static RoomEntry New(string name = "مهمان") => new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "g-" + Guid.NewGuid().ToString("N"), RoomEntryKind.Guest, name, Now);

    [Fact]
    public void A_newcomer_waits_and_the_host_decides_once()
    {
        var admitted = New();
        var denied = New();

        admitted.Admit(Host, Now.AddMinutes(1));
        denied.Deny(Host, Now.AddMinutes(1));

        Assert.Equal((RoomEntryStatus.Admitted, Host, Now.AddMinutes(1)), (admitted.Status, admitted.DecidedByUserId, admitted.DecidedAtUtc));
        Assert.Equal(RoomEntryStatus.Denied, denied.Status);
        Assert.Throws<MeetingDomainException>(() => admitted.Admit(Host, Now));
        Assert.Throws<MeetingDomainException>(() => denied.Admit(Host, Now));
        Assert.Throws<MeetingDomainException>(() => admitted.Deny(Host, Now));
    }

    [Fact]
    public void Removal_works_for_waiting_or_admitted_people_and_is_final()
    {
        var waiting = New();
        var inside = New();
        inside.Admit(Host, Now);
        var denied = New();
        denied.Deny(Host, Now);

        waiting.Remove(Host, Now);
        inside.Remove(Host, Now);

        Assert.All(new[] { waiting, inside }, e => Assert.Equal(RoomEntryStatus.Removed, e.Status));
        Assert.Throws<MeetingDomainException>(() => denied.Remove(Host, Now));
        Assert.Throws<MeetingDomainException>(() => inside.Remove(Host, Now));
        Assert.Throws<MeetingDomainException>(() => inside.Admit(Host, Now));
    }

    [Fact]
    public void Each_decision_changes_the_concurrency_token_and_needs_a_real_actor()
    {
        var e = New();

        Assert.Throws<MeetingDomainException>(() => e.Admit(Guid.Empty, Now));
        var before = e.DecisionVersion;
        e.Admit(Host, Now);

        Assert.True(e.DecisionVersion > before);
    }

    [Fact]
    public void Names_are_cleaned_and_identities_validated()
    {
        Assert.Equal("participant", New("   ").DisplayName);
        Assert.Equal("ab", New("a\r\nb").DisplayName);
        Assert.Equal(RoomEntry.MaxNameLength, New(new string('x', 400)).DisplayName.Length);
        Assert.Throws<MeetingDomainException>(() => new RoomEntry(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), " ", RoomEntryKind.Member, "x", Now));
        Assert.Throws<MeetingDomainException>(() => new RoomEntry(Guid.Empty, Guid.NewGuid(), Guid.NewGuid(), "u-1", RoomEntryKind.Member, "x", Now));
    }

    [Theory]
    [InlineData("u-0123456789abcdef0123456789abcdef", true)]
    [InlineData("g-0123456789abcdef0123456789abcdef", true)]
    [InlineData("x-0123456789abcdef0123456789abcdef", false)]
    [InlineData("u-0123", false)]
    [InlineData("u-0123456789ABCDEF0123456789abcdef", false)]
    [InlineData("u-0123456789abcdef0123456789abcdef/../x", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Only_well_formed_identities_are_accepted(string? identity, bool valid) =>
        Assert.Equal(valid, RoomControlService.IsValidIdentity(identity));
}

public class LiveKitRoomControlTests
{
    private const string Secret = "0123456789abcdef0123456789abcdef-secret";
    private static readonly DateTimeOffset Now = new(2026, 5, 1, 9, 0, 0, TimeSpan.Zero);

    private sealed class Clock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class Stub(HttpStatusCode status) : HttpMessageHandler
    {
        public List<(HttpRequestMessage Request, string Body)> Calls { get; } = [];

        public string ResponseBody { get; set; } = "{}";
        public Queue<string> ResponseBodies { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
            Calls.Add((request, body));
            return new HttpResponseMessage(status) { Content = new StringContent(ResponseBodies.Count > 0 ? ResponseBodies.Dequeue() : ResponseBody) };
        }
    }

    private static MediaOptions Options(string url = "wss://media.example.org", string? apiUrl = null) =>
        new() { ServerUrl = url, ApiKey = "APIkey1", ApiSecret = Secret, ApiUrl = apiUrl };

    private static byte[] Decode(string part) =>
        Convert.FromBase64String(part.Replace('-', '+').Replace('_', '/').PadRight(part.Length + ((4 - (part.Length % 4)) % 4), '='));

    [Fact]
    public async Task Closing_a_room_posts_the_twirp_call_with_a_short_admin_token_limited_to_that_room()
    {
        var stub = new Stub(HttpStatusCode.OK);
        stub.ResponseBodies.Enqueue("{\"participants\":[{\"identity\":\"u-member\"}]}");
        stub.ResponseBodies.Enqueue("{}");
        var control = new LiveKitRoomControl(new HttpClient(stub), Options(), new Clock());

        await control.CloseRoomAsync("ksc-room-1", CancellationToken.None);

        var (request, body) = Assert.Single(stub.Calls);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("https://media.example.org/twirp/livekit.RoomService/DeleteRoom", request.RequestUri!.ToString());
        Assert.Equal("application/json", request.Content!.Headers.ContentType!.MediaType);
        Assert.Equal("ksc-room-1", JsonDocument.Parse(body).RootElement.GetProperty("room").GetString());

        var token = request.Headers.Authorization!.Parameter!;
        var parts = token.Split('.');
        var claims = JsonDocument.Parse(Decode(parts[1])).RootElement;
        var expected = HMACSHA256.HashData(Encoding.UTF8.GetBytes(Secret), Encoding.ASCII.GetBytes($"{parts[0]}.{parts[1]}"));
        Assert.Equal("Bearer", request.Headers.Authorization.Scheme);
        Assert.Equal(expected, Decode(parts[2]));
        Assert.Equal("APIkey1", claims.GetProperty("iss").GetString());
        Assert.True(claims.GetProperty("video").GetProperty("roomAdmin").GetBoolean());
        Assert.Equal("ksc-room-1", claims.GetProperty("video").GetProperty("room").GetString());
        Assert.True(claims.GetProperty("exp").GetInt64() - claims.GetProperty("nbf").GetInt64() <= 60);
        Assert.DoesNotContain(Secret, token, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Removing_a_participant_names_the_room_and_identity()
    {
        var stub = new Stub(HttpStatusCode.OK);
        var control = new LiveKitRoomControl(new HttpClient(stub), Options(), new Clock());

        await control.RemoveParticipantAsync("ksc-room-1", "g-abc", CancellationToken.None);

        var (request, body) = Assert.Single(stub.Calls);
        Assert.EndsWith("/twirp/livekit.RoomService/RemoveParticipant", request.RequestUri!.AbsolutePath, StringComparison.Ordinal);
        var json = JsonDocument.Parse(body).RootElement;
        Assert.Equal(("ksc-room-1", "g-abc"), (json.GetProperty("room").GetString(), json.GetProperty("identity").GetString()));
    }

    [Fact]
    public async Task Screen_share_permission_uses_room_service_and_only_screen_share_source_changes()
    {
        var stub = new Stub(HttpStatusCode.OK);
        stub.ResponseBodies.Enqueue("{\"participants\":[{\"identity\":\"u-member\"}]}");
        stub.ResponseBodies.Enqueue("{}");
        var control = new LiveKitRoomControl(new HttpClient(stub), Options(), new Clock());

        await control.SetScreenSharePermissionAsync("ksc-room-1", "u-member", true, CancellationToken.None);

        Assert.Equal(2, stub.Calls.Count);
        var (request, body) = stub.Calls[1];
        Assert.EndsWith("/twirp/livekit.RoomService/UpdateParticipant", request.RequestUri!.AbsolutePath, StringComparison.Ordinal);
        using var json = JsonDocument.Parse(body);
        Assert.Equal("u-member", json.RootElement.GetProperty("identity").GetString());
        var sources = json.RootElement.GetProperty("permission").GetProperty("canPublishSources");
        Assert.Contains(sources.EnumerateArray(), source => source.GetString() == "SCREEN_SHARE");
        Assert.DoesNotContain(sources.EnumerateArray(), source => source.GetString() == "MICROPHONE");
        Assert.DoesNotContain(sources.EnumerateArray(), source => source.GetString() == "SCREEN_SHARE_AUDIO");
    }

    [Fact]
    public async Task Microphone_permission_preserves_other_livekit_publish_sources()
    {
        var stub = new Stub(HttpStatusCode.OK);
        stub.ResponseBodies.Enqueue("{\"participants\":[{\"identity\":\"u-member\",\"permission\":{\"canPublishSources\":[\"CAMERA\",\"SCREEN_SHARE\"]}}]}");
        stub.ResponseBodies.Enqueue("{}");
        var control = new LiveKitRoomControl(new HttpClient(stub), Options(), new Clock());

        await control.SetParticipantPublishSourcePermissionAsync("ksc-room-1", "u-member", "MICROPHONE", true, CancellationToken.None);

        using var json = JsonDocument.Parse(stub.Calls[1].Body);
        var sources = json.RootElement.GetProperty("permission").GetProperty("canPublishSources");
        Assert.Contains(sources.EnumerateArray(), source => source.GetString() == "CAMERA");
        Assert.Contains(sources.EnumerateArray(), source => source.GetString() == "MICROPHONE");
        Assert.Contains(sources.EnumerateArray(), source => source.GetString() == "SCREEN_SHARE");
    }

    [Fact]
    public async Task Muting_everyone_lists_published_audio_tracks_and_mutes_remote_participants_only()
    {
        var stub = new Stub(HttpStatusCode.OK)
        {
            ResponseBody = """
                {"participants":[
                  {"identity":"u-host","tracks":[{"sid":"host-audio","type":"AUDIO"}]},
                  {"identity":"u-member","tracks":[{"sid":"member-audio","type":"AUDIO"},{"sid":"member-video","type":"VIDEO"}]},
                  {"identity":"u-listener","tracks":[]}
                ]}
                """
        };
        var control = new LiveKitRoomControl(new HttpClient(stub), Options(), new Clock());

        await control.MuteAllParticipantsAsync("ksc-room-1", "u-host", CancellationToken.None);

        Assert.Equal(2, stub.Calls.Count);
        Assert.EndsWith("/twirp/livekit.RoomService/ListParticipants", stub.Calls[0].Request.RequestUri!.AbsolutePath, StringComparison.Ordinal);
        var (muteRequest, muteBody) = stub.Calls[1];
        Assert.EndsWith("/twirp/livekit.RoomService/MutePublishedTrack", muteRequest.RequestUri!.AbsolutePath, StringComparison.Ordinal);
        using var body = JsonDocument.Parse(muteBody);
        var json = body.RootElement;
        Assert.Equal("ksc-room-1", json.GetProperty("room").GetString());
        Assert.Equal("u-member", json.GetProperty("identity").GetString());
        Assert.Equal("member-audio", json.GetProperty("track_sid").GetString());
        Assert.True(json.GetProperty("muted").GetBoolean());
    }

    [Fact]
    public async Task A_missing_room_or_participant_is_not_an_error_but_server_errors_are()
    {
        var notFound = new LiveKitRoomControl(new HttpClient(new Stub(HttpStatusCode.NotFound)), Options(), new Clock());
        var broken = new LiveKitRoomControl(new HttpClient(new Stub(HttpStatusCode.InternalServerError)), Options(), new Clock());
        var unauthorized = new LiveKitRoomControl(new HttpClient(new Stub(HttpStatusCode.Unauthorized)), Options(), new Clock());

        await notFound.CloseRoomAsync("r", CancellationToken.None);
        await Assert.ThrowsAsync<HttpRequestException>(() => broken.CloseRoomAsync("r", CancellationToken.None));
        await Assert.ThrowsAsync<HttpRequestException>(() => unauthorized.RemoveParticipantAsync("r", "u-1", CancellationToken.None));
    }

    [Fact]
    public async Task A_missing_room_during_mute_all_is_a_no_op()
    {
        var stub = new Stub(HttpStatusCode.NotFound);
        var control = new LiveKitRoomControl(new HttpClient(stub), Options(), new Clock());

        await control.MuteAllParticipantsAsync("r", "u-host", CancellationToken.None);

        Assert.Single(stub.Calls);
    }

    [Fact]
    public async Task Without_complete_settings_nothing_is_sent()
    {
        var stub = new Stub(HttpStatusCode.OK);
        var control = new LiveKitRoomControl(new HttpClient(stub), new MediaOptions(), new Clock());

        await control.CloseRoomAsync("r", CancellationToken.None);

        Assert.Empty(stub.Calls);
    }

    [Fact]
    public async Task Recording_without_a_configured_path_fails_with_an_explicit_configuration_error()
    {
        var control = new LiveKitRoomControl(new HttpClient(new Stub(HttpStatusCode.OK)), Options(), new Clock());

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            control.SetRecordingAsync("r", true, null, CancellationToken.None));

        Assert.Contains("recording path", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("wss://media.example.org", null, "https://media.example.org/")]
    [InlineData("ws://localhost:7880", null, "http://localhost:7880/")]
    [InlineData("wss://media.example.org:8443", null, "https://media.example.org:8443/")]
    [InlineData("wss://media.example.org", "http://livekit.internal:7880", "http://livekit.internal:7880/")]
    public void The_http_address_comes_from_the_public_websocket_address_unless_overridden(string url, string? apiUrl, string expected) =>
        Assert.Equal(expected, LiveKitRoomControl.ApiBase(Options(url, apiUrl)).ToString());
}
