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
}
