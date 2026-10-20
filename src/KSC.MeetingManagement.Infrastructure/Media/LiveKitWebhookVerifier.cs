using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using KSC.MeetingManagement.Application.Media;

namespace KSC.MeetingManagement.Infrastructure.Media;

/// <summary>
/// Checks that a webhook really comes from the media server: the Authorization value is a JWT signed with our API secret, issued by our API
/// key, still valid, and carrying the SHA-256 of the exact body that was received. Anything else is rejected; the body is parsed only after that.
/// </summary>
public sealed class LiveKitWebhookVerifier(MediaOptions options, TimeProvider clock)
{
    private static readonly TimeSpan Skew = TimeSpan.FromMinutes(1);

    public bool IsConfigured => options.IsComplete;

    public bool Verify(string? authorization, ReadOnlySpan<byte> body)
    {
        if (!IsConfigured || string.IsNullOrWhiteSpace(authorization))
        {
            return false;
        }

        // LiveKit sends the bare token; accept a "Bearer " prefix too.
        var token = authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? authorization[7..].Trim() : authorization.Trim();
        var parts = token.Split('.');
        if (parts.Length != 3)
        {
            return false;
        }

        try
        {
            var header = JsonDocument.Parse(Decode(parts[0])).RootElement;
            if (header.GetProperty("alg").GetString() != "HS256")
            {
                return false;
            }

            var expected = HMACSHA256.HashData(Encoding.UTF8.GetBytes(options.ApiSecret!), Encoding.ASCII.GetBytes($"{parts[0]}.{parts[1]}"));
            if (!CryptographicOperations.FixedTimeEquals(expected, Decode(parts[2])))
            {
                return false;
            }

            var claims = JsonDocument.Parse(Decode(parts[1])).RootElement;
            var now = clock.GetUtcNow();
            if (claims.GetProperty("iss").GetString() != options.ApiKey
                || claims.TryGetProperty("exp", out var exp) && DateTimeOffset.FromUnixTimeSeconds(exp.GetInt64()) < now - Skew
                || claims.TryGetProperty("nbf", out var nbf) && DateTimeOffset.FromUnixTimeSeconds(nbf.GetInt64()) > now + Skew)
            {
                return false;
            }

            var bodyHash = Convert.ToBase64String(SHA256.HashData(body));
            return claims.TryGetProperty("sha256", out var claimed)
                && claimed.GetString() is { } text
                && CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(text), Encoding.ASCII.GetBytes(bodyHash));
        }
        catch (Exception ex) when (ex is JsonException or FormatException or KeyNotFoundException or InvalidOperationException or ArgumentOutOfRangeException)
        {
            return false;
        }
    }

    /// <summary>Reads the events this system cares about. Returns null for other event types or a body that does not look like a media event.</summary>
    public static MediaWebhookEvent? Parse(ReadOnlySpan<byte> body)
    {
        try
        {
            var root = JsonDocument.Parse(body.ToArray()).RootElement;
            var type = root.GetProperty("event").GetString() switch
            {
                "participant_joined" => MediaEventType.ParticipantJoined,
                "participant_left" => MediaEventType.ParticipantLeft,
                "room_finished" => MediaEventType.RoomFinished,
                _ => (MediaEventType?)null,
            };
            if (type is null)
            {
                return null;
            }

            var room = root.GetProperty("room").GetProperty("name").GetString();
            var id = root.GetProperty("id").GetString();
            var at = UnixSeconds(root.GetProperty("createdAt"));
            string? identity = null;
            string? name = null;
            if (root.TryGetProperty("participant", out var participant))
            {
                identity = participant.TryGetProperty("identity", out var i) ? i.GetString() : null;
                name = participant.TryGetProperty("name", out var n) ? n.GetString() : null;
            }

            return room is null || id is null ? null : new MediaWebhookEvent(id, type.Value, room, identity, name, at);
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            return null;
        }
    }

    // Protobuf JSON writes 64-bit numbers as strings; accept both.
    private static DateTimeOffset UnixSeconds(JsonElement element) =>
        DateTimeOffset.FromUnixTimeSeconds(
            element.ValueKind == JsonValueKind.String ? long.Parse(element.GetString()!, CultureInfo.InvariantCulture) : element.GetInt64());

    private static byte[] Decode(string part) =>
        Convert.FromBase64String(part.Replace('-', '+').Replace('_', '/').PadRight(part.Length + ((4 - (part.Length % 4)) % 4), '='));
}
