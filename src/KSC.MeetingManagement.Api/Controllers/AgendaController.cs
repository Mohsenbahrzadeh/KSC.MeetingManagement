using KSC.MeetingManagement.Api.Authorization;
using KSC.MeetingManagement.Api.Tenancy;
using KSC.MeetingManagement.Application.Agenda;
using KSC.MeetingManagement.Application.Meetings;
using KSC.MeetingManagement.Domain.Identity;
using KSC.MeetingManagement.Domain.Meetings;
using Microsoft.AspNetCore.Mvc;

namespace KSC.MeetingManagement.Api.Controllers;

public sealed record AgendaItemRequest(long ExpectedRevision, string Title, string? Description, Guid? PresenterUserId, int? DurationMinutes);

public sealed record ReorderAgendaRequest(long ExpectedRevision, IReadOnlyList<Guid> ItemIds);

[ApiController]
[Route("api/v1/meetings/{meetingId:guid}/agenda")]
[RequiresTenant]
public sealed class AgendaController(AgendaService agenda) : ControllerBase
{
    [HttpGet]
    [HasPermission(Permissions.MeetingsView)]
    public async Task<IActionResult> Get(Guid meetingId, CancellationToken cancellationToken) =>
        await agenda.GetAsync(meetingId, cancellationToken) is { } dto ? Ok(dto) : NotFound();

    [HttpPost]
    [HasPermission(Permissions.MeetingsEdit)]
    public Task<IActionResult> Add(Guid meetingId, AgendaItemRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return Run(() => agenda.AddAsync(meetingId, request.ExpectedRevision, ToInput(request), cancellationToken));
    }

    [HttpPut("{itemId:guid}")]
    [HasPermission(Permissions.MeetingsEdit)]
    public Task<IActionResult> Update(Guid meetingId, Guid itemId, AgendaItemRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return Run(() => agenda.UpdateAsync(meetingId, itemId, request.ExpectedRevision, ToInput(request), cancellationToken));
    }

    [HttpDelete("{itemId:guid}")]
    [HasPermission(Permissions.MeetingsEdit)]
    public Task<IActionResult> Remove(Guid meetingId, Guid itemId, [FromQuery] long expectedRevision, CancellationToken cancellationToken) =>
        Run(() => agenda.RemoveAsync(meetingId, itemId, expectedRevision, cancellationToken));

    [HttpPut("order")]
    [HasPermission(Permissions.MeetingsEdit)]
    public Task<IActionResult> Reorder(Guid meetingId, ReorderAgendaRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return Run(() => agenda.ReorderAsync(meetingId, request.ExpectedRevision, request.ItemIds ?? [], cancellationToken));
    }

    private static AgendaItemInput ToInput(AgendaItemRequest r) => new(r.Title, r.Description, r.PresenterUserId, r.DurationMinutes);

    private async Task<IActionResult> Run(Func<Task<AgendaDto?>> action)
    {
        try
        {
            return await action() is { } dto ? Ok(dto) : NotFound();
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
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
