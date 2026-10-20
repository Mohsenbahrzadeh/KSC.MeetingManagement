namespace KSC.MeetingManagement.Domain.Identity;

public static class Permissions
{
    public const string MeetingsView = "meetings.view";
    public const string MeetingsCreate = "meetings.create";
    public const string MeetingsEdit = "meetings.edit";
    public const string MeetingsCancel = "meetings.cancel";
    public const string MinutesWrite = "minutes.write";
    public const string MinutesApprove = "minutes.approve";
    public const string UsersManage = "users.manage";
    public const string OrganizationManage = "organization.manage";
    public const string WorkflowsManage = "workflows.manage";
    public const string AuditView = "audit.view";
    public const string ReportsView = "reports.view";

    public static IReadOnlyCollection<string> All { get; } =
    [
        MeetingsView, MeetingsCreate, MeetingsEdit, MeetingsCancel, MinutesWrite, MinutesApprove,
        UsersManage, OrganizationManage, WorkflowsManage, AuditView, ReportsView,
    ];
}

/// <summary>Static role to permission mapping (requirements section 4). Roles are tenant-scoped via Membership.</summary>
public static class RolePermissions
{
    private static readonly Dictionary<string, string[]> Map = new(StringComparer.Ordinal)
    {
        [Roles.TenantOwner] = [.. Permissions.All],
        [Roles.TenantAdministrator] =
        [
            Permissions.MeetingsView, Permissions.MeetingsCreate, Permissions.MeetingsEdit, Permissions.MeetingsCancel,
            Permissions.UsersManage, Permissions.OrganizationManage, Permissions.ReportsView,
        ],
        [Roles.WorkflowAdministrator] = [Permissions.WorkflowsManage, Permissions.MeetingsView],
        [Roles.MeetingOrganizer] =
            [Permissions.MeetingsView, Permissions.MeetingsCreate, Permissions.MeetingsEdit, Permissions.MeetingsCancel],
        [Roles.Secretary] = [Permissions.MeetingsView, Permissions.MinutesWrite],
        [Roles.Participant] = [Permissions.MeetingsView],
        [Roles.Approver] = [Permissions.MeetingsView, Permissions.MinutesApprove],
        [Roles.Auditor] = [Permissions.MeetingsView, Permissions.AuditView, Permissions.ReportsView],
    };

    public static bool Grants(string role, string permission) =>
        Map.TryGetValue(role, out var permissions) && permissions.Contains(permission, StringComparer.Ordinal);

    public static bool Grants(Membership membership, string permission)
    {
        ArgumentNullException.ThrowIfNull(membership);
        return membership.IsActive && membership.RoleNames.Any(r => Grants(r, permission));
    }
}
