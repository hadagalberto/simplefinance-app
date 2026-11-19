using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using SimpleFinance.Application.Auth;
using SimpleFinance.Domain.Entities;

namespace SimpleFinance.Infrastructure.Security;

/// <summary>
/// Issues HMAC-SHA256-signed JWTs using a dedicated Jwt__SigningKey secret (distinct from
/// Totp__EncryptionKey — PITFALLS.md #12: never reuse secrets across purposes).
/// </summary>
public class JwtAccessTokenService : IAccessTokenService
{
    private const string PendingTwoFactorPurpose = "2fa_pending";
    private static readonly TimeSpan PendingTwoFactorLifetime = TimeSpan.FromMinutes(5);

    private readonly JwtOptions _options;

    public JwtAccessTokenService(IOptions<JwtOptions> options)
    {
        _options = options.Value;
    }

    public string IssueAccessToken(User user)
    {
        var claims = new[]
        {
            new Claim(JwtRegisteredClaimNames.Sub, user.Id.ToString()),
            new Claim(JwtRegisteredClaimNames.Email, user.Email),
        };

        return CreateToken(claims, TimeSpan.FromMinutes(_options.AccessTokenMinutes));
    }

    public string IssuePendingTwoFactorToken(Guid userId)
    {
        // Deliberately no "sub" claim — this token must never be mistaken for an authenticated
        // session by middleware that reads "sub" as the caller's identity. The user id travels
        // in a distinct "uid" claim, gated by the "purpose" claim the 2fa/verify endpoint checks.
        var claims = new[]
        {
            new Claim("uid", userId.ToString()),
            new Claim("purpose", PendingTwoFactorPurpose),
        };

        return CreateToken(claims, PendingTwoFactorLifetime);
    }

    public DateTime GetAccessTokenExpiresAt() =>
        DateTime.UtcNow.AddMinutes(_options.AccessTokenMinutes);

    private string CreateToken(IEnumerable<Claim> claims, TimeSpan lifetime)
    {
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_options.SigningKey));
        var credentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);
        var now = DateTime.UtcNow;

        var token = new JwtSecurityToken(
            claims: claims,
            notBefore: now,
            expires: now.Add(lifetime),
            signingCredentials: credentials);

        return new JwtSecurityTokenHandler().WriteToken(token);
    }
}
