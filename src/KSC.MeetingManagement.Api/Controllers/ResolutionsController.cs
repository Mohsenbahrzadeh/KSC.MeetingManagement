using KSC.MeetingManagement.Api.Observability;
using KSC.MeetingManagement.Api.Authorization;
using KSC.MeetingManagement.Api.Tenancy;
using KSC.MeetingManagement.Application.Meetings;
using KSC.MeetingManagement.Application.Resolutions;
using KSC.MeetingManagement.Domain.Identity;
using KSC.MeetingManagement.Domain.Meetings;
using Microsoft.AspNetCore.Mvc;

namespace KSC.MeetingManagement.Api.Controllers;

public sealed record CreateResolutionRequest(string Text, Guid? AgendaItemId);

public sealed record EditResolutionRequest(long ExpectedLockVersion, string Text, Guid? AgendaItemId);

public sealed record DecideResolutionRequest(long ExpectedLockVersion);

public sealed record RevokeResolutionRequest(long ExpectedLockVersion, string Reason);

[ApiController]
[Route("api/v1/meetings/{meetingId:guid}/resolutions")]
[RequiresTenant]
public sealed class ResolutionsController(ResolutionService resolutions) : ControllerBase
{
    [HttpGet]
    [HasPermission(Permissions.MeetingsView)]
    public async Task<IActionResult> List(Guid meetingId, CancellationToken cancellationToken) =>
        await resolutions.ListAsync(meetingId, cancellationToken) is { } list ? Ok(list) : NotFound();

    [Audited("resolution.create", "resolution", "meetingId")]
    [HttpPost]
    [HasPermission(Permissions.MinutesWrite)]
    public async Task<IActionResult> Create(Guid meetingId, CreateResolutionRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        try
        {
            return await resolutions.CreateAsync(meetingId, request.Text, request.AgendaItemId, cancellationToken) is { } dto
                ? StatusCode(StatusCodes.Status201Created, dto)
                : NotFound();
        }
        catch (MeetingDomainException ex)
        {
            return Problem(ex.Message, statusCode: StatusCodes.Status422UnprocessableEntity);
        }
    }

    [Audited("resolution.edit", "resolution", "meetingId")]
    [HttpPut("{resolutionId:guid}")]
    [HasPermission(Permissions.MinutesWrite)]
    public Task<IActionResult> Edit(Guid meetingId, Guid resolutionId, EditResolutionRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return Run(() => resolutions.EditAsync(meetingId, resolutionId, request.ExpectedLockVersion, request.Text, request.AgendaItemId, cancellationToken));
    }

    [Audited("resolution.adopt", "resolution", "meetingId")]
    [HttpPost("{resolutionId:guid}/adopt")]
    [HasPermission(Permissions.MinutesWrite)]
    public Task<IActionResult> Adopt(Guid meetingId, Guid resolutionId, DecideResolutionRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return Run(() => resolutions.AdoptAsync(meetingId, resolutionId, request.ExpectedLockVersion, cancellationToken));
    }

    [Audited("resolution.reject", "resolution", "meetingId")]
    [HttpPost("{resolutionId:guid}/reject")]
    [HasPermission(Permissions.MinutesWrite)]
    public Task<IActionResult> Reject(Guid meetingId, Guid resolutionId, DecideResolutionRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return Run(() => resolutions.RejectAsync(meetingId, resolutionId, request.ExpectedLockVersion, cancellationToken));
    }

    /// <summary>Revoking an adopted resolution needs the approval authority and a reason.</summary>
    [Audited("resolution.revoke", "resolution", "meetingId")]
    [HttpPost("{resolutionId:guid}/revoke")]
    [HasPermission(Permissions.MinutesApprove)]
    public Task<IActionResult> Revoke(Guid meetingId, Guid resolutionId, RevokeResolutionRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return Run(() => resolutions.RevokeAsync(meetingId, resolutionId, request.ExpectedLockVersion, request.Reason ?? string.Empty, cancellationToken));
    }

    private async Task<IActionResult> Run(Func<Task<ResolutionDto?>> action)
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
