using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using KSC.MeetingManagement.Application.Media;
using KSC.MeetingManagement.Infrastructure.Media;

namespace KSC.MeetingManagement.UnitTests;

public class MediaTokenTests
{
    private const string Secret = "0123456789abcdef0123456789abcdef-secret";
    private static readonly DateTimeOffset Now = new(2026, 5, 1, 9, 0, 0, TimeSpan.Zero);

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private static MediaOptions Options(string? secret = Secret) =>
        new() { ServerUrl = "wss://media.example.org", ApiKey = "APIkey1", ApiSecret = secret };

    private static LiveKitTokenService Service(MediaOptions? options = null) => new(options ?? Options(), new FixedClock(Now));

    private static MediaTokenRequest Request(MediaRole role = MediaRole.Participant, string room = "ksc-room-1", string identity = "u-1", TimeSpan? lifetime = null) =>
        new(room, identity, "علی", role, lifetime ?? TimeSpan.FromMinutes(10));

    private static byte[] Decode(string part) =>
        Convert.FromBase64String(part.Replace('-', '+').Replace('_', '/').PadRight(part.Length + ((4 - (part.Length % 4)) % 4), '='));

    private static JsonElement Payload(string token) => JsonDocument.Parse(Decode(token.Split('.')[1])).RootElement;

    [Fact]
    public void Token_is_a_valid_hs256_jwt_signed_with_the_secret()
    {
        var token = Service().Issue(Request()).Token;
        var parts = token.Split('.');

        var header = JsonDocument.Parse(Decode(parts[0])).RootElement;
        var expected = HMACSHA256.HashData(Encoding.UTF8.GetBytes(Secret), Encoding.ASCII.GetBytes($"{parts[0]}.{parts[1]}"));

        Assert.Equal(3, parts.Length);
        Assert.Equal("HS256", header.GetProperty("alg").GetString());
        Assert.Equal("JWT", header.GetProperty("typ").GetString());
        Assert.Equal(expected, Decode(parts[2]));
    }

    [Fact]
    public void A_different_secret_or_a_tampered_payload_does_not_validate()
    {
        var token = Service().Issue(Request(MediaRole.Guest)).Token;
        var parts = token.Split('.');
        var other = HMACSHA256.HashData(Encoding.UTF8.GetBytes("another-secret-another-secret-another"), Encoding.ASCII.GetBytes($"{parts[0]}.{parts[1]}"));
        var tamperedBody = Convert.ToBase64String(Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(Decode(parts[1])).Replace("\"roomAdmin\":false", "\"roomAdmin\":true", StringComparison.Ordinal)))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var forged = HMACSHA256.HashData(Encoding.UTF8.GetBytes(Secret), Encoding.ASCII.GetBytes($"{parts[0]}.{tamperedBody}"));

        Assert.NotEqual(other, Decode(parts[2]));
        Assert.NotEqual(forged, Decode(parts[2]));
    }

    [Fact]
    public void Claims_bind_the_token_to_one_identity_one_room_and_a_short_window()
    {
        var result = Service().Issue(Request(room: "ksc-abc", identity: "u-42", lifetime: TimeSpan.FromMinutes(10)));
        var p = Payload(result.Token);

        Assert.Equal("APIkey1", p.GetProperty("iss").GetString());
        Assert.Equal("u-42", p.GetProperty("sub").GetString());
        Assert.Equal("علی", p.GetProperty("name").GetString());
        Assert.Equal("ksc-abc", p.GetProperty("video").GetProperty("room").GetString());
        Assert.Equal(Now.ToUnixTimeSeconds(), p.GetProperty("nbf").GetInt64());
        Assert.Equal(Now.AddMinutes(10).ToUnixTimeSeconds(), p.GetProperty("exp").GetInt64());
        Assert.Equal(Now.AddMinutes(10), result.ExpiresAtUtc);
        Assert.Equal("wss://media.example.org", result.ServerUrl);
    }

    [Fact]
    public void The_secret_never_appears_in_the_token()
    {
        var token = Service().Issue(Request()).Token;

        Assert.DoesNotContain(Secret, token, StringComparison.Ordinal);
        Assert.DoesNotContain(Secret, Encoding.UTF8.GetString(Decode(token.Split('.')[1])), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(MediaRole.Host, true, true, true, true)]
    [InlineData(MediaRole.Participant, false, true, true, true)]
    [InlineData(MediaRole.Guest, false, true, true, false)]
    [InlineData(MediaRole.Observer, false, false, true, false)]
    public void Capabilities_follow_the_role(MediaRole role, bool admin, bool publish, bool subscribe, bool data)
    {
        var video = Payload(Service().Issue(Request(role)).Token).GetProperty("video");

        Assert.Equal(admin, video.GetProperty("roomAdmin").GetBoolean());
        Assert.Equal(publish, video.GetProperty("canPublish").GetBoolean());
        Assert.Equal(subscribe, video.GetProperty("canSubscribe").GetBoolean());
        Assert.Equal(data, video.GetProperty("canPublishData").GetBoolean());
        Assert.True(video.GetProperty("roomJoin").GetBoolean());
    }

    [Fact]
    public void No_role_can_create_list_or_record_rooms_and_guests_cannot_share_the_screen()
    {
        foreach (var role in Enum.GetValues<MediaRole>())
        {
            var video = Payload(Service().Issue(Request(role)).Token).GetProperty("video");
            Assert.False(video.GetProperty("roomCreate").GetBoolean());
            Assert.False(video.GetProperty("roomList").GetBoolean());
            Assert.False(video.GetProperty("roomRecord").GetBoolean());
            Assert.False(video.GetProperty("hidden").GetBoolean());
        }

        var guest = Payload(Service().Issue(Request(MediaRole.Guest)).Token).GetProperty("video").GetProperty("canPublishSources");
        var host = Payload(Service().Issue(Request(MediaRole.Host)).Token).GetProperty("video").GetProperty("canPublishSources");
        Assert.DoesNotContain(guest.EnumerateArray(), e => e.GetString() == "screen_share");
        Assert.Contains(host.EnumerateArray(), e => e.GetString() == "screen_share");
    }

    [Theory]
    [InlineData("")]
    [InlineData("room with space")]
    [InlineData("room/../x")]
    [InlineData("اتاق")]
    public void Unsafe_room_names_are_refused(string room) =>
        Assert.Throws<ArgumentException>(() => Service().Issue(Request(room: room)));

    [Fact]
    public void Unsafe_identities_are_refused()
    {
        Assert.Throws<ArgumentException>(() => Service().Issue(Request(identity: "")));
        Assert.Throws<ArgumentException>(() => Service().Issue(Request(identity: "a b")));
        Assert.Throws<ArgumentException>(() => Service().Issue(Request(identity: new string('x', 129))));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    [InlineData(61)]
    public void Lifetime_must_be_between_zero_and_the_maximum(int minutes) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => Service().Issue(Request(lifetime: TimeSpan.FromMinutes(minutes))));

    [Fact]
    public void Display_names_are_cleaned_and_limited()
    {
        var long_ = Payload(Service().Issue(Request() with { DisplayName = new string('ا', 300) }).Token).GetProperty("name").GetString();
        var control = Payload(Service().Issue(Request() with { DisplayName = "a\r\nb\t" }).Token).GetProperty("name").GetString();
        var empty = Payload(Service().Issue(Request() with { DisplayName = "   " }).Token).GetProperty("name").GetString();

        Assert.Equal(100, long_!.Length);
        Assert.Equal("ab", control);
        Assert.Equal("participant", empty);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("short-secret")]
    public void Missing_or_weak_configuration_blocks_issuing(string? secret)
    {
        var service = Service(Options(secret));

        Assert.False(service.IsConfigured);
        Assert.Throws<InvalidOperationException>(() => service.Issue(Request()));
    }

    [Fact]
    public void Incomplete_urls_and_keys_are_not_configured()
    {
        Assert.False(new MediaOptions { ApiKey = "k", ApiSecret = Secret }.IsComplete);
        Assert.False(new MediaOptions { ServerUrl = "wss://x", ApiSecret = Secret }.IsComplete);
        Assert.True(Options().IsComplete);
    }
}
