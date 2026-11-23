using Microsoft.AspNetCore.RateLimiting;
using KSC.MeetingManagement.Api.Security;
using KSC.MeetingManagement.Api.Observability;
using KSC.MeetingManagement.Api.Authorization;
using KSC.MeetingManagement.Api.Tenancy;
using KSC.MeetingManagement.Application.Media;
using KSC.MeetingManagement.Domain.Identity;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace KSC.MeetingManagement.Api.Controllers;

public sealed record JoinRoomRequest(string? DisplayName);

public sealed record JoinRoomAsGuestRequest(Guid ParticipantId, string Token);

public sealed record WaitingResponse(string Status, int RetryAfterSeconds);

public sealed record JoinRoomResponse(
    string ServerUrl, string Token, string Room, MediaRole Role, DateTimeOffset ExpiresAtUtc, string PresenterIdentity, IReadOnlyList<IceServer>? IceServers = null);
public sealed record ScreenSharePermissionRequest(bool Allowed);
public sealed record TranscribeMeetingResponse(Guid MeetingId, string Language, DateTimeOffset GeneratedAtUtc,
    IReadOnlyList<TranscriptSegment> Segments, string Text);

[ApiController]
[Route("api/v1/meetings/{meetingId:guid}/room")]
[RequiresTenant]
public sealed class RoomController(MediaAccessService media, RoomControlService control, TurnCredentialService turn, Application.Meetings.MeetingService meetings) : ControllerBase
{
    private const int WaitingRetrySeconds = 5;

    /// <summary>The organizer or an invited participant asks for a token to enter the meeting room.</summary>
    [EnableRateLimiting(SecurityExtensions.SensitivePolicy)]
    [Audited("room.join", "room", "meetingId")]
    [HttpPost("join")]
    [HasPermission(Permissions.MeetingsView)]
    public async Task<IActionResult> Join(Guid meetingId, JoinRoomRequest? request, CancellationToken cancellationToken) =>
        Respond(await media.JoinAsync(meetingId, request?.DisplayName, cancellationToken));

    /// <summary>A guest enters with the invitation token. Every failure of the invitation looks the same.</summary>
    [EnableRateLimiting(SecurityExtensions.GuestPolicy)]
    [HttpPost("join-guest")]
    [Audited("room.join-guest", "room", "meetingId", Guest = true)]
    [AllowAnonymous]
    public async Task<IActionResult> JoinAsGuest(Guid meetingId, JoinRoomAsGuestRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return Respond(await media.JoinGuestAsync(meetingId, request.ParticipantId, request.Token, cancellationToken));
    }

    /// <summary>People waiting for the host to let them in.</summary>
    [HttpGet("lobby")]
    [HasPermission(Permissions.MeetingsEdit)]
    public async Task<IActionResult> Lobby(Guid meetingId, CancellationToken cancellationToken) =>
        await control.WaitingAsync(meetingId, cancellationToken) is { } list ? Ok(list) : NotFound();

    [HttpPost("lobby/{identity}/admit")]
    [HasPermission(Permissions.MeetingsEdit)]
    [Audited("room.admit", "room", "meetingId")]
    public Task<IActionResult> Admit(Guid meetingId, string identity, CancellationToken cancellationToken) =>
        Decide(() => control.AdmitAsync(meetingId, identity, cancellationToken));

    [HttpPost("lobby/{identity}/deny")]
    [HasPermission(Permissions.MeetingsEdit)]
    [Audited("room.deny", "room", "meetingId")]
    public Task<IActionResult> Deny(Guid meetingId, string identity, CancellationToken cancellationToken) =>
        Decide(() => control.DenyAsync(meetingId, identity, cancellationToken));

    /// <summary>Removes a person from the room and bars them from entering again.</summary>
    [HttpPost("participants/{identity}/remove")]
    [HasPermission(Permissions.MeetingsEdit)]
    [Audited("room.remove", "room", "meetingId")]
    public Task<IActionResult> Remove(Guid meetingId, string identity, CancellationToken cancellationToken) =>
        Decide(() => control.RemoveAsync(meetingId, identity, cancellationToken));

    /// <summary>The meeting organizer mutes all other participants' microphones; they may unmute themselves at any time.</summary>
    [HttpPost("mute-all")]
    [HasPermission(Permissions.MeetingsEdit)]
    [Audited("room.mute-all", "room", "meetingId")]
    public async Task<IActionResult> MuteAll(Guid meetingId, CancellationToken cancellationToken)
    {
        try
        {
            return await control.MuteAllAsync(meetingId, cancellationToken) ? NoContent() : NotFound();
        }
        catch (UnauthorizedAccessException)
        {
            return Forbid();
        }
        catch (HttpRequestException)
        {
            return Problem("کنترل میکروفون شرکت‌کنندگان از دسترس خارج است.", statusCode: StatusCodes.Status503ServiceUnavailable);
        }
    }

    /// <summary>The organizer grants or revokes one participant's screen-share publishing permission.</summary>
    [HttpPut("participants/{identity}/screen-share-permission")]
    [HasPermission(Permissions.MeetingsEdit)]
    [Audited("room.screen-share-permission", "room", "meetingId")]
    public async Task<IActionResult> SetScreenSharePermission(Guid meetingId, string identity, [FromBody] ScreenSharePermissionRequest request, CancellationToken cancellationToken)
    {
        try
        {
            return await control.SetScreenSharePermissionAsync(meetingId, identity, request.Allowed, cancellationToken)
                ? NoContent()
                : NotFound();
        }
        catch (UnauthorizedAccessException)
        {
            return Forbid();
        }
        catch (HttpRequestException)
        {
            return Problem("تغییر مجوز اشتراک صفحه در دسترس نیست.", statusCode: StatusCodes.Status503ServiceUnavailable);
        }
    }

    [HttpPut("participants/{identity}/microphone-permission")]
    [HasPermission(Permissions.MeetingsEdit)]
    [Audited("room.microphone-permission", "room", "meetingId")]
    public async Task<IActionResult> SetMicrophonePermission(Guid meetingId, string identity, [FromBody] ScreenSharePermissionRequest request, CancellationToken cancellationToken)
    {
        try
        {
            return await control.SetMicrophonePermissionAsync(meetingId, identity, request.Allowed, cancellationToken)
                ? NoContent()
                : NotFound();
        }
        catch (UnauthorizedAccessException)
        {
            return Forbid();
        }
        catch (HttpRequestException)
        {
            return Problem("تغییر مجوز میکروفن در دسترس نیست.", statusCode: StatusCodes.Status503ServiceUnavailable);
        }
    }


    [HttpPost("recording/{actionName}")]
    [HasPermission(Permissions.MeetingsEdit)]
    [Audited("room.recording", "room", "meetingId")]
    public async Task<IActionResult> SetRecording(Guid meetingId, string actionName, CancellationToken cancellationToken)
    {
        if (actionName is not ("start" or "stop")) return NotFound();
        try
        {
            if (!await control.SetRecordingAsync(meetingId, actionName == "start", cancellationToken))
            {
                return Problem("ضبط شروع نشد: تنظیمات LiveKit یا مسیر ذخیره‌سازی ضبط کامل نیست.", statusCode: StatusCodes.Status503ServiceUnavailable);
            }
            return NoContent();
        }
        catch (UnauthorizedAccessException)
        {
            return Forbid();
        }
        catch (InvalidOperationException)
        {
            return Problem("ضبط فعال نیست: تنظیمات LiveKit و Media__RecordingPath را کامل کنید و مطمئن شوید LiveKit Egress و Redis فعال‌اند و Egress به پوشهٔ ضبط دسترسی نوشتن دارد.", statusCode: StatusCodes.Status503ServiceUnavailable);
        }
        catch (HttpRequestException)
        {
            return Problem("سرویس ضبط جلسه در دسترس نیست.", statusCode: StatusCodes.Status503ServiceUnavailable);
        }
    }

    /// <summary>Transcribes the completed server-side recording locally; no audio is sent to an external service.</summary>
    [HttpPost("transcription")]
    [EnableRateLimiting(SecurityExtensions.SensitivePolicy)]
    [HasPermission(Permissions.MeetingsView)]
    [Audited("room.transcription", "room", "meetingId")]
    public async Task<IActionResult> Transcribe(Guid meetingId, CancellationToken cancellationToken)
    {
        try
        {
            if (!await meetings.IsOrganizerAsync(meetingId, cancellationToken)) return NotFound();
            var transcript = await control.TranscribeAsync(meetingId, cancellationToken);
            return transcript is null ? NotFound() : Ok(ToResponse(transcript));
        }
        catch (UnauthorizedAccessException)
        {
            return Forbid();
        }
        catch (TranscriptionNotReadyException exception)
        {
            return Problem(exception.Message, statusCode: StatusCodes.Status409Conflict);
        }
        catch (InvalidOperationException exception)
        {
            return Problem(exception.Message, statusCode: StatusCodes.Status503ServiceUnavailable);
        }
        catch (DirectoryNotFoundException)
        {
            return Problem("مسیر فایل ضبط در API mount نشده است.", statusCode: StatusCodes.Status503ServiceUnavailable);
        }
        catch (FileNotFoundException)
        {
            return Problem("فایل ضبط‌شده یا مدل محلی پیدا نشد.", statusCode: StatusCodes.Status503ServiceUnavailable);
        }
        catch (TimeoutException)
        {
            return Problem("درخواست رونویسی دیگری برای این فایل در حال اجراست.", statusCode: StatusCodes.Status409Conflict);
        }
        catch (HttpRequestException)
        {
            return Problem("پردازش محلی فایل ضبط انجام نشد.", statusCode: StatusCodes.Status422UnprocessableEntity);
        }
        catch (InvalidDataException)
        {
            return Problem("فایل ضبط معتبر نیست یا صوت آن قابل استخراج نیست.", statusCode: StatusCodes.Status422UnprocessableEntity);
        }
    }

    /// <summary>Returns the latest locally generated transcript to the meeting organizer.</summary>
    [HttpGet("transcription")]
    [EnableRateLimiting(SecurityExtensions.SensitivePolicy)]
    [HasPermission(Permissions.MeetingsView)]
    public async Task<IActionResult> GetTranscript(Guid meetingId, CancellationToken cancellationToken)
    {
        try
        {
            if (!await meetings.IsOrganizerAsync(meetingId, cancellationToken)) return NotFound();
            var transcript = await control.GetTranscriptAsync(meetingId, cancellationToken);
            return transcript is null ? NotFound() : Ok(ToResponse(transcript));
        }
        catch (UnauthorizedAccessException)
        {
            return Forbid();
        }
    }

    private static TranscribeMeetingResponse ToResponse(MeetingTranscript transcript) =>
        new(transcript.MeetingId, transcript.Language, transcript.GeneratedAtUtc, transcript.Segments, transcript.Text);

    private async Task<IActionResult> Decide(Func<Task<RoomEntryDto?>> action)
    {
        try
        {
            return await action() is { } dto ? Ok(dto) : NotFound();
        }
        catch (KSC.MeetingManagement.Domain.Meetings.MeetingDomainException ex)
        {
            return Problem(ex.Message, statusCode: StatusCodes.Status422UnprocessableEntity);
        }
        catch (KSC.MeetingManagement.Application.Meetings.MeetingConcurrencyException ex)
        {
            return Problem(ex.Message, statusCode: StatusCodes.Status409Conflict);
        }
    }

    private IActionResult Respond(MediaJoinResult result) => result.Outcome switch
    {
        MediaJoinOutcome.Granted => Ok(new JoinRoomResponse(
            result.Token!.ServerUrl, result.Token.Token, result.Token.Room, result.Token.Role, result.Token.ExpiresAtUtc,
            result.Token.PresenterIdentity, turn.Issue(result.Token.Identity) is { } ice ? [ice] : null)),
        MediaJoinOutcome.MeetingNotFound => NotFound(),
        MediaJoinOutcome.Waiting => Accepted(new WaitingResponse("Waiting", WaitingRetrySeconds)),
        MediaJoinOutcome.NotAllowed => Problem("شما اجازهٔ ورود به این جلسه را ندارید.", statusCode: StatusCodes.Status403Forbidden),
        MediaJoinOutcome.NotOnline => Problem("این جلسه حضوری است و اتاق آنلاین ندارد.", statusCode: StatusCodes.Status422UnprocessableEntity),
        MediaJoinOutcome.NotOpen => Problem("اتاق جلسه اکنون باز نیست.", statusCode: StatusCodes.Status409Conflict),
        _ => Problem("سرویس جلسهٔ آنلاین در دسترس نیست.", statusCode: StatusCodes.Status503ServiceUnavailable),
    };
}
