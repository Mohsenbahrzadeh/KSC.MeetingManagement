using KSC.MeetingManagement.Application.Identity;
using KSC.MeetingManagement.Application.Tenants;
using KSC.MeetingManagement.Domain.Meetings;

namespace KSC.MeetingManagement.Application.Committees;

public sealed record CommitteeMemberDto(Guid UserId, CommitteeRole Role);

public sealed record CommitteeDto(
    Guid Id, string Name, string? Description, bool IsActive, IReadOnlyList<CommitteeMemberDto> Members)
{
    public static CommitteeDto From(Committee c)
    {
        ArgumentNullException.ThrowIfNull(c);
        return new(c.Id, c.Name, c.Description, c.IsActive,
            c.Members.OrderBy(m => m.UserId).Select(m => new CommitteeMemberDto(m.UserId, m.Role)).ToList());
    }
}

/// <summary>Persistence port. Implementations must only return committees of the current tenant.</summary>
public interface ICommitteeRepository
{
    Task<Committee?> GetAsync(Guid id, CancellationToken cancellationToken);

    Task<IReadOnlyList<Committee>> ListAsync(CancellationToken cancellationToken);

    Task AddAsync(Committee committee, CancellationToken cancellationToken);

    Task SaveChangesAsync(CancellationToken cancellationToken);
}

public sealed class CommitteeService(ICommitteeRepository repository, IMembershipStore memberships, ITenantContext tenant)
{
    public async Task<CommitteeDto> CreateAsync(string name, string? description, CancellationToken cancellationToken)
    {
        var committee = new Committee(Guid.NewGuid(), tenant.RequiredTenantId, name, description);
        await repository.AddAsync(committee, cancellationToken);
        await repository.SaveChangesAsync(cancellationToken);
        return CommitteeDto.From(committee);
    }

    public Task<CommitteeDto?> UpdateAsync(Guid id, string name, string? description, bool isActive, CancellationToken cancellationToken) =>
        ChangeAsync(id, c => c.Update(name, description, isActive), cancellationToken);

    public async Task<CommitteeDto?> GetAsync(Guid id, CancellationToken cancellationToken) =>
        await repository.GetAsync(id, cancellationToken) is { } c ? CommitteeDto.From(c) : null;

    public async Task<IReadOnlyList<CommitteeDto>> ListAsync(CancellationToken cancellationToken) =>
        (await repository.ListAsync(cancellationToken)).Select(CommitteeDto.From).ToList();

    public async Task<CommitteeDto?> AddMemberAsync(Guid id, Guid userId, CommitteeRole role, CancellationToken cancellationToken)
    {
        if (await memberships.FindAsync(tenant.RequiredTenantId, userId, cancellationToken) is not { IsActive: true })
        {
            throw new MeetingDomainException("The user is not an active member of this organisation.");
        }

        return await ChangeAsync(id, c => c.AddMember(userId, role), cancellationToken);
    }

    public Task<CommitteeDto?> ChangeMemberRoleAsync(Guid id, Guid userId, CommitteeRole role, CancellationToken cancellationToken) =>
        ChangeAsync(id, c =>
        {
            if (c.ChangeRole(userId, role) is null)
            {
                throw new KeyNotFoundException("Committee member not found.");
            }
        }, cancellationToken);

    public Task<CommitteeDto?> RemoveMemberAsync(Guid id, Guid userId, CancellationToken cancellationToken) =>
        ChangeAsync(id, c =>
        {
            if (!c.RemoveMember(userId))
            {
                throw new KeyNotFoundException("Committee member not found.");
            }
        }, cancellationToken);

    private async Task<CommitteeDto?> ChangeAsync(Guid id, Action<Committee> change, CancellationToken cancellationToken)
    {
        if (await repository.GetAsync(id, cancellationToken) is not { } committee)
        {
            return null;
        }

        change(committee);
        await repository.SaveChangesAsync(cancellationToken);
        return CommitteeDto.From(committee);
    }
}
