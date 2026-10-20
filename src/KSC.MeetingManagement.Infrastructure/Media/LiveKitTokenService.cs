using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using KSC.MeetingManagement.Application.Media;

namespace KSC.MeetingManagement.Infrastructure.Media;

/// <summary>
/// Signs LiveKit access tokens (HS256 JWT with a "video" grant). Tokens are short-lived and bound to one room and one identity.
/// </summary>
public sealed class LiveKitTokenService(MediaOptions options, TimeProvider clock) : IMediaTokenService
{
    private const int MaxNameLength = 100;
    private static readonly string[] AllSources = ["camera", "microphone", "screen_share", "screen_share_audio"];
    private static readonly string[] GuestSources = ["camera", "microphone"];

    public bool IsConfigured => options.IsComplete;

    public MediaToken Issue(MediaTokenRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!IsConfigured)
        {
            throw new InvalidOperationException("The media server is not configured.");
        }

        RequireToken(request.Room, nameof(request.Room), 200);
        RequireToken(request.Identity, nameof(request.Identity), 128);
        if (!Enum.IsDefined(request.Role))
        {
            throw new ArgumentException("Unknown media role.", nameof(request));
        }

        var maxLifetime = TimeSpan.FromMinutes(MediaOptions.MaxLifetimeMinutes);
        if (request.Lifetime <= TimeSpan.Zero || request.Lifetime > maxLifetime)
        {
            throw new ArgumentOutOfRangeException(nameof(request), $"Token lifetime must be between 1 second and {MediaOptions.MaxLifetimeMinutes} minutes.");
        }

        var now = clock.GetUtcNow();
        var expires = now + request.Lifetime;
        var payload = new Dictionary<string, object?>
        {
            ["iss"] = options.ApiKey,
            ["sub"] = request.Identity,
            ["name"] = CleanName(request.DisplayName),
            ["nbf"] = now.ToUnixTimeSeconds(),
            ["iat"] = now.ToUnixTimeSeconds(),
            ["exp"] = expires.ToUnixTimeSeconds(),
            ["video"] = Grant(request.Room, request.Role),
        };

        return new MediaToken(Sign(payload), options.ServerUrl!, request.Room, request.Identity, request.Role, expires);
    }

    private static Dictionary<string, object> Grant(string room, MediaRole role)
    {
        var grant = new Dictionary<string, object>
        {
            ["room"] = room,
            ["roomJoin"] = true,
            ["canSubscribe"] = true,
            ["canPublish"] = role != MediaRole.Observer,
            ["canPublishData"] = role is MediaRole.Host or MediaRole.Participant,
            ["canUpdateOwnMetadata"] = false,
            ["roomAdmin"] = role == MediaRole.Host,
            ["roomCreate"] = false,
            ["roomList"] = false,
            ["roomRecord"] = false,
            ["hidden"] = false,
            ["recorder"] = false,
        };

        if (role != MediaRole.Observer)
        {
            grant["canPublishSources"] = role == MediaRole.Guest ? GuestSources : AllSources;
        }

        return grant;
    }

    private string Sign(Dictionary<string, object?> payload)
    {
        var header = Base64Url(JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, string> { ["alg"] = "HS256", ["typ"] = "JWT" }));
        var body = Base64Url(JsonSerializer.SerializeToUtf8Bytes(payload));
        var signingInput = $"{header}.{body}";
        var signature = HMACSHA256.HashData(Encoding.UTF8.GetBytes(options.ApiSecret!), Encoding.ASCII.GetBytes(signingInput));
        return $"{signingInput}.{Base64Url(signature)}";
    }

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static void RequireToken(string value, string name, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > maxLength || value.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.')))
        {
            throw new ArgumentException($"{name} must be 1-{maxLength} characters of letters, digits, '-', '_' or '.'.", name);
        }
    }

    private static string CleanName(string? name)
    {
        var cleaned = new string((name ?? string.Empty).Where(c => !char.IsControl(c)).ToArray()).Trim();
        if (cleaned.Length == 0)
        {
            return "participant";
        }

        return cleaned.Length > MaxNameLength ? cleaned[..MaxNameLength] : cleaned;
    }
}
