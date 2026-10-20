namespace KSC.MeetingManagement.Domain.Identity;

public enum MembershipStatus
{
    Invited = 1,
    Active = 2,
    Disabled = 3,
}

/// <summary>Role names used by authorization policies (see requirements section 4).</summary>
public static class Roles
{
    public const string TenantOwner = "TenantOwner";
    public const string TenantAdministrator = "TenantAdministrator";
    public const string WorkflowAdministrator = "WorkflowAdministrator";
    public const string MeetingOrganizer = "MeetingOrganizer";
    public const string Secretary = "Secretary";
    public const string Participant = "Participant";
    public const string Approver = "Approver";
    public const string Auditor = "Auditor";

    public static IReadOnlyCollection<string> All { get; } =
    [
        TenantOwner, TenantAdministrator, WorkflowAdministrator, MeetingOrganizer, Secretary, Participant, Approver, Auditor,
    ];
}

/// <summary>Link between a user and a tenant, carrying the user's roles in that tenant.</summary>
public sealed class Membership
{
    private readonly HashSet<string> _roles;

    public Membership(Guid tenantId, Guid userId, IEnumerable<string> roles, MembershipStatus status = MembershipStatus.Active)
    {
        if (tenantId == Guid.Empty || userId == Guid.Empty)
        {
            throw new ArgumentException("Tenant and user ids are required.");
        }

        TenantId = tenantId;
        UserId = userId;
        _roles = new HashSet<string>(roles, StringComparer.Ordinal);
        Status = status;
    }

    public Guid TenantId { get; }
    public Guid UserId { get; }
    public MembershipStatus Status { get; private set; }
    public IReadOnlyCollection<string> RoleNames => _roles;

    public bool IsActive => Status == MembershipStatus.Active;

    public bool HasRole(string role) => IsActive && _roles.Contains(role);

    public void Disable() => Status = MembershipStatus.Disabled;

    public void Activate() => Status = MembershipStatus.Active;

    public void AddRole(string role) => _roles.Add(role);

    public void RemoveRole(string role) => _roles.Remove(role);
}
