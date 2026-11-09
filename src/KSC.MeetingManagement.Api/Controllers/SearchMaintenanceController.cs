using KSC.MeetingManagement.Api.Authorization;
using KSC.MeetingManagement.Api.Tenancy;
using KSC.MeetingManagement.Application.Meetings;
using KSC.MeetingManagement.Domain.Identity;
using Microsoft.AspNetCore.Mvc;

namespace KSC.MeetingManagement.Api.Controllers;

/// <summary>Privileged tenant-scoped repair endpoint for rebuilding the derived Lucene index.</summary>
[ApiController]
[Route("api/v1/search")]
[RequiresTenant]
[HasPermission(Permissions.OrganizationManage)]
public sealed class SearchMaintenanceController(MeetingSearchService search) : ControllerBase
{
    [HttpPost("rebuild")]
    public async Task<IActionResult> Rebuild(CancellationToken cancellationToken) =>
        Ok(new { IndexedSourceRecords = await search.RebuildAsync(cancellationToken) });
}
