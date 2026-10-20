using System.Security.Claims;
using KSC.MeetingManagement.Application.Identity;

namespace KSC.MeetingManagement.Api.Authorization;

public sealed class HttpCurrentUser(IHttpContextAccessor accessor) : ICurrentUser
{
    public Guid RequiredUserId
    {
        get
        {
            var user = accessor.HttpContext?.User;
            var sub = user?.FindFirstValue(ClaimTypes.NameIdentifier) ?? user?.FindFirstValue("sub");
            return Guid.TryParse(sub, out var id)
                ? id
                : throw new InvalidOperationException("No authenticated user for this request.");
        }
    }
}
