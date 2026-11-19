using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SimpleFinance.Application.Auth;
using SimpleFinance.Domain.Entities;
using SimpleFinance.Infrastructure.Persistence;

namespace SimpleFinance.Infrastructure.Security;

/// <summary>
/// Opaque rotating refresh tokens against SimpleFinanceDbContext. Only SHA-256 hashes ever
/// cross into persistence — the plaintext is generated, returned, and forgotten in the same
/// call. Reuse-detection: a token is only ever revoked with ReplacedByTokenId set when it goes
/// through RotateAsync's happy path; presenting such a token again is therefore an unambiguous
/// replay signal and cascades a revoke across FamilyId. A token revoked any other way (e.g.
/// RevokeAllForUserAsync on logout/password-change) has ReplacedByTokenId == null and is
/// rejected as Invalid rather than treated as a reuse/replay event.
/// </summary>
public class RefreshTokenService : IRefreshTokenService
{
    private const int OpaqueTokenSizeBytes = 32;

    private readonly SimpleFinanceDbContext _db;
    private readonly JwtOptions _options;

    public RefreshTokenService(SimpleFinanceDbContext db, IOptions<JwtOptions> options)
    {
        _db = db;
        _options = options.Value;
    }

    public async Task<(string Plaintext, DateTime ExpiresAt)> IssueAsync(Guid userId, CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        var plaintext = GenerateOpaqueToken();
        var expiresAt = now.AddDays(_options.RefreshTokenDays);

        var token = new RefreshToken
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            TokenHash = Hash(plaintext),
            ExpiresAt = expiresAt,
            CreatedAt = now,
            UpdatedAt = now,
        };
        token.FamilyId = token.Id;

        _db.RefreshTokens.Add(token);
        await _db.SaveChangesAsync(ct);

        return (plaintext, expiresAt);
    }

    public async Task<RefreshRotationResult> RotateAsync(string presentedPlaintext, CancellationToken ct = default)
    {
        var presentedHash = Hash(presentedPlaintext);
        var token = await _db.RefreshTokens.SingleOrDefaultAsync(rt => rt.TokenHash == presentedHash, ct);

        if (token is null)
        {
            return RefreshRotationResult.Invalid;
        }

        var now = DateTime.UtcNow;

        if (token.RevokedAt is not null)
        {
            if (token.ReplacedByTokenId is not null)
            {
                // The presented token was already rotated once; being presented again is a
                // reuse/replay signal — cascade-revoke the whole rotation family.
                await RevokeFamilyAsync(token.FamilyId, now, ct);
                return RefreshRotationResult.ReusedAndFamilyRevoked;
            }

            // Revoked some other way (logout, password change) — reject, but not a reuse event.
            return RefreshRotationResult.Invalid;
        }

        if (token.ExpiresAt <= now)
        {
            return RefreshRotationResult.Expired;
        }

        var newPlaintext = GenerateOpaqueToken();
        var newExpiresAt = now.AddDays(_options.RefreshTokenDays);

        var newToken = new RefreshToken
        {
            Id = Guid.NewGuid(),
            UserId = token.UserId,
            TokenHash = Hash(newPlaintext),
            ExpiresAt = newExpiresAt,
            FamilyId = token.FamilyId,
            CreatedAt = now,
            UpdatedAt = now,
        };

        token.RevokedAt = now;
        token.ReplacedByTokenId = newToken.Id;
        token.UpdatedAt = now;

        _db.RefreshTokens.Add(newToken);
        await _db.SaveChangesAsync(ct);

        return RefreshRotationResult.Succeeded(newPlaintext, newExpiresAt, token.UserId);
    }

    public async Task RevokeAsync(string presentedPlaintext, CancellationToken ct = default)
    {
        var hash = Hash(presentedPlaintext);
        var token = await _db.RefreshTokens.SingleOrDefaultAsync(rt => rt.TokenHash == hash, ct);

        if (token is null || token.RevokedAt is not null)
        {
            // Logout is idempotent — an unknown or already-revoked token is a no-op, not an error.
            return;
        }

        token.RevokedAt = DateTime.UtcNow;
        token.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
    }

    public async Task RevokeAllForUserAsync(Guid userId, CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        var activeTokens = await _db.RefreshTokens
            .Where(rt => rt.UserId == userId && rt.RevokedAt == null)
            .ToListAsync(ct);

        foreach (var token in activeTokens)
        {
            token.RevokedAt = now;
            token.UpdatedAt = now;
        }

        await _db.SaveChangesAsync(ct);
    }

    private async Task RevokeFamilyAsync(Guid familyId, DateTime now, CancellationToken ct)
    {
        var activeFamilyTokens = await _db.RefreshTokens
            .Where(rt => rt.FamilyId == familyId && rt.RevokedAt == null)
            .ToListAsync(ct);

        foreach (var token in activeFamilyTokens)
        {
            token.RevokedAt = now;
            token.UpdatedAt = now;
        }

        await _db.SaveChangesAsync(ct);
    }

    private static string GenerateOpaqueToken() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(OpaqueTokenSizeBytes));

    private static string Hash(string plaintext) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(plaintext)));
}
