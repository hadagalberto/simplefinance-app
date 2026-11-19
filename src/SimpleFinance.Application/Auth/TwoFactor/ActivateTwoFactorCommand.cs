namespace SimpleFinance.Application.Auth.TwoFactor;

/// <summary>Request contract for POST /api/auth/2fa/activate.</summary>
public sealed record ActivateTwoFactorCommand(string Code);

/// <summary>
/// Response contract for POST /api/auth/2fa/activate. The 8 recovery codes are plaintext and
/// shown exactly once — never retrievable again after this response.
/// </summary>
public sealed record ActivateTwoFactorResponse(IReadOnlyList<string> RecoveryCodes);
