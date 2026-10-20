namespace KSC.MeetingManagement.Application.Media;

/// <summary>What a person may do in a media room.</summary>
public enum MediaRole
{
    /// <summary>Organizer: can publish, subscribe and administer the room (mute or remove others).</summary>
    Host = 1,

    /// <summary>Invited internal participant: publish and subscribe.</summary>
    Participant = 2,

    /// <summary>External guest: publish and subscribe only; no data messages and no administration.</summary>
    Guest = 3,

    /// <summary>Can only watch and listen.</summary>
    Observer = 4,
}

/// <summary>Request to sign a token. Identity and room are decided by the caller after it has authorised the person.</summary>
public sealed record MediaTokenRequest(string Room, string Identity, string DisplayName, MediaRole Role, TimeSpan Lifetime);

public sealed record MediaToken(string Token, string ServerUrl, string Room, string Identity, MediaRole Role, DateTimeOffset ExpiresAtUtc);

/// <summary>Port that signs short-lived room tokens. The signing secret never leaves the implementation.</summary>
public interface IMediaTokenService
{
    /// <summary>True when the media server settings are complete; when false no token can be issued.</summary>
    bool IsConfigured { get; }

    MediaToken Issue(MediaTokenRequest request);
}

/// <summary>Media server settings. ApiSecret comes from environment or secret storage, never from committed files.</summary>
public sealed class MediaOptions
{
    public const int MinSecretLength = 32;
    public const int MaxLifetimeMinutes = 60;

    /// <summary>Public WebSocket address browsers connect to, for example wss://media.example.org.</summary>
    public string? ServerUrl { get; set; }

    public string? ApiKey { get; set; }

    public string? ApiSecret { get; set; }

    public int TokenLifetimeMinutes { get; set; } = 10;

    /// <summary>How long before the planned start people may enter the room.</summary>
    public int JoinOpensMinutesBeforeStart { get; set; } = 15;

    /// <summary>How long after the planned end people may still enter (late finish).</summary>
    public int JoinClosesMinutesAfterEnd { get; set; } = 120;

    public bool IsComplete =>
        !string.IsNullOrWhiteSpace(ServerUrl) && !string.IsNullOrWhiteSpace(ApiKey)
        && ApiSecret is { Length: >= MinSecretLength };
}
