namespace KSC.MeetingManagement.Application.Media;

/// <summary>What a person may do in a media room.</summary>
public enum MediaRole
{
    /// <summary>Organizer: can publish, subscribe and administer the room (mute or remove others).</summary>
    Host = 1,

    /// <summary>Invited internal participant: publish and subscribe.</summary>
    Participant = 2,

    /// <summary>External guest: publish and subscribe only; no whiteboard data messages or administration.</summary>
    Guest = 3,

    /// <summary>Can only watch and listen.</summary>
    Observer = 4,
}

/// <summary>Request to sign a token. Identity and room are decided by the caller after it has authorised the person.</summary>
public sealed record MediaTokenRequest(string Room, string Identity, string DisplayName, MediaRole Role, TimeSpan Lifetime);

public sealed record MediaToken(string Token, string ServerUrl, string Room, string Identity, MediaRole Role, DateTimeOffset ExpiresAtUtc, string PresenterIdentity);

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

    /// <summary>Guests wait in a lobby until the host admits them.</summary>
    public bool LobbyForGuests { get; set; } = true;

    /// <summary>Invited members also wait for admission (the organizer never does).</summary>
    public bool LobbyForMembers { get; set; }

    /// <summary>Address of the media server's HTTP API when it differs from the public one; defaults to the public address with http(s).</summary>
    public string? ApiUrl { get; set; }

    /// <summary>Absolute directory inside the Egress container where meeting recordings are written. Empty disables recording.</summary>
    public string? RecordingPath { get; set; }

    /// <summary>Path to a locally installed Whisper GGML model. No model is downloaded automatically.</summary>
    public string? TranscriptionModelPath { get; set; }

    /// <summary>Recording directory as mounted inside the API container; defaults to RecordingPath.</summary>
    public string? TranscriptionRecordingPath { get; set; }

    /// <summary>Writable local directory for generated transcript files.</summary>
    public string? TranscriptionOutputPath { get; set; }

    /// <summary>Whisper language code; defaults to Persian.</summary>
    public string TranscriptionLanguage { get; set; } = "fa";

    /// <summary>FFmpeg executable used to decode the local Egress MP4 into 16 kHz mono WAV.</summary>
    public string FfmpegPath { get; set; } = "ffmpeg";

    /// <summary>How long to wait for Egress to finish writing a recording before reporting that it is unavailable.</summary>
    public int TranscriptionFileWaitSeconds { get; set; } = 60;

    public bool IsComplete =>
        !string.IsNullOrWhiteSpace(ServerUrl) && !string.IsNullOrWhiteSpace(ApiKey)
        && ApiSecret is { Length: >= MinSecretLength };

    public bool RecordingConfigured => IsComplete && !string.IsNullOrWhiteSpace(RecordingPath);

    public string? EffectiveTranscriptionRecordingPath =>
        string.IsNullOrWhiteSpace(TranscriptionRecordingPath) ? RecordingPath : TranscriptionRecordingPath;

    public string EffectiveTranscriptionOutputPath =>
        string.IsNullOrWhiteSpace(TranscriptionOutputPath)
            ? Path.Combine(AppContext.BaseDirectory, "App_Data", "transcripts")
            : TranscriptionOutputPath;

    public bool TranscriptionConfigured => RecordingConfigured
        && !string.IsNullOrWhiteSpace(TranscriptionModelPath)
        && !string.IsNullOrWhiteSpace(EffectiveTranscriptionRecordingPath);
}

public sealed class TranscriptionNotReadyException(string message) : Exception(message);

public sealed record TranscriptSegment(TimeSpan Start, TimeSpan End, string Text);

public sealed record MeetingTranscript(Guid MeetingId, string Language, DateTimeOffset GeneratedAtUtc,
    IReadOnlyList<TranscriptSegment> Segments, string Text);

/// <summary>Transcribes server-side recordings using locally installed software and models only.</summary>
public interface IMeetingTranscriptionService
{
    bool IsConfigured { get; }

    Task<MeetingTranscript> TranscribeAsync(string room, Guid meetingId, CancellationToken cancellationToken);

    Task<MeetingTranscript?> GetLatestAsync(string room, Guid meetingId, CancellationToken cancellationToken);
}
