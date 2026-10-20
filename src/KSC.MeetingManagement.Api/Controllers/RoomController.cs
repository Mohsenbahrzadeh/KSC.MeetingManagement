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

public sealed record JoinRoomResponse(string ServerUrl, string Token, string Room, MediaRole Role, DateTimeOffset ExpiresAtUtc);

[ApiController]
[Route("api/v1/meetings/{meetingId:guid}/room")]
[RequiresTenant]
public sealed class RoomController(MediaAccessService media) : ControllerBase
{
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

    private IActionResult Respond(MediaJoinResult result) => result.Outcome switch
    {
        MediaJoinOutcome.Granted => Ok(new JoinRoomResponse(result.Token!.ServerUrl, result.Token.Token, result.Token.Room, result.Token.Role, result.Token.ExpiresAtUtc)),
        MediaJoinOutcome.MeetingNotFound => NotFound(),
        MediaJoinOutcome.NotAllowed => Problem("شما اجازهٔ ورود به این جلسه را ندارید.", statusCode: StatusCodes.Status403Forbidden),
        MediaJoinOutcome.NotOnline => Problem("این جلسه حضوری است و اتاق آنلاین ندارد.", statusCode: StatusCodes.Status422UnprocessableEntity),
        MediaJoinOutcome.NotOpen => Problem("اتاق جلسه اکنون باز نیست.", statusCode: StatusCodes.Status409Conflict),
        _ => Problem("سرویس جلسهٔ آنلاین در دسترس نیست.", statusCode: StatusCodes.Status503ServiceUnavailable),
    };
}
