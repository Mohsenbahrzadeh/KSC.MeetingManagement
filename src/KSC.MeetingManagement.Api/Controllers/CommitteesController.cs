using KSC.MeetingManagement.Api.Authorization;
using KSC.MeetingManagement.Api.Tenancy;
using KSC.MeetingManagement.Application.Committees;
using KSC.MeetingManagement.Domain.Identity;
using KSC.MeetingManagement.Domain.Meetings;
using Microsoft.AspNetCore.Mvc;

namespace KSC.MeetingManagement.Api.Controllers;

public sealed record CreateCommitteeRequest(string Name, string? Description);

public sealed record UpdateCommitteeRequest(string Name, string? Description, bool IsActive);

public sealed record AddCommitteeMemberRequest(Guid UserId, CommitteeRole Role);

public sealed record ChangeCommitteeRoleRequest(CommitteeRole Role);

[ApiController]
[Route("api/v1/committees")]
[RequiresTenant]
public sealed class CommitteesController(CommitteeService committees) : ControllerBase
{
    [HttpGet]
    [HasPermission(Permissions.MeetingsView)]
    public async Task<IActionResult> List(CancellationToken cancellationToken) =>
        Ok(await committees.ListAsync(cancellationToken));

    [HttpGet("{id:guid}")]
    [HasPermission(Permissions.MeetingsView)]
    public async Task<IActionResult> Get(Guid id, CancellationToken cancellationToken) =>
        await committees.GetAsync(id, cancellationToken) is { } dto ? Ok(dto) : NotFound();

    [HttpPost]
    [HasPermission(Permissions.OrganizationManage)]
    public async Task<IActionResult> Create(CreateCommitteeRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        try
        {
            var dto = await committees.CreateAsync(request.Name, request.Description, cancellationToken);
            return CreatedAtAction(nameof(Get), new { id = dto.Id }, dto);
        }
        catch (MeetingDomainException ex)
        {
            return Problem(ex.Message, statusCode: StatusCodes.Status422UnprocessableEntity);
        }
    }

    [HttpPut("{id:guid}")]
    [HasPermission(Permissions.OrganizationManage)]
    public Task<IActionResult> Update(Guid id, UpdateCommitteeRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return Run(() => committees.UpdateAsync(id, request.Name, request.Description, request.IsActive, cancellationToken));
    }

    [HttpPost("{id:guid}/members")]
    [HasPermission(Permissions.OrganizationManage)]
    public Task<IActionResult> AddMember(Guid id, AddCommitteeMemberRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return Run(() => committees.AddMemberAsync(id, request.UserId, request.Role, cancellationToken));
    }

    [HttpPut("{id:guid}/members/{userId:guid}")]
    [HasPermission(Permissions.OrganizationManage)]
    public Task<IActionResult> ChangeRole(Guid id, Guid userId, ChangeCommitteeRoleRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return Run(() => committees.ChangeMemberRoleAsync(id, userId, request.Role, cancellationToken));
    }

    [HttpDelete("{id:guid}/members/{userId:guid}")]
    [HasPermission(Permissions.OrganizationManage)]
    public Task<IActionResult> RemoveMember(Guid id, Guid userId, CancellationToken cancellationToken) =>
        Run(() => committees.RemoveMemberAsync(id, userId, cancellationToken));

    private async Task<IActionResult> Run(Func<Task<CommitteeDto?>> action)
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
    }
}
