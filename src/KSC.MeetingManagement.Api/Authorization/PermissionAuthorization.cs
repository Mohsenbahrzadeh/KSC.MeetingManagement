using System.Security.Claims;
using KSC.MeetingManagement.Application.Actions;
using KSC.MeetingManagement.Application.Committees;
using KSC.MeetingManagement.Application.Identity;
using KSC.MeetingManagement.Application.Meetings;
using KSC.MeetingManagement.Application.Participants;
using KSC.MeetingManagement.Application.Tenants;
using KSC.MeetingManagement.Domain.Identity;
using KSC.MeetingManagement.Domain.Meetings;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;

namespace KSC.MeetingManagement.Api.Authorization;

public sealed class PermissionRequirement(string permission) : IAuthorizationRequirement
{
    public string Permission { get; } = permission;
}

/// <summary>Builds "perm:xxx" policies on demand so each permission needs no manual registration.</summary>
public sealed class PermissionPolicyProvider(IOptions<AuthorizationOptions> options) : IAuthorizationPolicyProvider
{
    private readonly DefaultAuthorizationPolicyProvider _fallback = new(options);

    public Task<AuthorizationPolicy> GetDefaultPolicyAsync() => _fallback.GetDefaultPolicyAsync();

    public Task<AuthorizationPolicy?> GetFallbackPolicyAsync() => _fallback.GetFallbackPolicyAsync();

    public Task<AuthorizationPolicy?> GetPolicyAsync(string policyName)
    {
        if (!policyName.StartsWith(HasPermissionAttribute.PolicyPrefix, StringComparison.Ordinal))
        {
            return _fallback.GetPolicyAsync(policyName);
        }

        var policy = new AuthorizationPolicyBuilder()
            .RequireAuthenticatedUser()
            .AddRequirements(new PermissionRequirement(policyName[HasPermissionAttribute.PolicyPrefix.Length..]))
            .Build();
        return Task.FromResult<AuthorizationPolicy?>(policy);
    }
}

/// <summary>Succeeds only for an active membership in the request tenant whose roles grant the permission.</summary>
public sealed class PermissionHandler(
    ITenantContext tenant,
    IMembershipStore memberships,
    IHttpContextAccessor httpContextAccessor,
    IMeetingRepository meetings,
    IParticipantRepository participants,
    ICommitteeRepository committees)
    : AuthorizationHandler<PermissionRequirement>
{
    protected override async Task HandleRequirementAsync(AuthorizationHandlerContext context, PermissionRequirement requirement)
    {
        var tenantId = tenant.Tenant?.Id;
        var sub = context.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? context.User.FindFirstValue("sub");
        if (tenantId is null || !Guid.TryParse(sub, out var userId))
        {
            return;
        }

        var membership = await memberships.FindAsync(tenantId.Value, userId, CancellationToken.None);
        if (membership is null || !membership.IsActive) return;

        var route = httpContextAccessor.HttpContext?.Request;
        if (requirement.Permission == Permissions.MeetingsView && route?.Path.Value is { } path
            && path.Equals("/api/v1/meetings/search", StringComparison.OrdinalIgnoreCase))
        {
            var isAdmin = membership.HasRole(Roles.TenantOwner) || membership.HasRole(Roles.TenantAdministrator);
            var isCommitteeOfficer = await CanManageAnyCommitteeAsync(userId, CancellationToken.None);
            if (isAdmin || isCommitteeOfficer || RolePermissions.Grants(membership, requirement.Permission)) context.Succeed(requirement);
            return;
        }

        if (requirement.Permission == Permissions.MeetingsView && route?.Path.Value is { } meetingListPath
            && meetingListPath.Equals("/api/v1/meetings", StringComparison.OrdinalIgnoreCase))
        {
            var isAdmin = membership.HasRole(Roles.TenantOwner) || membership.HasRole(Roles.TenantAdministrator);
            var isCommitteeMember = await CanSeeAnyCommitteeAsync(userId, CancellationToken.None);
            if (isAdmin || isCommitteeMember || RolePermissions.Grants(membership, requirement.Permission)) context.Succeed(requirement);
            return;
        }

        if (requirement.Permission == Permissions.MeetingsView && TryGetMeetingId(route, out var viewMeetingId))
        {
            var meeting = await meetings.GetAsync(viewMeetingId, CancellationToken.None);
            if (meeting is null)
            {
                // Let tenant-scoped endpoint services return their normal 404 for missing/foreign meetings.
                if (RolePermissions.Grants(membership, requirement.Permission)) context.Succeed(requirement);
                return;
            }

            // Keep the meeting details' 404 and action-owner checks in their application services.
            if (IsMeetingDetailsRequest(route))
            {
                context.Succeed(requirement);
                return;
            }

            if (IsActionRequest(route))
            {
                // Action visibility and ownership are enforced by ActionItemService; preserve its 404
                // behavior for unrelated users instead of rejecting at the coarse permission layer.
                context.Succeed(requirement);
                return;
            }

            var isVisible = membership.HasRole(Roles.TenantOwner)
                || membership.HasRole(Roles.TenantAdministrator)
                || meeting.OrganizerId == userId
                || await IsInvitedAsync(viewMeetingId, userId, CancellationToken.None)
                || await IsCommitteeMemberAsync(meeting, userId, chairOrSecretaryOnly: false, CancellationToken.None);
            if (isVisible) context.Succeed(requirement);
            return;
        }

        if (TryGetMeetingId(route, out var meetingId) && requirement.Permission is
            (Permissions.MeetingsEdit or Permissions.MeetingsCancel or Permissions.ActionsManage
                or Permissions.MinutesWrite or Permissions.MinutesApprove))
        {
            var meeting = await meetings.GetAsync(meetingId, CancellationToken.None);
            if (meeting is null)
            {
                if (RolePermissions.Grants(membership, requirement.Permission)) context.Succeed(requirement);
                return;
            }

            var isVisible = (membership.HasRole(Roles.TenantOwner) || membership.HasRole(Roles.TenantAdministrator))
                || meeting.OrganizerId == userId
                || await IsInvitedAsync(meetingId, userId, CancellationToken.None)
                || await IsCommitteeMemberAsync(meeting, userId, chairOrSecretaryOnly: false, CancellationToken.None);
            var isManager = await CanManageAsync(meeting, userId, membership, CancellationToken.None);

            if (requirement.Permission is Permissions.MeetingsEdit or Permissions.MeetingsCancel)
            {
                var legacyNonCommitteeManager = meeting.CommitteeId is null
                    && RolePermissions.Grants(membership, requirement.Permission)
                    && membership.HasRole(Roles.Secretary);
                if (isManager || legacyNonCommitteeManager)
                    context.Succeed(requirement);
                return;
            }

            if (requirement.Permission == Permissions.ActionsManage)
            {
                var legacyNonCommitteeManager = meeting.CommitteeId is null
                    && RolePermissions.Grants(membership, requirement.Permission)
                    && membership.HasRole(Roles.Secretary);
                if (isManager || legacyNonCommitteeManager)
                    context.Succeed(requirement);
                return;
            }

            if (requirement.Permission is Permissions.MinutesWrite or Permissions.MinutesApprove)
            {
                var legacyNonCommitteeMinutesAuthority = meeting.CommitteeId is null
                    && (membership.HasRole(Roles.Secretary)
                        || requirement.Permission == Permissions.MinutesApprove && membership.HasRole(Roles.Approver));
                var hasCommitteeMinutesScope = meeting.CommitteeId is { } committeeId
                    && await IsCommitteeOfficerAsync(committeeId, userId, CancellationToken.None);
                if ((isVisible || isManager || legacyNonCommitteeMinutesAuthority || hasCommitteeMinutesScope)
                    && RolePermissions.Grants(membership, requirement.Permission)) context.Succeed(requirement);
                return;
            }

            if (!isVisible) return;
        }

        if (requirement.Permission == Permissions.MeetingsView
            && route?.Path.Value?.Equals("/api/v1/actions/mine", StringComparison.OrdinalIgnoreCase) == true)
        {
            if (RolePermissions.Grants(membership, requirement.Permission)) context.Succeed(requirement);
            return;
        }

        if (requirement.Permission == Permissions.ReportsView
            && route?.Path.Value?.Equals("/api/v1/actions/overdue", StringComparison.OrdinalIgnoreCase) == true)
        {
            if (RolePermissions.Grants(membership, requirement.Permission)) context.Succeed(requirement);
            return;
        }

        if ((requirement.Permission is Permissions.MeetingsCreate or Permissions.MeetingsEdit)
            && await CanManageAnyCommitteeAsync(userId, CancellationToken.None))
        {
            context.Succeed(requirement);
            return;
        }

        if (RolePermissions.Grants(membership, requirement.Permission))
        {
            context.Succeed(requirement);
        }
    }

    private async Task<bool> IsInvitedAsync(Guid meetingId, Guid userId, CancellationToken cancellationToken) =>
        (await participants.ListAsync(meetingId, cancellationToken)).Any(participant =>
            participant.UserId == userId && participant.Status is InvitationStatus.Invited or InvitationStatus.Accepted);

    private async Task<bool> CanManageAsync(Meeting meeting, Guid userId, Membership membership, CancellationToken cancellationToken)
    {
        if (membership.HasRole(Roles.TenantOwner)
            || membership.HasRole(Roles.TenantAdministrator)) return true;

        // A tenant meeting organizer may manage their own non-committee meeting, but committee meetings
        // are managed only by that committee's chair/secretary (or a tenant administrator).
        if (meeting.CommitteeId is null && meeting.OrganizerId == userId)
            return membership.HasRole(Roles.MeetingOrganizer);

        return await IsCommitteeMemberAsync(meeting, userId, chairOrSecretaryOnly: true, cancellationToken);
    }

    private async Task<bool> IsCommitteeMemberAsync(Meeting meeting, Guid userId, bool chairOrSecretaryOnly, CancellationToken cancellationToken)
    {
        if (meeting.CommitteeId is not { } committeeId) return false;
        var committee = await committees.GetAsync(committeeId, cancellationToken);
        return committee is { IsActive: true } && committee.Members.Any(member => member.UserId == userId
            && (!chairOrSecretaryOnly || member.Role is CommitteeRole.Chair or CommitteeRole.Secretary));
    }

    private async Task<bool> CanManageAnyCommitteeAsync(Guid userId, CancellationToken cancellationToken)
    {
        var all = await committees.ListAsync(cancellationToken);
        return all.Any(committee => committee.IsActive && committee.Members.Any(member =>
            member.UserId == userId && member.Role is CommitteeRole.Chair or CommitteeRole.Secretary));
    }

    private async Task<bool> IsCommitteeOfficerAsync(Guid committeeId, Guid userId, CancellationToken cancellationToken)
    {
        var committee = await committees.GetAsync(committeeId, cancellationToken);
        return committee is { IsActive: true } && committee.Members.Any(member => member.UserId == userId
            && member.Role is CommitteeRole.Chair or CommitteeRole.Secretary);
    }

    private async Task<bool> CanSeeAnyCommitteeAsync(Guid userId, CancellationToken cancellationToken)
    {
        var all = await committees.ListAsync(cancellationToken);
        return all.Any(committee => committee.IsActive && committee.Members.Any(member => member.UserId == userId));
    }

    private static bool TryGetMeetingId(HttpRequest? request, out Guid meetingId)
    {
        if (request is not null
            && Guid.TryParse(request.RouteValues["meetingId"]?.ToString(), out meetingId)) return true;

        // The root meeting endpoints use {id}; do not treat unrelated GUIDs such as actionId or itemId
        // as a meeting identifier. Other meeting-bound endpoints use {meetingId} above.
        var segments = request?.Path.Value?.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments is { Length: >= 4 }
            && segments[0].Equals("api", StringComparison.OrdinalIgnoreCase)
            && segments[1] == "v1"
            && segments[2].Equals("meetings", StringComparison.OrdinalIgnoreCase)
            && Guid.TryParse(segments[3], out meetingId)) return true;

        meetingId = Guid.Empty;
        return false;
    }

    private static bool IsMeetingDetailsRequest(HttpRequest? request)
    {
        var segments = request?.Path.Value?.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return request?.Method == HttpMethods.Get
            && segments is { Length: 4 }
            && segments[0].Equals("api", StringComparison.OrdinalIgnoreCase)
            && segments[1].Equals("v1", StringComparison.OrdinalIgnoreCase)
            && segments[2].Equals("meetings", StringComparison.OrdinalIgnoreCase)
            && Guid.TryParse(segments[3], out _);
    }

    private static bool IsActionRequest(HttpRequest? request) =>
        request?.Path.Value?.Contains("/actions", StringComparison.OrdinalIgnoreCase) == true;
}
