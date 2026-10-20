using Microsoft.AspNetCore.Authorization;

namespace KSC.MeetingManagement.Api.Authorization;

/// <summary>Requires the caller to hold the given permission in the request tenant. Combine with [RequiresTenant].</summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = true)]
public sealed class HasPermissionAttribute(string permission) : AuthorizeAttribute(PolicyPrefix + permission)
{
    public const string PolicyPrefix = "perm:";
}
