using KSC.MeetingManagement.Api.Authorization;
using KSC.MeetingManagement.Api.Tenancy;
using KSC.MeetingManagement.Application.Actions;
using KSC.MeetingManagement.Application.Identity;
using KSC.MeetingManagement.Application.Meetings;
using KSC.MeetingManagement.Domain.Identity;
using KSC.MeetingManagement.Domain.Meetings;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace KSC.MeetingManagement.Api.Controllers;

public sealed record ActionItemRequest(string Title, string? Description, Guid OwnerUserId, DateOnly DueDate, Guid? ResolutionId);

public sealed record UpdateActionRequest(long ExpectedLockVersion, string Title, string? Description, Guid OwnerUserId, DateOnly DueDate);

public sealed record ActionVersionRequest(long ExpectedLockVersion);

public sealed record CompleteActionRequest(long ExpectedLockVersion, string? Note);

public sealed record CancelActionRequest(long ExpectedLockVersion, string Reason);

[ApiController]
[RequiresTenant]
public sealed class ActionsController(ActionItemService actions, IAuthorizationService authorization, ICurrentUser user) : ControllerBase
{
    [HttpGet("api/v1/meetings/{meetingId:guid}/actions")]
    [HasPermission(Permissions.MeetingsView)]
    public async Task<IActionResult> ListForMeeting(Guid meetingId, CancellationToken cancellationToken) =>
        await actions.ListForMeetingAsync(meetingId, cancellationToken) is { } list ? Ok(list) : NotFound();

    [HttpPost("api/v1/meetings/{meetingId:guid}/actions")]
    [HasPermission(Permissions.ActionsManage)]
    public async Task<IActionResult> Create(Guid meetingId, ActionItemRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        try
        {
            var input = new ActionItemInput(request.Title, request.Description, request.OwnerUserId, request.DueDate, request.ResolutionId);
            return await actions.CreateAsync(meetingId, input, cancellationToken) is { } dto ? StatusCode(StatusCodes.Status201Created, dto) : NotFound();
        }
        catch (MeetingDomainException ex)
        {
            return Problem(ex.Message, statusCode: StatusCodes.Status422UnprocessableEntity);
        }
    }

    [HttpPut("api/v1/meetings/{meetingId:guid}/actions/{actionId:guid}")]
    [HasPermission(Permissions.ActionsManage)]
    public Task<IActionResult> Update(Guid meetingId, Guid actionId, UpdateActionRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var input = new ActionItemInput(request.Title, request.Description, request.OwnerUserId, request.DueDate, null);
        return Run(() => actions.UpdateAsync(meetingId, actionId, request.ExpectedLockVersion, input, cancellationToken));
    }

    /// <summary>The owner (or a manager) starts the action.</summary>
    [HttpPost("api/v1/meetings/{meetingId:guid}/actions/{actionId:guid}/start")]
    [HasPermission(Permissions.MeetingsView)]
    public Task<IActionResult> Start(Guid meetingId, Guid actionId, ActionVersionRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return RunAsOwnerOrManager(canManage => actions.StartAsync(meetingId, actionId, request.ExpectedLockVersion, canManage, cancellationToken));
    }

    /// <summary>Records completion; allowed for the owner or a manager.</summary>
    [HttpPost("api/v1/meetings/{meetingId:guid}/actions/{actionId:guid}/complete")]
    [HasPermission(Permissions.MeetingsView)]
    public Task<IActionResult> Complete(Guid meetingId, Guid actionId, CompleteActionRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return RunAsOwnerOrManager(canManage => actions.CompleteAsync(meetingId, actionId, request.ExpectedLockVersion, request.Note, canManage, cancellationToken));
    }

    [HttpPost("api/v1/meetings/{meetingId:guid}/actions/{actionId:guid}/cancel")]
    [HasPermission(Permissions.ActionsManage)]
    public Task<IActionResult> Cancel(Guid meetingId, Guid actionId, CancelActionRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return Run(() => actions.CancelAsync(meetingId, actionId, request.ExpectedLockVersion, request.Reason ?? string.Empty, cancellationToken));
    }

    /// <summary>The caller's own actions, optionally only the active ones.</summary>
    [HttpGet("api/v1/actions/mine")]
    [HasPermission(Permissions.MeetingsView)]
    public async Task<IActionResult> Mine(bool activeOnly = true, int skip = 0, int take = 50, CancellationToken cancellationToken = default) =>
        Ok(await actions.QueryAsync(new ActionItemQuery(user.RequiredUserId, null, null, activeOnly, skip, take), cancellationToken));

    /// <summary>Overdue report: active actions whose due day is before today in the organisation's time zone.</summary>
    [HttpGet("api/v1/actions/overdue")]
    [HasPermission(Permissions.ReportsView)]
    public async Task<IActionResult> Overdue(Guid? ownerUserId, int skip = 0, int take = 50, CancellationToken cancellationToken = default) =>
        Ok(await actions.OverdueAsync(ownerUserId, skip, take, cancellationToken));

    private async Task<bool> CanManageAsync() =>
        (await authorization.AuthorizeAsync(User, null, HasPermissionAttribute.PolicyPrefix + Permissions.ActionsManage)).Succeeded;

    private async Task<IActionResult> RunAsOwnerOrManager(Func<bool, Task<ActionItemDto?>> action) =>
        await Run(async () => await action(await CanManageAsync()));

    private async Task<IActionResult> Run(Func<Task<ActionItemDto?>> action)
    {
        try
        {
            return await action() is { } dto ? Ok(dto) : NotFound();
        }
        catch (MeetingDomainException ex)
        {
            return Problem(ex.Message, statusCode: StatusCodes.Status422UnprocessableEntity);
        }
        catch (MeetingConcurrencyException ex)
        {
            return Problem(ex.Message, statusCode: StatusCodes.Status409Conflict);
        }
    }
}
