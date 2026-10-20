using KSC.MeetingManagement.Api.Observability;
using KSC.MeetingManagement.Api.Authorization;
using KSC.MeetingManagement.Api.Tenancy;
using KSC.MeetingManagement.Application.Meetings;
using KSC.MeetingManagement.Application.Minutes;
using KSC.MeetingManagement.Domain.Identity;
using KSC.MeetingManagement.Domain.Meetings;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace KSC.MeetingManagement.Api.Controllers;

public sealed record SaveMinutesRequest(string Content, int ExpectedVersionNumber);

public sealed record CommentRequest(string? Comment);

[ApiController]
[Route("api/v1/meetings/{meetingId:guid}/minutes")]
[RequiresTenant]
public sealed class MinutesController(MinutesService minutes, IAuthorizationService authorization) : ControllerBase
{
    [HttpGet]
    [HasPermission(Permissions.MeetingsView)]
    public async Task<IActionResult> Get(Guid meetingId, CancellationToken cancellationToken) =>
        await minutes.GetAsync(meetingId, await CanSeeUnapprovedAsync(), cancellationToken) is { } dto ? Ok(dto) : NotFound();

    [HttpGet("versions")]
    [HasPermission(Permissions.MeetingsView)]
    public async Task<IActionResult> Versions(Guid meetingId, CancellationToken cancellationToken) =>
        await minutes.ListVersionsAsync(meetingId, await CanSeeUnapprovedAsync(), cancellationToken) is { } list ? Ok(list) : NotFound();

    [HttpGet("versions/{number:int}")]
    [HasPermission(Permissions.MeetingsView)]
    public async Task<IActionResult> Version(Guid meetingId, int number, CancellationToken cancellationToken) =>
        await minutes.GetVersionAsync(meetingId, number, await CanSeeUnapprovedAsync(), cancellationToken) is { } dto ? Ok(dto) : NotFound();

    /// <summary>Creates the minutes (expectedVersionNumber 0) or saves a new version of the draft.</summary>
    [Audited("minutes.save", "minutes", "meetingId")]
    [HttpPut]
    [HasPermission(Permissions.MinutesWrite)]
    public Task<IActionResult> Save(Guid meetingId, SaveMinutesRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return Run(() => minutes.SaveAsync(meetingId, request.Content, request.ExpectedVersionNumber, cancellationToken));
    }

    [Audited("minutes.submit", "minutes", "meetingId")]
    [HttpPost("submit")]
    [HasPermission(Permissions.MinutesWrite)]
    public Task<IActionResult> Submit(Guid meetingId, CancellationToken cancellationToken) =>
        Run(() => minutes.SubmitAsync(meetingId, cancellationToken));

    [Audited("minutes.approve", "minutes", "meetingId")]
    [HttpPost("approve")]
    [HasPermission(Permissions.MinutesApprove)]
    public Task<IActionResult> Approve(Guid meetingId, CommentRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return Run(() => minutes.ApproveAsync(meetingId, request.Comment, cancellationToken));
    }

    [Audited("minutes.reject", "minutes", "meetingId")]
    [HttpPost("reject")]
    [HasPermission(Permissions.MinutesApprove)]
    public Task<IActionResult> Reject(Guid meetingId, CommentRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return Run(() => minutes.RejectAsync(meetingId, request.Comment ?? string.Empty, cancellationToken));
    }

    /// <summary>The authorised way to change approved minutes; a reason is required and the minutes go through review again.</summary>
    [Audited("minutes.reopen", "minutes", "meetingId")]
    [HttpPost("reopen")]
    [HasPermission(Permissions.MinutesApprove)]
    public Task<IActionResult> Reopen(Guid meetingId, CommentRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return Run(() => minutes.ReopenAsync(meetingId, request.Comment ?? string.Empty, cancellationToken));
    }

    private async Task<bool> CanSeeUnapprovedAsync() =>
        (await authorization.AuthorizeAsync(User, null, HasPermissionAttribute.PolicyPrefix + Permissions.MinutesWrite)).Succeeded
        || (await authorization.AuthorizeAsync(User, null, HasPermissionAttribute.PolicyPrefix + Permissions.MinutesApprove)).Succeeded;

    private async Task<IActionResult> Run(Func<Task<MinutesDto?>> action)
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
