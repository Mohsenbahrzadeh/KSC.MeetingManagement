using Microsoft.AspNetCore.RateLimiting;
using KSC.MeetingManagement.Api.Security;
using KSC.MeetingManagement.Api.Observability;
using KSC.MeetingManagement.Api.Authorization;
using KSC.MeetingManagement.Api.Tenancy;
using KSC.MeetingManagement.Application.Identity;
using KSC.MeetingManagement.Application.Meetings;
using KSC.MeetingManagement.Application.Participants;
using KSC.MeetingManagement.Domain.Identity;
using KSC.MeetingManagement.Domain.Meetings;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace KSC.MeetingManagement.Api.Controllers;

public sealed record AddInternalParticipantRequest(Guid UserId, bool AllowConflicts = false);

public sealed record InviteGuestRequest(string Name, string Email, DateTimeOffset ExpiresUtc);

public sealed record RespondRequest(bool Accept);

public sealed record RedeemInvitationRequest(string Token);

[ApiController]
[Route("api/v1/meetings/{meetingId:guid}/participants")]
[RequiresTenant]
public sealed class ParticipantsController(ParticipantService participants, ICurrentUser user) : ControllerBase
{
    [HttpGet]
    [HasPermission(Permissions.MeetingsView)]
    public async Task<IActionResult> List(Guid meetingId, CancellationToken cancellationToken) =>
        Ok(await participants.ListAsync(meetingId, cancellationToken));

    [Audited("participant.add-internal", "participant", "meetingId")]
    [HttpPost("internal")]
    [HasPermission(Permissions.MeetingsEdit)]
    public Task<IActionResult> AddInternal(Guid meetingId, AddInternalParticipantRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return Run(async () => await participants.AddInternalAsync(meetingId, request.UserId, request.AllowConflicts, cancellationToken) is { } dto
            ? StatusCode(StatusCodes.Status201Created, dto)
            : NotFound());
    }

    /// <summary>Invites a guest. The raw token is returned once in this response and is never retrievable again.</summary>
    [Audited("participant.invite-guest", "participant", "meetingId")]
    [HttpPost("guests")]
    [HasPermission(Permissions.MeetingsEdit)]
    public Task<IActionResult> InviteGuest(Guid meetingId, InviteGuestRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return Run(async () => await participants.InviteGuestAsync(meetingId, request.Name, request.Email, request.ExpiresUtc, cancellationToken) is { } dto
            ? StatusCode(StatusCodes.Status201Created, dto)
            : NotFound());
    }

    [Audited("participant.revoke", "participant", "meetingId")]
    [HttpPost("{participantId:guid}/revoke")]
    [HasPermission(Permissions.MeetingsEdit)]
    public async Task<IActionResult> Revoke(Guid meetingId, Guid participantId, CancellationToken cancellationToken) =>
        await participants.RevokeAsync(meetingId, participantId, cancellationToken) is { } dto ? Ok(dto) : NotFound();

    [Audited("participant.respond", "participant", "meetingId")]
    [HttpPost("{participantId:guid}/respond")]
    [HasPermission(Permissions.MeetingsView)]
    public Task<IActionResult> Respond(Guid meetingId, Guid participantId, RespondRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return Run(async () => await participants.RespondAsync(meetingId, participantId, user.RequiredUserId, request.Accept, cancellationToken) is { } dto
            ? Ok(dto)
            : NotFound());
    }

    /// <summary>Guest presents the invitation token. Any failure returns the same 404 so nothing is revealed.</summary>
    [EnableRateLimiting(SecurityExtensions.GuestPolicy)]
    [HttpPost("{participantId:guid}/redeem")]
    [Audited("participant.redeem", "participant", "meetingId", Guest = true)]
    [AllowAnonymous]
    public async Task<IActionResult> Redeem(Guid meetingId, Guid participantId, RedeemInvitationRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return await participants.RedeemAsync(meetingId, participantId, request.Token, cancellationToken) is { } dto
            ? Ok(dto)
            : NotFound();
    }

    private async Task<IActionResult> Run(Func<Task<IActionResult>> action)
    {
        try
        {
            return await action();
        }
        catch (MeetingDomainException ex)
        {
            return Problem(ex.Message, statusCode: StatusCodes.Status422UnprocessableEntity);
        }
        catch (MeetingTimeConflictException ex)
        {
            return ConflictProblem.From(ex);
        }
    }
}
