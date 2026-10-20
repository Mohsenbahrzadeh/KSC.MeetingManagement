using KSC.MeetingManagement.Api.Observability;
using KSC.MeetingManagement.Api.Authorization;
using KSC.MeetingManagement.Api.Tenancy;
using KSC.MeetingManagement.Application.Meetings;
using KSC.MeetingManagement.Domain.Identity;
using KSC.MeetingManagement.Domain.Meetings;
using Microsoft.AspNetCore.Mvc;

namespace KSC.MeetingManagement.Api.Controllers;

public sealed record CreateMeetingRequest(
    Guid? Id,
    string Title,
    string? Description,
    MeetingMode Mode,
    DateTimeOffset? Start,
    DateTimeOffset? End,
    bool AllowConflicts = false);

public sealed record UpdateMeetingRequest(
    long ExpectedVersion,
    string Title,
    string? Description,
    MeetingMode Mode,
    DateTimeOffset? Start,
    DateTimeOffset? End,
    bool AllowConflicts = false);

public sealed record RescheduleMeetingRequest(long ExpectedVersion, DateTimeOffset Start, DateTimeOffset End, bool AllowConflicts = false);

public sealed record CancelMeetingRequest(long ExpectedVersion, string Reason);

public sealed record PostponeMeetingRequest(long ExpectedVersion);

[ApiController]
[Route("api/v1/meetings")]
[RequiresTenant]
public sealed class MeetingsController(MeetingService meetings) : ControllerBase
{
    [Audited("meeting.create", "meeting", "id")]
    [HttpPost]
    [HasPermission(Permissions.MeetingsCreate)]
    [ProducesResponseType<MeetingDto>(StatusCodes.Status201Created)]
    [ProducesResponseType<MeetingDto>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Create(CreateMeetingRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        try
        {
            var result = await meetings.CreateAsync(
                new CreateMeetingCommand(request.Id, request.Title, request.Description, request.Mode, request.Start, request.End, request.AllowConflicts),
                cancellationToken);

            return result.Created
                ? CreatedAtAction(nameof(Get), new { id = result.Meeting.Id }, result.Meeting)
                : Ok(result.Meeting);
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

    [HttpGet("{id:guid}")]
    [HasPermission(Permissions.MeetingsView)]
    public async Task<IActionResult> Get(Guid id, CancellationToken cancellationToken) =>
        await meetings.GetAsync(id, cancellationToken) is { } dto ? Ok(dto) : NotFound();

    [Audited("meeting.update", "meeting", "id")]
    [HttpPut("{id:guid}")]
    [HasPermission(Permissions.MeetingsEdit)]
    public Task<IActionResult> Update(Guid id, UpdateMeetingRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return Mutate(() => meetings.UpdateAsync(
            id,
            new UpdateMeetingCommand(request.ExpectedVersion, request.Title, request.Description, request.Mode, request.Start, request.End, request.AllowConflicts),
            cancellationToken));
    }

    [Audited("meeting.reschedule", "meeting", "id")]
    [HttpPost("{id:guid}/reschedule")]
    [HasPermission(Permissions.MeetingsEdit)]
    public Task<IActionResult> Reschedule(Guid id, RescheduleMeetingRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return Mutate(() => meetings.RescheduleAsync(id, request.ExpectedVersion, request.Start, request.End, request.AllowConflicts, cancellationToken));
    }

    [Audited("meeting.cancel", "meeting", "id")]
    [HttpPost("{id:guid}/cancel")]
    [HasPermission(Permissions.MeetingsCancel)]
    public Task<IActionResult> Cancel(Guid id, CancelMeetingRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return Mutate(() => meetings.CancelAsync(id, request.ExpectedVersion, request.Reason, cancellationToken));
    }

    [Audited("meeting.postpone", "meeting", "id")]
    [HttpPost("{id:guid}/postpone")]
    [HasPermission(Permissions.MeetingsEdit)]
    public Task<IActionResult> Postpone(Guid id, PostponeMeetingRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return Mutate(() => meetings.PostponeAsync(id, request.ExpectedVersion, cancellationToken));
    }

    private async Task<IActionResult> Mutate(Func<Task<MeetingDto?>> action)
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
        catch (MeetingTimeConflictException ex)
        {
            return ConflictProblem.From(ex);
        }
    }

    /// <summary>Lists meetings; from/to select the meetings overlapping that range (used by the scheduler).</summary>
    [HttpGet]
    [HasPermission(Permissions.MeetingsView)]
    public async Task<IActionResult> List(
        DateTimeOffset? from,
        DateTimeOffset? to,
        MeetingStatus? status,
        int skip = 0,
        int take = 50,
        CancellationToken cancellationToken = default)
    {
        try
        {
            return Ok(await meetings.ListAsync(new MeetingQuery(from, to, status, skip, take), cancellationToken));
        }
        catch (MeetingDomainException ex)
        {
            return Problem(ex.Message, statusCode: StatusCodes.Status422UnprocessableEntity);
        }
    }
}
