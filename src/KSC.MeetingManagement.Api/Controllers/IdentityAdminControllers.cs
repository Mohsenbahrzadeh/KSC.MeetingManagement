using System.Security.Cryptography;
using System.Text;
using KSC.MeetingManagement.Api.Authorization;
using KSC.MeetingManagement.Api.Observability;
using KSC.MeetingManagement.Api.Tenancy;
using KSC.MeetingManagement.Application.Identity;
using KSC.MeetingManagement.Domain.Identity;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace KSC.MeetingManagement.Api.Controllers;

public sealed record AddMemberRequest(string? Email, string? DisplayName, string? Password, string[]? Roles);

public sealed record SetRolesRequest(string[]? Roles);

public sealed record SetActiveRequest(bool Active);

/// <summary>
/// One-time installation: creates the first organisation and its owner. Only available while the system has no organisation and
/// only to someone who knows the <c>Setup:Token</c> secret (sent in the X-Setup-Token header). Without that setting the endpoint does not exist.
/// </summary>
[ApiController]
[Route("api/v1/setup")]
public sealed class SetupController(IdentityAdminService admin, IConfiguration configuration) : ControllerBase
{
    [HttpPost]
    [AllowAnonymous]
    [EnableRateLimiting(Security.SecurityExtensions.GuestPolicy)]
    public async Task<IActionResult> Setup(SetupRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var expected = configuration["Setup:Token"];
        if (string.IsNullOrWhiteSpace(expected) || expected.Length < 16)
        {
            return NotFound();
        }

        if (!Request.Headers.TryGetValue("X-Setup-Token", out var supplied) || !SameSecret(expected, supplied.ToString()))
        {
            return Problem(statusCode: StatusCodes.Status401Unauthorized, title: "Invalid setup token.");
        }

        var result = await admin.SetupAsync(request, cancellationToken);
        return result.Succeeded ? StatusCode(StatusCodes.Status201Created, result.Value) : AdminProblems.From(this, result.Error!.Value, result.Message);
    }

    private static bool SameSecret(string expected, string supplied) =>
        CryptographicOperations.FixedTimeEquals(SHA256.HashData(Encoding.UTF8.GetBytes(expected)), SHA256.HashData(Encoding.UTF8.GetBytes(supplied)));
}

internal static class AdminProblems
{
    public static ObjectResult From(ControllerBase controller, AdminError error, string? message) =>
        controller.Problem(
            statusCode: error switch
            {
                AdminError.NotFound => StatusCodes.Status404NotFound,
                AdminError.Conflict => StatusCodes.Status409Conflict,
                AdminError.Forbidden => StatusCodes.Status403Forbidden,
                _ => StatusCodes.Status400BadRequest,
            },
            title: message ?? "Request failed.");
}

[ApiController]
[Route("api/v1/users")]
[RequiresTenant]
public sealed class UsersController(IdentityAdminService admin) : ControllerBase
{
    [HttpGet]
    [HasPermission(Permissions.UsersManage)]
    public async Task<IActionResult> List(CancellationToken cancellationToken) => Ok(await admin.ListAsync(cancellationToken));

    [Audited("user.add", "user", "id")]
    [HttpPost]
    [HasPermission(Permissions.UsersManage)]
    public async Task<IActionResult> Add(AddMemberRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var result = await admin.AddMemberAsync(request.Email, request.DisplayName, request.Password, request.Roles, cancellationToken);
        return result.Succeeded
            ? Created($"/api/v1/users/{result.Value!.UserId}", result.Value)
            : AdminProblems.From(this, result.Error!.Value, result.Message);
    }

    [Audited("user.roles", "user", "id")]
    [HttpPut("{id:guid}/roles")]
    [HasPermission(Permissions.UsersManage)]
    public async Task<IActionResult> SetRoles(Guid id, SetRolesRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var result = await admin.SetRolesAsync(id, request.Roles, cancellationToken);
        return result.Succeeded ? Ok(result.Value) : AdminProblems.From(this, result.Error!.Value, result.Message);
    }

    [Audited("user.active", "user", "id")]
    [HttpPut("{id:guid}/active")]
    [HasPermission(Permissions.UsersManage)]
    public async Task<IActionResult> SetActive(Guid id, SetActiveRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var result = await admin.SetActiveAsync(id, request.Active, cancellationToken);
        return result.Succeeded ? Ok(result.Value) : AdminProblems.From(this, result.Error!.Value, result.Message);
    }
}
