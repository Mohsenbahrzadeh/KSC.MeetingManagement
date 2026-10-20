using KSC.MeetingManagement.Application.Tenants;
using KSC.MeetingManagement.Domain.Identity;
using KSC.MeetingManagement.Domain.Tenants;

namespace KSC.MeetingManagement.Application.Identity;

public enum AdminError
{
    Invalid = 1,
    NotFound,
    Conflict,
    Forbidden,
    Disabled,
}

public sealed record AdminResult<T>(T? Value, AdminError? Error = null, string? Message = null)
{
    public bool Succeeded => Error is null;
}

public sealed record SetupRequest(string? TenantName, string? Slug, string? TimeZoneId, string? Email, string? DisplayName, string? Password);

public sealed record SetupResult(Guid TenantId, Guid UserId);

public sealed record MemberDto(Guid UserId, string Email, string DisplayName, IReadOnlyList<string> Roles, bool MembershipActive, bool UserActive);

public interface IIdentityAdminStore
{
    Task<bool> AnyTenantAsync(CancellationToken cancellationToken);

    Task AddTenantAsync(Tenant tenant, CancellationToken cancellationToken);

    Task<IReadOnlyList<Membership>> ListMembershipsAsync(Guid tenantId, CancellationToken cancellationToken);

    /// <summary>Inserts or updates the membership of (tenant, user). Takes effect on <see cref="SaveChangesAsync"/>.</summary>
    Task SaveMembershipAsync(Membership membership, CancellationToken cancellationToken);

    Task SaveChangesAsync(CancellationToken cancellationToken);
}

public static class PasswordPolicy
{
    public const int MinLength = 10;
    public const int MaxLength = 128;

    public static string? Check(string? password, string email)
    {
        if (string.IsNullOrEmpty(password) || password.Length < MinLength)
        {
            return $"Password must be at least {MinLength} characters.";
        }

        if (password.Length > MaxLength)
        {
            return $"Password must be at most {MaxLength} characters.";
        }

        return string.Equals(password, email, StringComparison.OrdinalIgnoreCase) ? "Password must differ from the email." : null;
    }
}

/// <summary>
/// First-time installation and per-tenant user administration. Setup is one-shot (only while the system has no tenant);
/// administrators can add members, change their roles and (de)activate them, but cannot touch their own membership
/// and only a TenantOwner can grant, remove or modify the owner role.
/// </summary>
public sealed class IdentityAdminService(
    IIdentityAdminStore store,
    IUserRepository users,
    IMembershipStore memberships,
    IPasswordHasher hasher,
    ITenantContext tenant,
    ICurrentUser currentUser,
    TimeProvider clock)
{
    public async Task<AdminResult<SetupResult>> SetupAsync(SetupRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (await store.AnyTenantAsync(cancellationToken) || await users.AnyAsync(cancellationToken))
        {
            return new(null, AdminError.Conflict, "The system has already been set up.");
        }

        try
        {
            var email = User.NormalizeEmail(request.Email);
            if (PasswordPolicy.Check(request.Password, email) is { } weak)
            {
                return new(null, AdminError.Invalid, weak);
            }

            var tenantEntity = new Tenant(Guid.NewGuid(), request.Slug ?? string.Empty, request.TenantName ?? string.Empty, request.TimeZoneId ?? "Asia/Tehran");
            if (!IsValidTimeZone(tenantEntity.TimeZoneId))
            {
                return new(null, AdminError.Invalid, "Unknown time zone.");
            }

            var user = new User(Guid.NewGuid(), email, request.DisplayName ?? string.Empty, hasher.Hash(request.Password!), clock.GetUtcNow());
            await store.AddTenantAsync(tenantEntity, cancellationToken);
            await users.AddAsync(user, cancellationToken);
            await store.SaveMembershipAsync(new Membership(tenantEntity.Id, user.Id, [Roles.TenantOwner]), cancellationToken);
            await store.SaveChangesAsync(cancellationToken);
            return new(new SetupResult(tenantEntity.Id, user.Id));
        }
        catch (ArgumentException ex)
        {
            return new(null, AdminError.Invalid, ex.Message);
        }
    }

    public async Task<IReadOnlyList<MemberDto>> ListAsync(CancellationToken cancellationToken)
    {
        var members = await store.ListMembershipsAsync(tenant.RequiredTenantId, cancellationToken);
        var people = (await users.FindByIdsAsync(members.Select(m => m.UserId).ToList(), cancellationToken)).ToDictionary(u => u.Id);
        return members
            .Where(m => people.ContainsKey(m.UserId))
            .Select(m => ToDto(m, people[m.UserId]))
            .OrderBy(d => d.Email, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>Adds a person to this organisation. A new email creates the account (password required); an existing one only gains a membership.</summary>
    public async Task<AdminResult<MemberDto>> AddMemberAsync(string? email, string? displayName, string? password, IReadOnlyCollection<string>? roles, CancellationToken cancellationToken)
    {
        if (CheckRoles(roles) is { } roleProblem)
        {
            return new(null, AdminError.Invalid, roleProblem);
        }

        string normalized;
        try
        {
            normalized = User.NormalizeEmail(email);
        }
        catch (ArgumentException ex)
        {
            return new(null, AdminError.Invalid, ex.Message);
        }

        if (await CheckOwnerRuleAsync(roles!, cancellationToken) is { } denied)
        {
            return new(null, AdminError.Forbidden, denied);
        }

        var user = await users.FindByEmailAsync(normalized, cancellationToken);
        if (user is null)
        {
            if (PasswordPolicy.Check(password, normalized) is { } weak)
            {
                return new(null, AdminError.Invalid, weak);
            }

            user = new User(Guid.NewGuid(), normalized, displayName ?? string.Empty, hasher.Hash(password!), clock.GetUtcNow());
            await users.AddAsync(user, cancellationToken);
        }
        else if (await memberships.FindAsync(tenant.RequiredTenantId, user.Id, cancellationToken) is not null)
        {
            return new(null, AdminError.Conflict, "This person is already a member.");
        }

        var membership = new Membership(tenant.RequiredTenantId, user.Id, roles!);
        await store.SaveMembershipAsync(membership, cancellationToken);
        await store.SaveChangesAsync(cancellationToken);
        return new(ToDto(membership, user));
    }

    public Task<AdminResult<MemberDto>> SetRolesAsync(Guid userId, IReadOnlyCollection<string>? roles, CancellationToken cancellationToken) =>
        ChangeAsync(userId, roles, m => { }, replaceRoles: true, cancellationToken);

    public Task<AdminResult<MemberDto>> SetActiveAsync(Guid userId, bool active, CancellationToken cancellationToken) =>
        ChangeAsync(userId, null, m => { if (active) { m.Activate(); } else { m.Disable(); } }, replaceRoles: false, cancellationToken);

    private async Task<AdminResult<MemberDto>> ChangeAsync(Guid userId, IReadOnlyCollection<string>? roles, Action<Membership> mutate, bool replaceRoles, CancellationToken cancellationToken)
    {
        if (userId == currentUser.RequiredUserId)
        {
            return new(null, AdminError.Forbidden, "You cannot change your own membership.");
        }

        if (replaceRoles && CheckRoles(roles) is { } problem)
        {
            return new(null, AdminError.Invalid, problem);
        }

        var tenantId = tenant.RequiredTenantId;
        var current = (await store.ListMembershipsAsync(tenantId, cancellationToken)).FirstOrDefault(m => m.UserId == userId);
        if (current is null || await users.FindByIdAsync(userId, cancellationToken) is not { } person)
        {
            return new(null, AdminError.NotFound);
        }

        var ownerChange = replaceRoles
            ? current.RoleNames.Contains(Roles.TenantOwner) != roles!.Contains(Roles.TenantOwner, StringComparer.Ordinal)
            : current.RoleNames.Contains(Roles.TenantOwner);
        if (ownerChange && await CheckOwnerRuleAsync([Roles.TenantOwner], cancellationToken) is { } denied)
        {
            return new(null, AdminError.Forbidden, denied);
        }

        if (replaceRoles)
        {
            foreach (var role in current.RoleNames.Except(roles!, StringComparer.Ordinal).ToList())
            {
                current.RemoveRole(role);
            }

            foreach (var role in roles!)
            {
                current.AddRole(role);
            }
        }

        mutate(current);
        await store.SaveMembershipAsync(current, cancellationToken);
        await store.SaveChangesAsync(cancellationToken);
        return new(ToDto(current, person));
    }

    private static string? CheckRoles(IReadOnlyCollection<string>? roles) =>
        roles is null || roles.Count == 0 ? "At least one role is required."
        : roles.Any(r => !Roles.All.Contains(r, StringComparer.Ordinal)) ? "Unknown role."
        : null;

    /// <summary>Only an active TenantOwner may grant or change the owner role.</summary>
    private async Task<string?> CheckOwnerRuleAsync(IReadOnlyCollection<string> roles, CancellationToken cancellationToken)
    {
        if (!roles.Contains(Roles.TenantOwner, StringComparer.Ordinal))
        {
            return null;
        }

        var caller = await memberships.FindAsync(tenant.RequiredTenantId, currentUser.RequiredUserId, cancellationToken);
        return caller?.HasRole(Roles.TenantOwner) == true ? null : "Only an owner can manage the owner role.";
    }

    private static MemberDto ToDto(Membership m, User u) =>
        new(u.Id, u.Email, u.DisplayName, m.RoleNames.Order(StringComparer.Ordinal).ToList(), m.IsActive, u.IsActive);

    private static bool IsValidTimeZone(string id)
    {
        try
        {
            _ = TimeZoneInfo.FindSystemTimeZoneById(id);
            return true;
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            return false;
        }
    }
}
