using System.Security.Claims;
using KSC.MeetingManagement.Application.Identity;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace KSC.MeetingManagement.Api.Controllers;

public sealed record LoginRequest(string? Email, string? Password);

public sealed record RefreshRequest(string? RefreshToken);

public sealed record AuthResponse(string AccessToken, DateTimeOffset AccessTokenExpiresAtUtc, string RefreshToken, DateTimeOffset RefreshTokenExpiresAtUtc, UserProfile User);

[ApiController]
[Route("api/v1/auth")]
public sealed partial class AuthController(AuthService auth, ILogger<AuthController> logger) : ControllerBase
{
    [HttpPost("login")]
    [AllowAnonymous]
    [EnableRateLimiting(Security.SecurityExtensions.GuestPolicy)]
    public async Task<IActionResult> Login(LoginRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var result = await auth.LoginAsync(request.Email, request.Password, cancellationToken);
        if (!result.Succeeded)
        {
            LogRefused(logger, result.Failure);
        }

        return Respond(result);
    }

    [HttpPost("refresh")]
    [AllowAnonymous]
    [EnableRateLimiting(Security.SecurityExtensions.GuestPolicy)]
    public async Task<IActionResult> Refresh(RefreshRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return Respond(await auth.RefreshAsync(request.RefreshToken, cancellationToken));
    }

    [HttpPost("logout")]
    [AllowAnonymous]
    public async Task<IActionResult> Logout(RefreshRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        await auth.LogoutAsync(request.RefreshToken, cancellationToken);
        return NoContent();
    }

    [HttpGet("me")]
    [Authorize]
    public async Task<IActionResult> Me(CancellationToken cancellationToken)
    {
        var sub = User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.FindFirstValue("sub");
        return Guid.TryParse(sub, out var id) && await auth.ProfileAsync(id, cancellationToken) is { } profile ? Ok(profile) : Unauthorized();
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Sign-in refused: {Reason}")]
    private static partial void LogRefused(ILogger logger, AuthFailure? reason);

    private ObjectResult Respond(AuthResult result) => result switch
    {
        { Tokens: { } t } => Ok(new AuthResponse(t.Access.Token, t.Access.ExpiresAtUtc, t.RefreshToken, t.RefreshExpiresAtUtc, t.User)),
        { Failure: AuthFailure.NotConfigured } => Problem(statusCode: StatusCodes.Status503ServiceUnavailable, title: "Sign-in is not configured."),
        { Failure: AuthFailure.Locked } => Problem(statusCode: StatusCodes.Status429TooManyRequests, title: "Too many failed attempts. Try again later."),
        { Failure: AuthFailure.InvalidRefreshToken } => Problem(statusCode: StatusCodes.Status401Unauthorized, title: "Invalid or expired session."),
        _ => Problem(statusCode: StatusCodes.Status401Unauthorized, title: "Invalid email or password."),
    };
}
