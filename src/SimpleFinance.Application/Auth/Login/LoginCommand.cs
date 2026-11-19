using System.Text.Json.Serialization;

namespace SimpleFinance.Application.Auth.Login;

/// <summary>Request contract for POST /api/auth/login — shared with 01-06's frontend client.</summary>
public sealed record LoginCommand(string Email, string Password);

/// <summary>Minimal authenticated-user shape returned alongside a successful login.</summary>
public sealed record LoginUserDto(Guid Id, string Name, string Email);

/// <summary>
/// Response contract for POST /api/auth/login. Exactly one of two shapes is ever populated:
///   - RequiresTwoFactor == false: AccessToken/AccessTokenExpiresAt/User are set, PendingToken
///     is null (a real session was issued).
///   - RequiresTwoFactor == true: only PendingToken is set — no access token, no refresh
///     cookie, 01-05's /2fa/verify endpoint completes the session from here.
/// System.Text.Json's JsonIgnore(WhenWritingNull) keeps the wire shape exactly matching
/// 01-04's api_contract (no stray null fields in either branch).
/// </summary>
public sealed record LoginResponse(
    bool RequiresTwoFactor,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? AccessToken,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] DateTime? AccessTokenExpiresAt,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] LoginUserDto? User,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? PendingToken)
{
    public static LoginResponse Authenticated(string accessToken, DateTime accessTokenExpiresAt, LoginUserDto user) =>
        new(false, accessToken, accessTokenExpiresAt, user, null);

    public static LoginResponse TwoFactorPending(string pendingToken) =>
        new(true, null, null, null, pendingToken);
}
