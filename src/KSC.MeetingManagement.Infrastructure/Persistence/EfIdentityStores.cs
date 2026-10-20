using KSC.MeetingManagement.Application.Identity;
using KSC.MeetingManagement.Application.Tenants;
using KSC.MeetingManagement.Domain.Identity;
using KSC.MeetingManagement.Domain.Tenants;
using KSC.MeetingManagement.Infrastructure.Tenants;
using Microsoft.EntityFrameworkCore;

namespace KSC.MeetingManagement.Infrastructure.Persistence;

/// <summary>Database row for a membership; roles are stored as a comma separated list (role names never contain commas).</summary>
public sealed class MembershipRow
{
    public Guid TenantId { get; set; }

    public Guid UserId { get; set; }

    public string Roles { get; set; } = string.Empty;

    public MembershipStatus Status { get; set; }

    public static MembershipRow From(Membership m)
    {
        ArgumentNullException.ThrowIfNull(m);
        return new MembershipRow
        {
            TenantId = m.TenantId,
            UserId = m.UserId,
            Roles = string.Join(',', m.RoleNames.Order(StringComparer.Ordinal)),
            Status = m.Status,
        };
    }

    public Membership ToDomain() =>
        new(TenantId, UserId, Roles.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries), Status);
}

/// <summary>Tenants from the database. Not tenant-filtered: they are what the tenant filter is built from.</summary>
public sealed class EfTenantStore(KscDbContext db) : ITenantStore
{
    public Task<Tenant?> FindByIdAsync(Guid tenantId, CancellationToken cancellationToken) =>
        db.Tenants.AsNoTracking().FirstOrDefaultAsync(t => t.Id == tenantId, cancellationToken);
}

public sealed class EfMembershipStore(KscDbContext db) : IMembershipStore
{
    public async Task<Membership?> FindAsync(Guid tenantId, Guid userId, CancellationToken cancellationToken) =>
        (await db.Memberships.AsNoTracking().FirstOrDefaultAsync(m => m.TenantId == tenantId && m.UserId == userId, cancellationToken))?.ToDomain();

    public async Task<IReadOnlyList<Membership>> ListForUserAsync(Guid userId, CancellationToken cancellationToken) =>
        (await db.Memberships.AsNoTracking().Where(m => m.UserId == userId).ToListAsync(cancellationToken)).Select(m => m.ToDomain()).ToList();
}

/// <summary>Looks in the in-memory store first (tests seed it), then in the database.</summary>
public sealed class CompositeTenantStore(InMemoryTenantStore memory, EfTenantStore database) : ITenantStore
{
    public async Task<Tenant?> FindByIdAsync(Guid tenantId, CancellationToken cancellationToken) =>
        await memory.FindByIdAsync(tenantId, cancellationToken) ?? await database.FindByIdAsync(tenantId, cancellationToken);
}

public sealed class CompositeMembershipStore(InMemoryMembershipStore memory, EfMembershipStore database) : IMembershipStore
{
    public async Task<Membership?> FindAsync(Guid tenantId, Guid userId, CancellationToken cancellationToken) =>
        await memory.FindAsync(tenantId, userId, cancellationToken) ?? await database.FindAsync(tenantId, userId, cancellationToken);

    public async Task<IReadOnlyList<Membership>> ListForUserAsync(Guid userId, CancellationToken cancellationToken)
    {
        var fromMemory = await memory.ListForUserAsync(userId, cancellationToken);
        var fromDb = await database.ListForUserAsync(userId, cancellationToken);
        return [.. fromMemory, .. fromDb.Where(d => !fromMemory.Any(m => m.TenantId == d.TenantId))];
    }
}

public sealed class EfUserRepository(KscDbContext db) : IUserRepository
{
    public Task<User?> FindByEmailAsync(string normalizedEmail, CancellationToken cancellationToken) =>
        db.Users.FirstOrDefaultAsync(u => u.Email == normalizedEmail, cancellationToken);

    public Task<User?> FindByIdAsync(Guid id, CancellationToken cancellationToken) =>
        db.Users.FirstOrDefaultAsync(u => u.Id == id, cancellationToken);

    public Task<bool> AnyAsync(CancellationToken cancellationToken) => db.Users.AnyAsync(cancellationToken);

    public async Task<IReadOnlyList<User>> FindByIdsAsync(IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken) =>
        await db.Users.AsNoTracking().Where(u => ids.Contains(u.Id)).ToListAsync(cancellationToken);

    public async Task AddAsync(User user, CancellationToken cancellationToken) => await db.Users.AddAsync(user, cancellationToken);

    public Task<RefreshToken?> FindRefreshAsync(string tokenHash, CancellationToken cancellationToken) =>
        db.RefreshTokens.FirstOrDefaultAsync(t => t.TokenHash == tokenHash, cancellationToken);

    public async Task<IReadOnlyList<RefreshToken>> ActiveRefreshTokensAsync(Guid userId, DateTimeOffset nowUtc, CancellationToken cancellationToken) =>
        await db.RefreshTokens.Where(t => t.UserId == userId && t.RevokedAtUtc == null && t.ExpiresAtUtc > nowUtc).ToListAsync(cancellationToken);

    public async Task AddRefreshAsync(RefreshToken token, CancellationToken cancellationToken) =>
        await db.RefreshTokens.AddAsync(token, cancellationToken);

    public Task SaveChangesAsync(CancellationToken cancellationToken) => db.SaveChangesAsync(cancellationToken);
}

public sealed class EfIdentityAdminStore(KscDbContext db) : IIdentityAdminStore
{
    public Task<bool> AnyTenantAsync(CancellationToken cancellationToken) => db.Tenants.AnyAsync(cancellationToken);

    public async Task AddTenantAsync(Tenant tenant, CancellationToken cancellationToken) => await db.Tenants.AddAsync(tenant, cancellationToken);

    public async Task<IReadOnlyList<Membership>> ListMembershipsAsync(Guid tenantId, CancellationToken cancellationToken) =>
        (await db.Memberships.AsNoTracking().Where(m => m.TenantId == tenantId).ToListAsync(cancellationToken)).Select(m => m.ToDomain()).ToList();

    public async Task SaveMembershipAsync(Membership membership, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(membership);
        var row = await db.Memberships.FirstOrDefaultAsync(m => m.TenantId == membership.TenantId && m.UserId == membership.UserId, cancellationToken);
        var updated = MembershipRow.From(membership);
        if (row is null)
        {
            await db.Memberships.AddAsync(updated, cancellationToken);
        }
        else
        {
            row.Roles = updated.Roles;
            row.Status = updated.Status;
        }
    }

    public Task SaveChangesAsync(CancellationToken cancellationToken) => db.SaveChangesAsync(cancellationToken);
}
