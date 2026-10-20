using KSC.MeetingManagement.Domain.Identity;

namespace KSC.MeetingManagement.Application.Identity;

public interface IMembershipStore
{
    Task<Membership?> FindAsync(Guid tenantId, Guid userId, CancellationToken cancellationToken);
}
