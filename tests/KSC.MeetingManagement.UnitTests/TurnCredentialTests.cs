using System.Security.Cryptography;
using System.Text;
using KSC.MeetingManagement.Application.Media;

namespace KSC.MeetingManagement.UnitTests;

public class TurnCredentialTests
{
    private const string Secret = "0123456789abcdef0123456789abcdef-turn";
    private static readonly DateTimeOffset Now = new(2026, 5, 1, 9, 0, 0, TimeSpan.Zero);

    private sealed class Clock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private static TurnCredentialService Service(string? secret = Secret, string[]? urls = null, int ttl = 60) =>
        new(new TurnOptions { Urls = urls ?? ["turn:turn.example.org:3478?transport=udp"], Secret = secret, TtlMinutes = ttl }, new Clock());

    [Fact]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "CA5350:Do Not Use Weak Cryptographic Algorithms", Justification = "Reference computation of the coturn REST scheme.")]
    public void The_credential_follows_the_turn_rest_scheme_and_expires()
    {
        var ice = Service().Issue("u-abc")!;

        var parts = ice.Username.Split(':');
        var expected = Convert.ToBase64String(HMACSHA1.HashData(Encoding.UTF8.GetBytes(Secret), Encoding.UTF8.GetBytes(ice.Username)));
        Assert.Equal(Now.AddMinutes(60).ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture), parts[0]);
        Assert.Equal("u-abc", parts[1]);
        Assert.Equal(expected, ice.Credential);
        Assert.Equal(["turn:turn.example.org:3478?transport=udp"], ice.Urls);
        Assert.DoesNotContain(Secret, ice.Credential + ice.Username, StringComparison.Ordinal);
    }

    [Fact]
    public void Credentials_differ_per_identity_and_a_different_secret_gives_a_different_password()
    {
        Assert.NotEqual(Service().Issue("u-a")!.Credential, Service().Issue("u-b")!.Credential);
        Assert.NotEqual(Service().Issue("u-a")!.Credential, Service("another-secret-another-secret-another").Issue("u-a")!.Credential);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("short")]
    public void Without_a_strong_secret_nothing_is_issued(string? secret) => Assert.Null(Service(secret).Issue("u-a"));

    [Fact]
    public void Without_addresses_or_with_an_unsafe_identity_nothing_is_issued()
    {
        Assert.Null(Service(urls: []).Issue("u-a"));
        Assert.Null(Service().Issue(""));
        Assert.Null(Service().Issue("u:a"));
    }

    [Fact]
    public void The_lifetime_is_bounded()
    {
        var long_ = Service(ttl: 100000).Issue("u-a")!;
        var short_ = Service(ttl: 0).Issue("u-a")!;

        Assert.Equal(Now.AddHours(24).ToUnixTimeSeconds(), long.Parse(long_.Username.Split(':')[0], System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal(Now.AddMinutes(1).ToUnixTimeSeconds(), long.Parse(short_.Username.Split(':')[0], System.Globalization.CultureInfo.InvariantCulture));
    }
}
