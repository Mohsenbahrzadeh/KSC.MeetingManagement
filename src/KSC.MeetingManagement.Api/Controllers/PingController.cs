using KSC.MeetingManagement.Api.Tenancy;
using KSC.MeetingManagement.Application.Tenants;
using Microsoft.AspNetCore.Mvc;

namespace KSC.MeetingManagement.Api.Controllers;

[ApiController]
[Route("api/v1/ping")]
public sealed class PingController : ControllerBase
{
    [HttpGet]
    public IActionResult Get() => Ok(new { status = "ok" });

    [HttpGet("fail")]
    public IActionResult Fail() => throw new InvalidOperationException("ping failure test");

    [HttpGet("tenant")]
    [RequiresTenant]
    public IActionResult Tenant([FromServices] ITenantContext tenant) => Ok(new { tenantId = tenant.RequiredTenantId });
}
