using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using FluentValidation;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using SimpleFinance.Application.Auth;
using SimpleFinance.Application.Auth.ChangePassword;
using SimpleFinance.Application.Auth.Login;
using SimpleFinance.Application.Auth.Setup;
using SimpleFinance.Domain.Entities;
using SimpleFinance.Infrastructure.Categorization;
using SimpleFinance.Infrastructure.Persistence;

namespace SimpleFinance.Api.Endpoints;

/// <summary>
/// Auth core HTTP surface: setup (AUTH-01), login (AUTH-02, 2FA-aware per 01-04's api_contract),
/// refresh/logout, and change-password (AUTH-05). 01-05 adds the /2fa/verify completion step to
/// this same group.
/// </summary>
public static class AuthEndpoints
{
    public const string RefreshCookieName = "refresh_token";
    private const string AuthCookiePath = "/api/auth";

    public static void MapAuthEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/auth");

        group.MapPost("/setup", SetupAsync);

        group.MapPost("/login", LoginAsync)
            .RequireRateLimiting("login");

        group.MapPost("/refresh", RefreshAsync);

        group.MapPost("/logout", LogoutAsync);

        group.MapPost("/change-password", ChangePasswordAsync)
            .RequireAuthorization();
    }

    private static async Task<IResult> SetupAsync(
        SetupUserCommand command,
        SimpleFinanceDbContext db,
        IValidator<SetupUserCommand> validator,
        IPasswordHasher passwordHasher,
        CategorySeeder categorySeeder,
        CancellationToken ct)
    {
        await validator.ValidateAndThrowAsync(command, ct);

        // T-01-15: Users has no soft-delete filter (User inherits AuditableEntity, not
        // UserOwnedEntity), so IgnoreQueryFilters is a no-op safeguard here — kept explicit so
        // this check never silently breaks if that ever changes.
        if (await db.Users.IgnoreQueryFilters().AnyAsync(ct))
        {
            return SetupAlreadyCompleted();
        }

        var now = DateTime.UtcNow;
        var user = new User
        {
            Id = Guid.NewGuid(),
            Name = command.Name.Trim(),
            Email = NormalizeEmail(command.Email),
            PasswordHash = passwordHasher.Hash(command.Password),
            CreatedAt = now,
            UpdatedAt = now,
        };

        db.Users.Add(user);

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            // T-01-15: fallback for the race between the Any() check above and this insert —
            // the unique index on Email/single-row expectation rejects a concurrent second
            // setup call at the DB level; treat it identically to the ordinary "locked" path.
            return SetupAlreadyCompleted();
        }

        // CAT-01: seed the default category tree/mappings synchronously in the same request --
        // no timing gap between the user existing and having a usable category tree.
        await categorySeeder.SeedForUserAsync(user.Id, ct);

        return Results.Created($"/api/auth/setup", new { userId = user.Id, email = user.Email });
    }

    private static async Task<IResult> LoginAsync(
        LoginCommand command,
        SimpleFinanceDbContext db,
        IValidator<LoginCommand> validator,
        IPasswordHasher passwordHasher,
        IAccessTokenService accessTokenService,
        IRefreshTokenService refreshTokenService,
        HttpResponse response,
        CancellationToken ct)
    {
        await validator.ValidateAndThrowAsync(command, ct);

        var normalizedEmail = NormalizeEmail(command.Email);
        var user = await db.Users.SingleOrDefaultAsync(u => u.Email == normalizedEmail, ct);

        // T-01-11 / T-01-12: identical response whether no such user exists or the password is
        // wrong — never signal which one failed.
        if (user is null || !passwordHasher.Verify(command.Password, user.PasswordHash))
        {
            return InvalidCredentials();
        }

        if (user.TotpEnabled)
        {
            // 2FA completion (verifying the TOTP/recovery code and actually issuing the real
            // session) ships in 01-05 — this pending-token shape is the contract it completes.
            // LastLoginAt is deliberately NOT updated yet; that only happens once 2FA succeeds.
            var pendingToken = accessTokenService.IssuePendingTwoFactorToken(user.Id);
            return Results.Ok(LoginResponse.TwoFactorPending(pendingToken));
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

    private static async Task<IResult> RefreshAsync(
        HttpRequest request,
        HttpResponse response,
        IRefreshTokenService refreshTokenService,
        IAccessTokenService accessTokenService,
        SimpleFinanceDbContext db,
        CancellationToken ct)
    {
        if (!request.Cookies.TryGetValue(RefreshCookieName, out var presented) || string.IsNullOrEmpty(presented))
        {
            return InvalidRefreshToken();
        }

        var result = await refreshTokenService.RotateAsync(presented, ct);

        // Invalid/Expired/ReusedAndFamilyRevoked all map to the same generic 401 — an attacker
        // probing which failure mode occurred gains nothing from a more specific response.
        if (result.Outcome != RefreshRotationOutcome.Success)
        {
            ClearRefreshCookie(response);
            return InvalidRefreshToken();
        }

        var user = await db.Users.SingleAsync(u => u.Id == result.UserId!.Value, ct);

        SetRefreshCookie(response, result.NewPlaintext!, result.NewExpiresAt!.Value);

        var accessToken = accessTokenService.IssueAccessToken(user);
        var accessTokenExpiresAt = accessTokenService.GetAccessTokenExpiresAt();

        return Results.Ok(new { accessToken, accessTokenExpiresAt });
    }

    private static async Task<IResult> LogoutAsync(
        HttpRequest request,
        HttpResponse response,
        IRefreshTokenService refreshTokenService,
        CancellationToken ct)
    {
        if (request.Cookies.TryGetValue(RefreshCookieName, out var presented) && !string.IsNullOrEmpty(presented))
        {
            await refreshTokenService.RevokeAsync(presented, ct);
        }

        // Logout is idempotent: 204 regardless of whether a cookie/session was actually present.
        ClearRefreshCookie(response);
        return Results.NoContent();
    }

    private static string NormalizeEmail(string email) => email.Trim().ToLowerInvariant();

    private static IResult SetupAlreadyCompleted() =>
        Results.Json(new { error = "setup_already_completed" }, statusCode: StatusCodes.Status409Conflict);

    private static IResult InvalidCredentials() =>
        Results.Json(new { error = "invalid_credentials" }, statusCode: StatusCodes.Status401Unauthorized);

    private static IResult InvalidRefreshToken() =>
        Results.Json(new { error = "invalid_refresh_token" }, statusCode: StatusCodes.Status401Unauthorized);

    private static IResult InvalidCurrentPassword() =>
        Results.Json(new { error = "invalid_current_password" }, statusCode: StatusCodes.Status400BadRequest);

    private static async Task<IResult> ChangePasswordAsync(
        ChangePasswordCommand command,
        ClaimsPrincipal principal,
        HttpResponse response,
        SimpleFinanceDbContext db,
        IValidator<ChangePasswordCommand> validator,
        IPasswordHasher passwordHasher,
        IRefreshTokenService refreshTokenService,
        CancellationToken ct)
    {
        var subClaim = principal.FindFirst(JwtRegisteredClaimNames.Sub)?.Value;
        if (subClaim is null || !Guid.TryParse(subClaim, out var userId))
        {
            return Results.Unauthorized();
        }

        var user = await db.Users.SingleOrDefaultAsync(u => u.Id == userId, ct);
        if (user is null)
        {
            return Results.Unauthorized();
        }

        // T-01-14: current password is re-verified server-side (below) even though the caller
        // already holds a valid access token — a stolen/leaked bearer token alone must not be
        // enough to change the password without also knowing it.
        // ValidateAndThrowAsync<T> only accepts a bare T instance, not a ValidationContext<T> —
        // use the IValidationContext-accepting ValidateAsync overload directly and throw
        // manually so ChangePasswordValidator's "new password differs from current" rule can
        // read the existing hash out of RootContextData (see the validator's own comment).
        var validationContext = new ValidationContext<ChangePasswordCommand>(command);
        validationContext.RootContextData[ChangePasswordValidator.ExistingPasswordHashKey] = user.PasswordHash;
        var validationResult = await validator.ValidateAsync(validationContext, ct);
        if (!validationResult.IsValid)
        {
            throw new ValidationException(validationResult.Errors);
        }

        if (!passwordHasher.Verify(command.CurrentPassword, user.PasswordHash))
        {
            return InvalidCurrentPassword();
        }

        user.PasswordHash = passwordHasher.Hash(command.NewPassword);
        user.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);

        // AUTH-05: every session (including the caller's own) is invalidated on password
        // change — forces re-login everywhere, not just on other devices.
        await refreshTokenService.RevokeAllForUserAsync(userId, ct);
        ClearRefreshCookie(response);

        return Results.NoContent();
    }

    private static void SetRefreshCookie(HttpResponse response, string plaintext, DateTime expiresAt)
    {
        response.Cookies.Append(RefreshCookieName, plaintext, new CookieOptions
        {
            HttpOnly = true,
            Secure = true,
            // SameSite=Lax, not Strict: front (localhost:5173) and api (localhost:5080) are
            // different ports and therefore different origins in local dev. SameSite=Strict
            // blocks the browser from ever attaching the cookie to the SPA's cross-origin
            // fetch/XHR calls, even though CORS explicitly allows the origin below — Lax is the
            // standard compromise (still blocks top-level cross-site navigation CSRF, which is
            // the attack SameSite exists to stop) when a cookie-based session is split across a
            // separate front/api origin pair. In production, front and api will likely sit
            // behind the same StackForge-provisioned origin/reverse-proxy path, making this a
            // non-issue there — Lax remains a safe, unaffected default even same-origin.
            SameSite = SameSiteMode.Lax,
            Path = AuthCookiePath,
            Expires = expiresAt,
        });
    }

    private static void ClearRefreshCookie(HttpResponse response)
    {
        response.Cookies.Delete(RefreshCookieName, new CookieOptions
        {
            HttpOnly = true,
            Secure = true,
            SameSite = SameSiteMode.Lax,
            Path = AuthCookiePath,
        });
    }
}
