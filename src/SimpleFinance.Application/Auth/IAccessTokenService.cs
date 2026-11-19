using SimpleFinance.Domain.Entities;

namespace SimpleFinance.Application.Auth;

/// <summary>
/// Issues short-lived JWTs. Access tokens carry the authenticated user's identity; pending
/// 2FA tokens carry only a "purpose" claim + user id, scoped narrowly to completing the
/// 2FA challenge (01-05's 2fa/verify endpoint) without granting a full authenticated session.
/// </summary>
public interface IAccessTokenService
{
    /// <summary>Issues a signed, short-lived access token for an authenticated user.</summary>
    string IssueAccessToken(User user);

    /// <summary>
    /// Issues a short-TTL (e.g. 5-minute) JWT carrying a distinct "purpose: 2fa_pending" claim
    /// instead of a full authenticated-session subject — returned by login when TotpEnabled is
    /// true, consumed by 2fa/verify to complete login without re-checking the password.
    /// </summary>
    string IssuePendingTwoFactorToken(Guid userId);

    /// <summary>
    /// Returns the UTC expiry an access token issued right now would carry. Lets the login and
    /// refresh HTTP endpoints (01-04) populate the `accessTokenExpiresAt` field in their
    /// response without the Api layer reaching past this interface into Infrastructure's
    /// JwtOptions to duplicate the AccessTokenMinutes constant.
    /// </summary>
    DateTime GetAccessTokenExpiresAt();
}
