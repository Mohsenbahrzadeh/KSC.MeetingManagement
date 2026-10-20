namespace KSC.MeetingManagement.Web.Security;

/// <summary>
/// Defensive headers for the Blazor app. Scripts and fonts come only from this site (everything is bundled), styles may be inline
/// because MudBlazor sets them at run time, and the page cannot be framed. The SignalR connection needs ws:/wss:.
/// Camera and microphone stay allowed for this site only, for the meeting room.
/// </summary>
public sealed class WebSecurityHeadersMiddleware(RequestDelegate next)
{
    public const string NonceKey = "csp-nonce";

    public static string ContentSecurityPolicyFor(string nonce) =>
        $"default-src 'self'; script-src 'self' 'nonce-{nonce}'; style-src 'self' 'unsafe-inline'; img-src 'self' data:; font-src 'self'; "
        + "connect-src 'self' ws: wss:; object-src 'none'; base-uri 'self'; form-action 'self'; frame-ancestors 'none'";

    public async Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var nonce = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(16));
        context.Items[NonceKey] = nonce;
        context.Response.OnStarting(() =>
        {
            var headers = context.Response.Headers;
            headers["X-Content-Type-Options"] = "nosniff";
            headers["X-Frame-Options"] = "DENY";
            headers["Referrer-Policy"] = "strict-origin-when-cross-origin";
            headers["Content-Security-Policy"] = ContentSecurityPolicyFor(nonce);
            headers["Permissions-Policy"] = "camera=(self), microphone=(self), display-capture=(self), geolocation=(), payment=()";
            return Task.CompletedTask;
        });

        await next(context);
    }
}
