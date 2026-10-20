namespace KSC.MeetingManagement.Api.Security;

/// <summary>
/// Adds defensive headers to every API response. The API returns JSON and files only, so the content security policy
/// forbids loading anything and framing; responses are not cacheable by shared caches because they are tenant data.
/// </summary>
public sealed class SecurityHeadersMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        context.Response.OnStarting(() =>
        {
            var headers = context.Response.Headers;
            headers["X-Content-Type-Options"] = "nosniff";
            headers["X-Frame-Options"] = "DENY";
            headers["Referrer-Policy"] = "no-referrer";
            headers["Content-Security-Policy"] = "default-src 'none'; frame-ancestors 'none'";
            headers["Permissions-Policy"] = "camera=(), microphone=(), geolocation=(), payment=()";
            headers["Cross-Origin-Resource-Policy"] = "same-site";

            if (!headers.ContainsKey("Cache-Control"))
            {
                headers["Cache-Control"] = "no-store";
            }

            return Task.CompletedTask;
        });

        await next(context);
    }
}
