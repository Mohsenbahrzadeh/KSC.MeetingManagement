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
    DateTimeOffset? End);

[ApiController]
[Route("api/v1/meetings")]
[RequiresTenant]
public sealed class MeetingsController(MeetingService meetings) : ControllerBase
{
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
                new CreateMeetingCommand(request.Id, request.Title, request.Description, request.Mode, request.Start, request.End),
                cancellationToken);

            return result.Created
                ? CreatedAtAction(nameof(Get), new { id = result.Meeting.Id }, result.Meeting)
                : Ok(result.Meeting);
        }
        catch (MeetingDomainException ex)
        {
            return Problem(ex.Message, statusCode: StatusCodes.Status422UnprocessableEntity);
        }
    }

    [HttpGet("{id:guid}")]
    [HasPermission(Permissions.MeetingsView)]
    public async Task<IActionResult> Get(Guid id, CancellationToken cancellationToken) =>
        await meetings.GetAsync(id, cancellationToken) is { } dto ? Ok(dto) : NotFound();

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
