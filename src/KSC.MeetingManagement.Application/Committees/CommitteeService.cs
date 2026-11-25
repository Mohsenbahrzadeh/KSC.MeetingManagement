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
    public async Task<CommitteeDto> CreateAsync(string name, string? description, IReadOnlyCollection<Guid>? memberUserIds, IReadOnlyCollection<Guid>? secretaryUserIds, CancellationToken cancellationToken)
    {
        var members = await ValidateMembersAsync(memberUserIds, cancellationToken);
        var secretaries = await ValidateSecretariesAsync(secretaryUserIds, cancellationToken);
        if (secretaries.Count == 0 || !secretaries.IsSubsetOf(members))
        {
            throw new MeetingDomainException("Select at least one secretary, and make sure every secretary is also a committee member.");
        }

        var committee = new Committee(Guid.NewGuid(), tenant.RequiredTenantId, name, description);
        foreach (var userId in members)
        {
            committee.AddMember(userId, secretaries.Contains(userId) ? CommitteeRole.Secretary : CommitteeRole.Member);
        }

        await repository.AddAsync(committee, cancellationToken);
        await repository.SaveChangesAsync(cancellationToken);
        return CommitteeDto.From(committee);
    }

    public async Task<CommitteeDto?> UpdateAsync(Guid id, string name, string? description, bool isActive, IReadOnlyCollection<Guid>? memberUserIds, IReadOnlyCollection<Guid>? secretaryUserIds, CancellationToken cancellationToken)
    {
        if (await repository.GetAsync(id, cancellationToken) is not { } committee)
        {
            return null;
        }

        var members = await ValidateMembersAsync(memberUserIds, cancellationToken);
        var secretaries = await ValidateSecretariesAsync(secretaryUserIds, cancellationToken);
        if (secretaries.Count == 0 || !secretaries.IsSubsetOf(members))
        {
            throw new MeetingDomainException("Select at least one secretary, and make sure every secretary is also a committee member.");
        }

        committee.Update(name, description, isActive);
        foreach (var existing in committee.Members.Where(m => !members.Contains(m.UserId)).ToList())
        {
            committee.RemoveMember(existing.UserId);
        }

        foreach (var userId in members)
        {
            var existing = committee.Members.FirstOrDefault(m => m.UserId == userId);
            var desiredRole = secretaries.Contains(userId) ? CommitteeRole.Secretary : CommitteeRole.Member;
            if (existing is null) committee.AddMember(userId, desiredRole);
            else if (existing.Role != CommitteeRole.Chair) committee.ChangeRole(userId, desiredRole);
        }

        await repository.SaveChangesAsync(cancellationToken);
        return CommitteeDto.From(committee);
    }

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

    private static Task<HashSet<Guid>> ValidateSecretariesAsync(IReadOnlyCollection<Guid>? secretaryUserIds, CancellationToken cancellationToken)
    {
        var secretaries = secretaryUserIds?.ToHashSet() ?? [];
        if (secretaries.Contains(Guid.Empty))
        {
            throw new MeetingDomainException("Secretary user ids are required.");
        }

        return Task.FromResult(secretaries);
    }

    private async Task<HashSet<Guid>> ValidateMembersAsync(IReadOnlyCollection<Guid>? memberUserIds, CancellationToken cancellationToken)
    {
        var members = memberUserIds?.ToHashSet() ?? [];
        if (members.Contains(Guid.Empty))
        {
            throw new MeetingDomainException("Committee member user ids are required.");
        }

        foreach (var userId in members)
        {
            if (await memberships.FindAsync(tenant.RequiredTenantId, userId, cancellationToken) is not { IsActive: true })
            {
                throw new MeetingDomainException("Every committee member must be an active member of this organisation.");
            }
        }

        return members;
    }

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
