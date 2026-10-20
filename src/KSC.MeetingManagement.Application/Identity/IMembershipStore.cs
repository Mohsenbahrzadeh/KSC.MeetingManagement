using KSC.MeetingManagement.Domain.Identity;

namespace KSC.MeetingManagement.Application.Identity;

public interface IMembershipStore
{
    Task<Membership?> FindAsync(Guid tenantId, Guid userId, CancellationToken cancellationToken);

    /// <summary>All memberships of a user across tenants (used to build the sign-in profile; never exposes other users).</summary>
    Task<IReadOnlyList<Membership>> ListForUserAsync(Guid userId, CancellationToken cancellationToken);
}
