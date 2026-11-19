namespace SimpleFinance.Application.Auth.TwoFactor;

/// <summary>
/// Response contract for POST /api/auth/2fa/enable. No request body — the caller is identified
/// by the authenticated session ([Authorize]). Returns the pending (not-yet-activated) secret in
/// three forms the frontend needs: raw Base32 (manual entry fallback), the otpauth:// URI, and a
/// ready-to-render QR code PNG.
/// </summary>
public sealed record EnableTwoFactorResponse(string Secret, string OtpauthUri, string QrCodePngBase64);
