using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SimpleFinance.Application.Auth;
using SimpleFinance.Domain.Entities;
using SimpleFinance.Infrastructure.Persistence;
using SimpleFinance.Infrastructure.Security;

namespace SimpleFinance.UnitTests.Security;

public class RefreshTokenServiceTests
{
    private static SimpleFinanceDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<SimpleFinanceDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        return new SimpleFinanceDbContext(options);
    }

    private static JwtOptions CreateJwtOptions() => new()
    {
        SigningKey = "unit-test-signing-key-at-least-32-bytes-long-for-hmac256",
        AccessTokenMinutes = 15,
        RefreshTokenDays = 30,
    };

    private static string Sha256Hex(string plaintext) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(plaintext)));

    [Fact]
    public async Task IssueAsync_PersistsOnlyTokenHash_NeverPlaintext()
    {
        await using var db = CreateDbContext();
        var sut = new RefreshTokenService(db, Options.Create(CreateJwtOptions()));
        var userId = Guid.NewGuid();

        var (plaintext, expiresAt) = await sut.IssueAsync(userId);

        var stored = await db.RefreshTokens.SingleAsync();

        Assert.Equal(userId, stored.UserId);
        Assert.NotEqual(plaintext, stored.TokenHash);
        Assert.Equal(Sha256Hex(plaintext), stored.TokenHash);
        Assert.True(Math.Abs((expiresAt - stored.ExpiresAt).TotalSeconds) < 1);
    }

    [Fact]
    public async Task RotateAsync_HappyPath_RevokesOldAndIssuesUnrevokedUnexpiredNew()
    {
        await using var db = CreateDbContext();
        var sut = new RefreshTokenService(db, Options.Create(CreateJwtOptions()));
        var userId = Guid.NewGuid();
        var (plaintext, _) = await sut.IssueAsync(userId);

        var result = await sut.RotateAsync(plaintext);

        Assert.Equal(RefreshRotationOutcome.Success, result.Outcome);
        Assert.NotNull(result.NewPlaintext);
        Assert.Equal(userId, result.UserId);

        var oldToken = await db.RefreshTokens.SingleAsync(rt => rt.TokenHash == Sha256Hex(plaintext));
        Assert.NotNull(oldToken.RevokedAt);
        Assert.NotNull(oldToken.ReplacedByTokenId);

        var newToken = await db.RefreshTokens.SingleAsync(rt => rt.Id == oldToken.ReplacedByTokenId);
        Assert.Null(newToken.RevokedAt);
        Assert.True(newToken.ExpiresAt > DateTime.UtcNow);
        Assert.Equal(oldToken.FamilyId, newToken.FamilyId);
    }

    [Fact]
    public async Task RotateAsync_PresentedAgainAfterRotation_CascadeRevokesEntireFamily()
    {
        await using var db = CreateDbContext();
        var sut = new RefreshTokenService(db, Options.Create(CreateJwtOptions()));
        var userId = Guid.NewGuid();
        var (originalPlaintext, _) = await sut.IssueAsync(userId);

        var firstRotate = await sut.RotateAsync(originalPlaintext);
        Assert.Equal(RefreshRotationOutcome.Success, firstRotate.Outcome);

        // Reuse: present the original (now-revoked, already-rotated) token again.
        var secondRotate = await sut.RotateAsync(originalPlaintext);

        Assert.Equal(RefreshRotationOutcome.ReusedAndFamilyRevoked, secondRotate.Outcome);

        var originalToken = await db.RefreshTokens.SingleAsync(rt => rt.TokenHash == Sha256Hex(originalPlaintext));
        var familyTokens = await db.RefreshTokens
            .Where(rt => rt.FamilyId == originalToken.FamilyId)
            .ToListAsync();

        Assert.True(familyTokens.Count >= 2);
        Assert.All(familyTokens, t => Assert.NotNull(t.RevokedAt));
    }

    [Fact]
    public async Task RotateAsync_ExpiredToken_ReturnsExpired()
    {
        await using var db = CreateDbContext();
        var sut = new RefreshTokenService(db, Options.Create(CreateJwtOptions()));
        var plaintext = "expired-token-plaintext";
        var now = DateTime.UtcNow;

        db.RefreshTokens.Add(new RefreshToken
        {
            Id = Guid.NewGuid(),
            UserId = Guid.NewGuid(),
            TokenHash = Sha256Hex(plaintext),
            ExpiresAt = now.AddDays(-1),
            FamilyId = Guid.NewGuid(),
            CreatedAt = now.AddDays(-31),
            UpdatedAt = now.AddDays(-31),
        });
        await db.SaveChangesAsync();

        var result = await sut.RotateAsync(plaintext);

        Assert.Equal(RefreshRotationOutcome.Expired, result.Outcome);
    }

    [Fact]
    public async Task RotateAsync_RevokedButNotViaRotation_ReturnsInvalid_NotSilentSuccess()
    {
        await using var db = CreateDbContext();
        var sut = new RefreshTokenService(db, Options.Create(CreateJwtOptions()));
        var plaintext = "explicitly-revoked-token";
        var now = DateTime.UtcNow;

        db.RefreshTokens.Add(new RefreshToken
        {
            Id = Guid.NewGuid(),
            UserId = Guid.NewGuid(),
            TokenHash = Sha256Hex(plaintext),
            ExpiresAt = now.AddDays(29),
            RevokedAt = now, // revoked via e.g. RevokeAllForUserAsync — never rotated
            ReplacedByTokenId = null,
            FamilyId = Guid.NewGuid(),
            CreatedAt = now.AddDays(-1),
            UpdatedAt = now,
        });
        await db.SaveChangesAsync();

        var result = await sut.RotateAsync(plaintext);

        Assert.Equal(RefreshRotationOutcome.Invalid, result.Outcome);
    }

    [Fact]
    public async Task RotateAsync_UnknownToken_ReturnsInvalid()
    {
        await using var db = CreateDbContext();
        var sut = new RefreshTokenService(db, Options.Create(CreateJwtOptions()));

        var result = await sut.RotateAsync("never-issued-token");

        Assert.Equal(RefreshRotationOutcome.Invalid, result.Outcome);
    }

    [Fact]
    public async Task RevokeAllForUserAsync_RevokesEveryActiveTokenForThatUser()
    {
        await using var db = CreateDbContext();
        var sut = new RefreshTokenService(db, Options.Create(CreateJwtOptions()));
        var userId = Guid.NewGuid();
        var otherUserId = Guid.NewGuid();

        var (_, _) = await sut.IssueAsync(userId);
        await sut.IssueAsync(userId);
        var (otherPlaintext, _) = await sut.IssueAsync(otherUserId);

        await sut.RevokeAllForUserAsync(userId);

        var userTokens = await db.RefreshTokens.Where(rt => rt.UserId == userId).ToListAsync();
        Assert.All(userTokens, t => Assert.NotNull(t.RevokedAt));

        var otherToken = await db.RefreshTokens.SingleAsync(rt => rt.TokenHash == Sha256Hex(otherPlaintext));
        Assert.Null(otherToken.RevokedAt);
    }
}
