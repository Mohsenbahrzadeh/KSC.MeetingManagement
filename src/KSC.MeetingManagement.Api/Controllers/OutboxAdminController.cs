using KSC.MeetingManagement.Api.Authorization;
using KSC.MeetingManagement.Api.Observability;
using KSC.MeetingManagement.Api.Tenancy;
using KSC.MeetingManagement.Application.Notifications;
using KSC.MeetingManagement.Domain.Identity;
using Microsoft.AspNetCore.Mvc;

namespace KSC.MeetingManagement.Api.Controllers;

/// <summary>Operator tools for the notification queue of the current organisation: see its size, inspect dead messages, send them again.</summary>
[ApiController]
[Route("api/v1/notifications/outbox")]
[RequiresTenant]
public sealed class OutboxAdminController(OutboxAdminService outbox) : ControllerBase
{
    [HttpGet("stats")]
    [HasPermission(Permissions.OrganizationManage)]
    public async Task<IActionResult> Stats(CancellationToken cancellationToken) => Ok(await outbox.StatsAsync(cancellationToken));

    [HttpGet("dead")]
    [HasPermission(Permissions.OrganizationManage)]
    public async Task<IActionResult> Dead([FromQuery] int skip = 0, [FromQuery] int take = 50, CancellationToken cancellationToken = default) =>
        Ok(await outbox.ListDeadAsync(skip, take, cancellationToken));

    [Audited("notification.requeue", "notification", "id")]
    [HttpPost("dead/{id:guid}/requeue")]
    [HasPermission(Permissions.OrganizationManage)]
    public async Task<IActionResult> Requeue(Guid id, CancellationToken cancellationToken) =>
        await outbox.RequeueAsync(id, cancellationToken) ? NoContent() : NotFound();

    [Audited("notification.requeue_all", "notification", "none")]
    [HttpPost("dead/requeue-all")]
    [HasPermission(Permissions.OrganizationManage)]
    public async Task<IActionResult> RequeueAll(CancellationToken cancellationToken) =>
        Ok(new { requeued = await outbox.RequeueAllAsync(cancellationToken) });
}
