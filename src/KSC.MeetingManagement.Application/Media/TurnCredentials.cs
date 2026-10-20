using System.Security.Cryptography;
using System.Text;

namespace KSC.MeetingManagement.Application.Media;

/// <summary>Relay (TURN) settings. The secret is shared with coturn (static-auth-secret) and comes from the environment.</summary>
public sealed class TurnOptions
{
    public const int MinSecretLength = 32;

    /// <summary>Relay addresses such as turn:turn.example.org:3478?transport=udp and turns:turn.example.org:5349?transport=tcp.</summary>
    public IReadOnlyList<string> Urls { get; set; } = [];

    public string? Secret { get; set; }

    public int TtlMinutes { get; set; } = 60;

    public bool IsComplete => Urls.Count > 0 && Secret is { Length: >= MinSecretLength };
}

public sealed record IceServer(IReadOnlyList<string> Urls, string Username, string Credential);

/// <summary>
/// Makes time-limited relay credentials in the TURN REST API form coturn understands: username "expiry:identity", password
/// base64(HMAC-SHA1(secret, username)). Nothing is stored; a credential stops working at its expiry and is bound to one identity.
/// </summary>
public sealed class TurnCredentialService(TurnOptions options, TimeProvider clock)
{
    public bool IsConfigured => options.IsComplete;

    // HMAC-SHA1 is dictated by the TURN REST credential scheme that coturn implements; it is used as a keyed MAC, where SHA-1 remains sound.
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "CA5350:Do Not Use Weak Cryptographic Algorithms", Justification = "Required by the coturn TURN REST scheme (keyed HMAC).")]
    public IceServer? Issue(string identity)
    {
        if (!IsConfigured || string.IsNullOrWhiteSpace(identity) || identity.Contains(':', StringComparison.Ordinal))
        {
            return null;
        }

        var expires = clock.GetUtcNow().AddMinutes(Math.Clamp(options.TtlMinutes, 1, 24 * 60)).ToUnixTimeSeconds();
        var username = $"{expires}:{identity}";
        var password = Convert.ToBase64String(HMACSHA1.HashData(Encoding.UTF8.GetBytes(options.Secret!), Encoding.UTF8.GetBytes(username)));
        return new IceServer(options.Urls, username, password);
    }
}
