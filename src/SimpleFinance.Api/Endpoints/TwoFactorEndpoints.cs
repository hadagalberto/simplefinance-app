using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using FluentValidation;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using SimpleFinance.Application.Auth;
using SimpleFinance.Application.Auth.Login;
using SimpleFinance.Application.Auth.TwoFactor;
using SimpleFinance.Domain.Entities;
using SimpleFinance.Infrastructure.Persistence;
using SimpleFinance.Infrastructure.Security;

namespace SimpleFinance.Api.Endpoints;

/// <summary>
/// AUTH-03: TOTP-based 2FA. Sibling to AuthEndpoints (01-04) under the same /api/auth group —
/// /2fa/verify is the completion step 01-04's login left as a pendingToken handoff.
/// </summary>
public static class TwoFactorEndpoints
{
    private const string PendingTwoFactorPurpose = "2fa_pending";
    private const string AuthCookiePath = "/api/auth";

    public static void MapTwoFactorEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/auth/2fa");

        group.MapPost("/enable", EnableAsync).RequireAuthorization();

        group.MapPost("/activate", ActivateAsync).RequireAuthorization();

        // Deliberately NOT [Authorize]: the caller isn't fully authenticated yet — the
        // pendingToken (validated manually below) substitutes for a session, per T-01-16.
        group.MapPost("/verify", VerifyAsync);
    }

    private static async Task<IResult> EnableAsync(
        ClaimsPrincipal principal,
        SimpleFinanceDbContext db,
        ITotpService totpService,
        ITotpSecretProtector protector,
        CancellationToken ct)
    {
        var user = await GetAuthenticatedUserAsync(principal, db, ct);
        if (user is null)
        {
            return Results.Unauthorized();
        }

        var secret = totpService.GenerateSecret();
        var otpauthUri = totpService.BuildOtpauthUri(secret, user.Email);
        var qrCodePng = totpService.GenerateQrCodePng(otpauthUri);

        // Stores the pending secret encrypted WITHOUT enabling 2FA — only /activate flips
        // TotpEnabled. Calling /enable again before activating simply regenerates this, safe
        // per this plan's api_contract.
        user.TotpSecret = protector.Encrypt(secret);
        user.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);

        return Results.Ok(new EnableTwoFactorResponse(secret, otpauthUri, Convert.ToBase64String(qrCodePng)));
    }

    private static async Task<IResult> ActivateAsync(
        ActivateTwoFactorCommand command,
        ClaimsPrincipal principal,
        SimpleFinanceDbContext db,
        IValidator<ActivateTwoFactorCommand> validator,
        ITotpService totpService,
        ITotpSecretProtector protector,
        IRecoveryCodeService recoveryCodeService,
        CancellationToken ct)
    {
        await validator.ValidateAndThrowAsync(command, ct);

        var user = await GetAuthenticatedUserAsync(principal, db, ct);
        if (user is null)
        {
            return Results.Unauthorized();
        }

        if (string.IsNullOrEmpty(user.TotpSecret))
        {
            return InvalidTotpCode();
        }

        var secret = protector.Decrypt(user.TotpSecret);
        if (!totpService.VerifyCode(secret, command.Code))
        {
            return InvalidTotpCode();
        }

        user.TotpEnabled = true;
        user.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);

        var recoveryCodes = await recoveryCodeService.GenerateAsync(user.Id, ct);

        return Results.Ok(new ActivateTwoFactorResponse(recoveryCodes));
    }

    private static async Task<IResult> VerifyAsync(
        VerifyTwoFactorCommand command,
        SimpleFinanceDbContext db,
        IValidator<VerifyTwoFactorCommand> validator,
        ITotpService totpService,
        ITotpSecretProtector protector,
        IRecoveryCodeService recoveryCodeService,
        IAccessTokenService accessTokenService,
        IRefreshTokenService refreshTokenService,
        IOptions<JwtOptions> jwtOptions,
        HttpResponse response,
        CancellationToken ct)
    {
        await validator.ValidateAndThrowAsync(command, ct);

        var userId = ValidatePendingToken(command.PendingToken, jwtOptions.Value);
        if (userId is null)
        {
            return InvalidCode();
        }

        var user = await db.Users.SingleOrDefaultAsync(u => u.Id == userId.Value, ct);
        if (user is null || !user.TotpEnabled || string.IsNullOrEmpty(user.TotpSecret))
        {
            return InvalidCode();
        }

        var secret = protector.Decrypt(user.TotpSecret);
        var totpValid = totpService.VerifyCode(secret, command.Code);

        if (!totpValid)
        {
            // Only fall back to recovery-code matching when the submitted code doesn't even
            // look like a 6-digit TOTP code (per this plan's api_contract) — otherwise a
            // mistyped TOTP code would be silently probed against every recovery code.
            var looksLikeTotp = command.Code.Length == 6 && command.Code.All(char.IsDigit);
            if (looksLikeTotp)
            {
                return InvalidCode();
            }

            var recoveryResult = await recoveryCodeService.ConsumeAsync(user.Id, command.Code, ct);
            switch (recoveryResult.Outcome)
            {
                case RecoveryCodeConsumeOutcome.AlreadyUsed:
                    return RecoveryCodeAlreadyUsed();
                case RecoveryCodeConsumeOutcome.NotFound:
                    return InvalidCode();
            }
        }

        var (refreshPlaintext, refreshExpiresAt) = await refreshTokenService.IssueAsync(user.Id, ct);
        SetRefreshCookie(response, refreshPlaintext, refreshExpiresAt);

        var accessToken = accessTokenService.IssueAccessToken(user);
        var accessTokenExpiresAt = accessTokenService.GetAccessTokenExpiresAt();

        user.LastLoginAt = DateTime.UtcNow;
        user.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);

        var userDto = new LoginUserDto(user.Id, user.Name, user.Email);
        return Results.Ok(LoginResponse.Authenticated(accessToken, accessTokenExpiresAt, userDto));
    }

    private static async Task<User?> GetAuthenticatedUserAsync(ClaimsPrincipal principal, SimpleFinanceDbContext db, CancellationToken ct)
    {
        var subClaim = principal.FindFirst(JwtRegisteredClaimNames.Sub)?.Value;
        if (subClaim is null || !Guid.TryParse(subClaim, out var userId))
        {
            return null;
        }

        return await db.Users.SingleOrDefaultAsync(u => u.Id == userId, ct);
    }

    /// <summary>
    /// Validates the pendingToken carries the "2fa_pending" purpose claim (rejecting a normal
    /// access token or any other JWT — T-01-16) and extracts the target user id from its "uid"
    /// claim. Returns null on any validation failure (bad signature, expired, wrong purpose).
    /// </summary>
    private static Guid? ValidatePendingToken(string token, JwtOptions jwtOptions)
    {
        var handler = new JwtSecurityTokenHandler { MapInboundClaims = false };
        var parameters = new TokenValidationParameters
        {
            ValidateIssuer = false,
            ValidateAudience = false,
            ValidateLifetime = true,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtOptions.SigningKey)),
            ClockSkew = TimeSpan.FromSeconds(30),
        };

        ClaimsPrincipal principal;
        try
        {
            principal = handler.ValidateToken(token, parameters, out _);
        }
        catch
        {
            return null;
        }

        var purpose = principal.FindFirst("purpose")?.Value;
        if (purpose != PendingTwoFactorPurpose)
        {
            return null;
        }

        var uid = principal.FindFirst("uid")?.Value;
        return Guid.TryParse(uid, out var userId) ? userId : null;
    }

    private static void SetRefreshCookie(HttpResponse response, string plaintext, DateTime expiresAt)
    {
        // Mirrors AuthEndpoints.SetRefreshCookie exactly (same cookie name/flags) — 01-04's
        // non-2FA login path and this 2FA completion path must issue indistinguishable cookies.
        response.Cookies.Append(AuthEndpoints.RefreshCookieName, plaintext, new CookieOptions
        {
            HttpOnly = true,
            Secure = true,
            SameSite = SameSiteMode.Lax,
            Path = AuthCookiePath,
            Expires = expiresAt,
        });
    }

    private static IResult InvalidTotpCode() =>
        Results.Json(new { error = "invalid_totp_code" }, statusCode: StatusCodes.Status400BadRequest);

    private static IResult InvalidCode() =>
        Results.Json(new { error = "invalid_code" }, statusCode: StatusCodes.Status401Unauthorized);

    private static IResult RecoveryCodeAlreadyUsed() =>
        Results.Json(new { error = "recovery_code_already_used" }, statusCode: StatusCodes.Status401Unauthorized);
}
