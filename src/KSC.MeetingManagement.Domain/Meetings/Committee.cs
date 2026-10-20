namespace KSC.MeetingManagement.Domain.Meetings;

public enum CommitteeRole
{
    Member = 1,
    Secretary = 2,
    Chair = 3,
}

public sealed class CommitteeMember
{
    public CommitteeMember(Guid committeeId, Guid tenantId, Guid userId, CommitteeRole role)
    {
        CommitteeId = committeeId;
        TenantId = tenantId;
        UserId = userId;
        Role = role;
    }

    public Guid CommitteeId { get; }
    public Guid TenantId { get; }
    public Guid UserId { get; }
    public CommitteeRole Role { get; internal set; }
}

/// <summary>A standing committee of a tenant with a set of members; at most one member is Chair.</summary>
public sealed class Committee
{
    private readonly List<CommitteeMember> _members = [];

    public Committee(Guid id, Guid tenantId, string name, string? description)
    {
        if (id == Guid.Empty || tenantId == Guid.Empty)
        {
            throw new MeetingDomainException("Committee and tenant ids are required.");
        }

        Id = id;
        TenantId = tenantId;
        Name = RequireName(name);
        Description = description?.Trim();
        IsActive = true;
    }

    public Guid Id { get; }
    public Guid TenantId { get; }
    public string Name { get; private set; }
    public string? Description { get; private set; }
    public bool IsActive { get; private set; }
    public IReadOnlyCollection<CommitteeMember> Members => _members;

    public void Update(string name, string? description, bool isActive)
    {
        Name = RequireName(name);
        Description = description?.Trim();
        IsActive = isActive;
    }

    public CommitteeMember AddMember(Guid userId, CommitteeRole role)
    {
        if (userId == Guid.Empty)
        {
            throw new MeetingDomainException("User id is required.");
        }

        if (_members.Any(m => m.UserId == userId))
        {
            throw new MeetingDomainException("The user is already a committee member.");
        }

        EnsureSingleChair(userId, role);
        var member = new CommitteeMember(Id, TenantId, userId, role);
        _members.Add(member);
        return member;
    }

    public CommitteeMember? ChangeRole(Guid userId, CommitteeRole role)
    {
        var member = _members.FirstOrDefault(m => m.UserId == userId);
        if (member is null)
        {
            return null;
        }

        EnsureSingleChair(userId, role);
        member.Role = role;
        return member;
    }

    public bool RemoveMember(Guid userId) => _members.RemoveAll(m => m.UserId == userId) > 0;

    private void EnsureSingleChair(Guid userId, CommitteeRole role)
    {
        if (role == CommitteeRole.Chair && _members.Any(m => m.Role == CommitteeRole.Chair && m.UserId != userId))
        {
            throw new MeetingDomainException("The committee already has a chair.");
        }
    }

    private static string RequireName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new MeetingDomainException("Committee name is required.");
        }

        return name.Trim();
    }
}
