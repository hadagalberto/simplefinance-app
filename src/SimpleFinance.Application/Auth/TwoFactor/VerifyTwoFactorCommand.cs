namespace SimpleFinance.Application.Auth.TwoFactor;

/// <summary>
/// Request contract for POST /api/auth/2fa/verify. `Code` is either a 6-digit TOTP code or a
/// recovery-code-formatted string — the endpoint tries TOTP first, falling back to a recovery
/// code (see 01-05-PLAN.md's api_contract).
/// </summary>
public sealed record VerifyTwoFactorCommand(string PendingToken, string Code);
