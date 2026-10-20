using KSC.MeetingManagement.Api.Authorization;
using KSC.MeetingManagement.Api.Observability;
using KSC.MeetingManagement.Api.Tenancy;
using KSC.MeetingManagement.Application.Media;
using KSC.MeetingManagement.Domain.Identity;
using KSC.MeetingManagement.Infrastructure.Media;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace KSC.MeetingManagement.Api.Controllers;

/// <summary>Receives events from the media server. Authenticated by signature, not by user.</summary>
[ApiController]
[Route("api/v1/media/webhook")]
public sealed class MediaWebhookController(LiveKitWebhookVerifier verifier, PresenceService presence) : ControllerBase
{
    private const int MaxBodyBytes = 64 * 1024;

    /// <summary>
    /// 401 unless the signature and body hash check out. Events we do not use, unknown rooms and repeated deliveries get 200, so the
    /// media server does not keep retrying them.
    /// </summary>
    [HttpPost]
    [AllowAnonymous]
    [RequestSizeLimit(MaxBodyBytes)]
    public async Task<IActionResult> Receive(CancellationToken cancellationToken)
    {
        if (!verifier.IsConfigured)
        {
            return Problem("سرویس جلسهٔ آنلاین در دسترس نیست.", statusCode: StatusCodes.Status503ServiceUnavailable);
        }

        using var buffer = new MemoryStream();
        await Request.Body.CopyToAsync(buffer, cancellationToken);
        var body = buffer.ToArray();

        if (!verifier.Verify(Request.Headers.Authorization.ToString(), body))
        {
            return Unauthorized();
        }

        if (LiveKitWebhookVerifier.Parse(body) is not { } evt)
        {
            return Ok(new { status = "ignored" });
        }

        var outcome = await presence.HandleAsync(evt, cancellationToken);
        return Ok(new { status = outcome.ToString().ToLowerInvariant() });
    }
}

[ApiController]
[Route("api/v1/meetings/{meetingId:guid}/presence")]
[RequiresTenant]
public sealed class PresenceController(PresenceService presence) : ControllerBase
{
    /// <summary>Who was in the room and for how long, built from validated media-server events.</summary>
    [HttpGet]
    [HasPermission(Permissions.ReportsView)]
    public async Task<IActionResult> Get(Guid meetingId, CancellationToken cancellationToken) =>
        await presence.SummaryAsync(meetingId, cancellationToken) is { } summary ? Ok(summary) : NotFound();
}
