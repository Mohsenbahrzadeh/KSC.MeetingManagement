namespace KSC.MeetingManagement.Domain.Identity;

/// <summary>
/// A person who can sign in. Users belong to no organisation by themselves; their roles come from memberships.
/// Repeated wrong passwords lock the account for a while (the lock never reveals whether the email exists to the caller).
/// </summary>
public sealed class User
{
    public const int MaxEmailLength = 320;
    public const int MaxNameLength = 200;

    public User(Guid id, string email, string displayName, string passwordHash, DateTimeOffset createdAtUtc)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException("User id is required.", nameof(id));
        }

        Id = id;
        Email = NormalizeEmail(email);
        DisplayName = string.IsNullOrWhiteSpace(displayName) ? Email : displayName.Trim()[..Math.Min(displayName.Trim().Length, MaxNameLength)];
        PasswordHash = RequireHash(passwordHash);
        CreatedAtUtc = createdAtUtc;
        IsActive = true;
    }

    public Guid Id { get; }

    /// <summary>Lower-case, trimmed; unique across the system.</summary>
    public string Email { get; }

    public string DisplayName { get; private set; }
    public string PasswordHash { get; private set; }
    public bool IsActive { get; private set; }
    public int FailedLoginCount { get; private set; }
    public DateTimeOffset? LockedUntilUtc { get; private set; }
    public DateTimeOffset CreatedAtUtc { get; }
    public DateTimeOffset? LastLoginAtUtc { get; private set; }

    public static string NormalizeEmail(string? email)
    {
        var normalized = (email ?? string.Empty).Trim().ToLowerInvariant();
        var at = normalized.IndexOf('@', StringComparison.Ordinal);
        if (normalized.Length is 0 or > MaxEmailLength || at < 1 || at != normalized.LastIndexOf('@') || at == normalized.Length - 1
            || normalized.Any(c => char.IsWhiteSpace(c) || char.IsControl(c)))
        {
            throw new ArgumentException("A valid email address is required.", nameof(email));
        }

        return normalized;
    }

    public bool IsLocked(DateTimeOffset nowUtc) => LockedUntilUtc is { } until && until > nowUtc;

    /// <summary>Counts a wrong password; locks the account for <paramref name="lockDuration"/> after <paramref name="maxAttempts"/> in a row.</summary>
    public void RegisterFailure(DateTimeOffset nowUtc, int maxAttempts, TimeSpan lockDuration)
    {
        FailedLoginCount++;
        if (FailedLoginCount >= maxAttempts)
        {
            LockedUntilUtc = nowUtc + lockDuration;
            FailedLoginCount = 0;
        }
    }

    public void RegisterSuccess(DateTimeOffset nowUtc)
    {
        FailedLoginCount = 0;
        LockedUntilUtc = null;
        LastLoginAtUtc = nowUtc;
    }

    public void ChangePassword(string passwordHash)
    {
        PasswordHash = RequireHash(passwordHash);
        FailedLoginCount = 0;
        LockedUntilUtc = null;
    }

    public void Rename(string displayName)
    {
        if (!string.IsNullOrWhiteSpace(displayName))
        {
            DisplayName = displayName.Trim()[..Math.Min(displayName.Trim().Length, MaxNameLength)];
        }
    }

    public void Deactivate() => IsActive = false;

    public void Activate() => IsActive = true;

    private static string RequireHash(string hash) =>
        string.IsNullOrWhiteSpace(hash) ? throw new ArgumentException("A password hash is required.", nameof(hash)) : hash;
}

/// <summary>
/// A long-lived sign-in credential, stored only as a hash. Each use replaces it with a new one (rotation); presenting an already
/// replaced token means it was stolen or copied, so every token of that user is revoked.
/// </summary>
public sealed class RefreshToken
{
    public RefreshToken(Guid id, Guid userId, string tokenHash, DateTimeOffset createdAtUtc, DateTimeOffset expiresAtUtc)
    {
        if (id == Guid.Empty || userId == Guid.Empty || string.IsNullOrWhiteSpace(tokenHash))
        {
            throw new ArgumentException("Id, user and token hash are required.");
        }

        Id = id;
        UserId = userId;
        TokenHash = tokenHash;
        CreatedAtUtc = createdAtUtc;
        ExpiresAtUtc = expiresAtUtc;
    }

    public Guid Id { get; }
    public Guid UserId { get; }
    public string TokenHash { get; }
    public DateTimeOffset CreatedAtUtc { get; }
    public DateTimeOffset ExpiresAtUtc { get; }
    public DateTimeOffset? RevokedAtUtc { get; private set; }

    /// <summary>Set when this token was exchanged for a new one.</summary>
    public bool Replaced { get; private set; }

    public bool IsActive(DateTimeOffset nowUtc) => RevokedAtUtc is null && ExpiresAtUtc > nowUtc;

    public void Revoke(DateTimeOffset nowUtc, bool replaced = false)
    {
        RevokedAtUtc ??= nowUtc;
        Replaced |= replaced;
    }
}
