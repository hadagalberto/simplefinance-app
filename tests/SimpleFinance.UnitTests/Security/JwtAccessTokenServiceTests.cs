using System.IdentityModel.Tokens.Jwt;
using System.Text;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using SimpleFinance.Domain.Entities;
using SimpleFinance.Infrastructure.Security;

namespace SimpleFinance.UnitTests.Security;

public class JwtAccessTokenServiceTests
{
    private static JwtOptions CreateOptions(string? signingKey = null) => new()
    {
        SigningKey = signingKey ?? "unit-test-signing-key-at-least-32-bytes-long-for-hmac256",
        AccessTokenMinutes = 15,
        RefreshTokenDays = 30,
    };

    private static User CreateUser() => new()
    {
        Id = Guid.NewGuid(),
        Name = "Owner",
        Email = "owner@example.com",
        PasswordHash = "irrelevant-for-this-test",
    };

    [Fact]
    public void IssueAccessToken_ProducesJwtWithSubAndEmail_SignedAndExpiringCorrectly()
    {
        var options = CreateOptions();
        var sut = new JwtAccessTokenService(Options.Create(options));
        var user = CreateUser();

        var token = sut.IssueAccessToken(user);

        // Disable JwtSecurityTokenHandler's default inbound claim-type remapping (it rewrites
        // "sub" -> ClaimTypes.NameIdentifier on validation) so the assertions below can read the
        // JWT-standard claim names actually written by JwtAccessTokenService.
        var handler = new JwtSecurityTokenHandler { MapInboundClaims = false };
        var validationParameters = new TokenValidationParameters
        {
            ValidateIssuer = false,
            ValidateAudience = false,
            ValidateLifetime = true,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(options.SigningKey)),
            ClockSkew = TimeSpan.FromSeconds(5),
        };

        var principal = handler.ValidateToken(token, validationParameters, out var validatedToken);

        Assert.Equal(user.Id.ToString(), principal.FindFirst(JwtRegisteredClaimNames.Sub)?.Value);
        Assert.Equal(user.Email, principal.FindFirst(JwtRegisteredClaimNames.Email)?.Value);

        var expectedExpiry = DateTime.UtcNow.AddMinutes(options.AccessTokenMinutes);
        Assert.True(Math.Abs((validatedToken.ValidTo - expectedExpiry).TotalSeconds) < 10);
    }

    [Fact]
    public void IssueAccessToken_TokenSignedWithDifferentKey_FailsValidation()
    {
        var sut = new JwtAccessTokenService(Options.Create(CreateOptions()));
        var user = CreateUser();

        var token = sut.IssueAccessToken(user);

        var handler = new JwtSecurityTokenHandler();
        var wrongKeyValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = false,
            ValidateAudience = false,
            IssuerSigningKey = new SymmetricSecurityKey(
                Encoding.UTF8.GetBytes("a-totally-different-signing-key-also-32-bytes-min")),
        };

        void Act()
        {
            handler.ValidateToken(token, wrongKeyValidationParameters, out _);
        }

        Assert.Throws<SecurityTokenSignatureKeyNotFoundException>(Act);
    }
}
