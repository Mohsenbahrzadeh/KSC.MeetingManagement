using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using KSC.MeetingManagement.Application.Identity;
using KSC.MeetingManagement.Domain.Identity;
using Microsoft.IdentityModel.Tokens;

namespace KSC.MeetingManagement.Api.Authentication;

/// <summary>Settings from section "Auth". The signing key must come from environment variables or secret storage, never a committed file.</summary>
public sealed class JwtSettings
{
    public const int MinKeyLength = 32;

    public string? SigningKey { get; init; }

    public string Issuer { get; init; } = "ksc-meetings";

    public string Audience { get; init; } = "ksc-meetings";

    public bool IsConfigured => !string.IsNullOrWhiteSpace(SigningKey) && SigningKey.Length >= MinKeyLength;

    /// <summary>Key used to validate tokens. Without configuration a random per-process key is used, so no token can ever validate.</summary>
    public SymmetricSecurityKey ValidationKey { get; } = new(RandomNumberGenerator.GetBytes(32));

    public SymmetricSecurityKey EffectiveKey => IsConfigured ? new SymmetricSecurityKey(Encoding.UTF8.GetBytes(SigningKey!)) : ValidationKey;

    public static JwtSettings From(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var key = configuration["Auth:SigningKey"];
        if (!string.IsNullOrWhiteSpace(key) && key.Length < MinKeyLength)
        {
            throw new InvalidOperationException($"Auth:SigningKey must be at least {MinKeyLength} characters.");
        }

        return new JwtSettings
        {
            SigningKey = key,
            Issuer = configuration["Auth:Issuer"] ?? "ksc-meetings",
            Audience = configuration["Auth:Audience"] ?? "ksc-meetings",
        };
    }
}

public sealed class JwtAccessTokenIssuer(JwtSettings settings, AuthOptions options, TimeProvider clock) : IAccessTokenIssuer
{
    public bool IsConfigured => settings.IsConfigured;

    public AccessToken Issue(User user)
    {
        ArgumentNullException.ThrowIfNull(user);
        if (!settings.IsConfigured)
        {
            throw new InvalidOperationException("Sign-in is not configured.");
        }

        var now = clock.GetUtcNow();
        var expires = now + options.AccessLifetime;
        var token = new JwtSecurityToken(
            settings.Issuer,
            settings.Audience,
            [new Claim("sub", user.Id.ToString()), new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString("N")), new Claim("name", user.DisplayName)],
            now.UtcDateTime,
            expires.UtcDateTime,
            signingCredentials: new SigningCredentials(settings.EffectiveKey, SecurityAlgorithms.HmacSha256));
        return new AccessToken(new JwtSecurityTokenHandler().WriteToken(token), expires);
    }
}
