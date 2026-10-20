using System.Security.Cryptography;
using System.Text;
using KSC.MeetingManagement.Application.Tenants;
using KSC.MeetingManagement.Domain.Identity;

namespace KSC.MeetingManagement.Application.Identity;

public sealed record AccessToken(string Token, DateTimeOffset ExpiresAtUtc);

public sealed record MembershipSummary(Guid TenantId, string TenantName, string TenantSlug, IReadOnlyList<string> Roles);

public sealed record UserProfile(Guid Id, string Email, string DisplayName, IReadOnlyList<MembershipSummary> Memberships);

public sealed record AuthTokens(AccessToken Access, string RefreshToken, DateTimeOffset RefreshExpiresAtUtc, UserProfile User);

public enum AuthFailure
{
    InvalidCredentials = 1,
    Locked,
    NotConfigured,
    InvalidRefreshToken,
}

public sealed record AuthResult(AuthTokens? Tokens, AuthFailure? Failure = null)
{
    public bool Succeeded => Tokens is not null;
}

public sealed class AuthOptions
{
    public int MaxFailedAttempts { get; set; } = 5;

    public TimeSpan LockDuration { get; set; } = TimeSpan.FromMinutes(15);

    public TimeSpan RefreshLifetime { get; set; } = TimeSpan.FromDays(14);

    public TimeSpan AccessLifetime { get; set; } = TimeSpan.FromMinutes(15);
}

public interface IPasswordHasher
{
    string Hash(string password);

    bool Verify(string password, string hash);
}

/// <summary>Signs short-lived access tokens. Returns null when sign-in is not configured.</summary>
public interface IAccessTokenIssuer
{
    bool IsConfigured { get; }

    AccessToken Issue(User user);
}

public interface IUserRepository
{
    Task<User?> FindByEmailAsync(string normalizedEmail, CancellationToken cancellationToken);

    Task<User?> FindByIdAsync(Guid id, CancellationToken cancellationToken);

    Task<bool> AnyAsync(CancellationToken cancellationToken);

    Task AddAsync(User user, CancellationToken cancellationToken);

    Task<RefreshToken?> FindRefreshAsync(string tokenHash, CancellationToken cancellationToken);

    Task<IReadOnlyList<RefreshToken>> ActiveRefreshTokensAsync(Guid userId, DateTimeOffset nowUtc, CancellationToken cancellationToken);

    Task AddRefreshAsync(RefreshToken token, CancellationToken cancellationToken);

    Task SaveChangesAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Sign-in with email and password, short-lived access tokens and rotating refresh tokens. Wrong email and wrong password look the same
/// (and cost the same time), accounts lock after repeated failures, and a reused refresh token revokes the whole family.
/// </summary>
public sealed class AuthService(
    IUserRepository users,
    IMembershipStore memberships,
    ITenantStore tenants,
    IPasswordHasher hasher,
    IAccessTokenIssuer tokens,
    AuthOptions options,
    TimeProvider clock)
{
    // Verified when the email is unknown so the response time does not reveal whether the account exists.
    private static readonly Lazy<string> DummyHashHolder = new(() => string.Empty);

    public async Task<AuthResult> LoginAsync(string? email, string? password, CancellationToken cancellationToken)
    {
        if (!tokens.IsConfigured)
        {
            return new AuthResult(null, AuthFailure.NotConfigured);
        }

        var now = clock.GetUtcNow();
        User? user = null;
        try
        {
            user = await users.FindByEmailAsync(User.NormalizeEmail(email), cancellationToken);
        }
        catch (ArgumentException)
        {
            // Not an email address: treated like an unknown account.
        }

        if (user is null || !user.IsActive)
        {
            _ = hasher.Verify(password ?? string.Empty, DummyHash());
            return new AuthResult(null, AuthFailure.InvalidCredentials);
        }

        if (user.IsLocked(now))
        {
            return new AuthResult(null, AuthFailure.Locked);
        }

        if (string.IsNullOrEmpty(password) || !hasher.Verify(password, user.PasswordHash))
        {
            user.RegisterFailure(now, options.MaxFailedAttempts, options.LockDuration);
            await users.SaveChangesAsync(cancellationToken);
            return new AuthResult(null, user.IsLocked(now) ? AuthFailure.Locked : AuthFailure.InvalidCredentials);
        }

        user.RegisterSuccess(now);
        var result = await IssueAsync(user, cancellationToken);
        await users.SaveChangesAsync(cancellationToken);
        return new AuthResult(result);
    }

    /// <summary>Exchanges a refresh token for a new pair. Using a token twice revokes every token of the user.</summary>
    public async Task<AuthResult> RefreshAsync(string? refreshToken, CancellationToken cancellationToken)
    {
        if (!tokens.IsConfigured)
        {
            return new AuthResult(null, AuthFailure.NotConfigured);
        }

        var now = clock.GetUtcNow();
        if (string.IsNullOrWhiteSpace(refreshToken) || await users.FindRefreshAsync(Hash(refreshToken), cancellationToken) is not { } stored)
        {
            return new AuthResult(null, AuthFailure.InvalidRefreshToken);
        }

        if (stored.Replaced)
        {
            foreach (var active in await users.ActiveRefreshTokensAsync(stored.UserId, now, cancellationToken))
            {
                active.Revoke(now);
            }

            await users.SaveChangesAsync(cancellationToken);
            return new AuthResult(null, AuthFailure.InvalidRefreshToken);
        }

        if (!stored.IsActive(now) || await users.FindByIdAsync(stored.UserId, cancellationToken) is not { IsActive: true } user)
        {
            return new AuthResult(null, AuthFailure.InvalidRefreshToken);
        }

        stored.Revoke(now, replaced: true);
        var result = await IssueAsync(user, cancellationToken);
        await users.SaveChangesAsync(cancellationToken);
        return new AuthResult(result);
    }

    /// <summary>Revokes one refresh token (sign out). Unknown tokens are ignored.</summary>
    public async Task LogoutAsync(string? refreshToken, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(refreshToken) && await users.FindRefreshAsync(Hash(refreshToken), cancellationToken) is { } stored)
        {
            stored.Revoke(clock.GetUtcNow());
            await users.SaveChangesAsync(cancellationToken);
        }
    }

    public async Task<UserProfile?> ProfileAsync(Guid userId, CancellationToken cancellationToken) =>
        await users.FindByIdAsync(userId, cancellationToken) is { IsActive: true } user ? await BuildProfileAsync(user, cancellationToken) : null;

    private async Task<AuthTokens> IssueAsync(User user, CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        var raw = Convert.ToBase64String(RandomNumberGenerator.GetBytes(48)).Replace('+', '-').Replace('/', '_').TrimEnd('=');
        var expires = now + options.RefreshLifetime;
        await users.AddRefreshAsync(new RefreshToken(Guid.NewGuid(), user.Id, Hash(raw), now, expires), cancellationToken);
        return new AuthTokens(tokens.Issue(user), raw, expires, await BuildProfileAsync(user, cancellationToken));
    }

    private async Task<UserProfile> BuildProfileAsync(User user, CancellationToken cancellationToken)
    {
        var summaries = new List<MembershipSummary>();
        foreach (var membership in await memberships.ListForUserAsync(user.Id, cancellationToken))
        {
            if (membership.IsActive && await tenants.FindByIdAsync(membership.TenantId, cancellationToken) is { IsActive: true } tenant)
            {
                summaries.Add(new MembershipSummary(tenant.Id, tenant.Name, tenant.Slug, membership.RoleNames.Order(StringComparer.Ordinal).ToList()));
            }
        }

        return new UserProfile(user.Id, user.Email, user.DisplayName, summaries);
    }

    private string DummyHash() => DummyHashHolder.Value.Length > 0 ? DummyHashHolder.Value : hasher.Hash("dummy-password-for-timing");

    /// <summary>SHA-256 of the token; refresh tokens are 384 random bits, so a fast hash is enough and lets us look them up directly.</summary>
    public static string Hash(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
}
