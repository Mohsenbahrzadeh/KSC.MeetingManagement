using KSC.MeetingManagement.Application.Tenants;
using Microsoft.AspNetCore.Mvc;

namespace KSC.MeetingManagement.Api.Tenancy;

/// <summary>
/// Resolves the tenant for endpoints marked [RequiresTenant] and rejects the request otherwise.
/// The tenant id currently comes from the X-Tenant-Id header; it will move to an authenticated
/// claim once ADR-0003 is accepted, without changing consumers of ITenantContext.
/// </summary>
public sealed class TenantResolutionMiddleware(RequestDelegate next)
{
    public const string HeaderName = "X-Tenant-Id";

    public async Task InvokeAsync(HttpContext context, ITenantContextSetter setter, ITenantStore store)
    {
        if (context.GetEndpoint()?.Metadata.GetMetadata<RequiresTenantAttribute>() is null)
        {
            await next(context);
            return;
        }

        if (!Guid.TryParse(context.Request.Headers[HeaderName].ToString(), out var tenantId) || tenantId == Guid.Empty)
        {
            await Reject(context, StatusCodes.Status400BadRequest, "شناسه سازمان نامعتبر است.");
            return;
        }

        var tenant = await store.FindByIdAsync(tenantId, context.RequestAborted);

        // Unknown and suspended tenants get the same answer so tenant existence is not disclosed.
        if (tenant is null || !tenant.IsActive)
        {
            await Reject(context, StatusCodes.Status403Forbidden, "دسترسی به این سازمان مجاز نیست.");
            return;
        }

        setter.SetTenant(tenant);
        await next(context);
    }

    private static Task Reject(HttpContext context, int status, string title)
    {
        context.Response.StatusCode = status;
        return context.Response.WriteAsJsonAsync(new ProblemDetails { Status = status, Title = title });
    }
}
