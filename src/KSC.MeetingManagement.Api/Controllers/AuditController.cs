using Microsoft.AspNetCore.RateLimiting;
using KSC.MeetingManagement.Api.Security;
using KSC.MeetingManagement.Api.Authorization;
using KSC.MeetingManagement.Api.Tenancy;
using KSC.MeetingManagement.Application.Audit;
using KSC.MeetingManagement.Domain.Audit;
using KSC.MeetingManagement.Domain.Identity;
using Microsoft.AspNetCore.Mvc;

namespace KSC.MeetingManagement.Api.Controllers;

[ApiController]
[Route("api/v1/audit")]
[RequiresTenant]
[EnableRateLimiting(SecurityExtensions.SensitivePolicy)]
public sealed class AuditController(AuditQueryService audit) : ControllerBase
{
    /// <summary>Searches the audit log of the current organisation, newest first.</summary>
    [HttpGet]
    [HasPermission(Permissions.AuditView)]
    public async Task<IActionResult> Search(
        DateTimeOffset? from,
        DateTimeOffset? to,
        string? action,
        Guid? actorId,
        Guid? resourceId,
        AuditOutcome? outcome,
        int skip = 0,
        int take = 50,
        CancellationToken cancellationToken = default)
    {
        try
        {
            return Ok(await audit.QueryAsync(new AuditQuery(from, to, action, actorId, resourceId, outcome, skip, take), cancellationToken));
        }
        catch (ArgumentException ex)
        {
            return Problem(ex.Message, statusCode: StatusCodes.Status422UnprocessableEntity);
        }
    }
}
