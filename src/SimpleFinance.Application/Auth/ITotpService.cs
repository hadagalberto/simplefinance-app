namespace SimpleFinance.Application.Auth;

/// <summary>
/// TOTP (RFC 6238) generation and verification for AUTH-03. SHA1/6-digit/30s step — matches
/// Google Authenticator/Microsoft Authenticator/Authy compatibility (research/STACK.md).
/// </summary>
public interface ITotpService
{
    /// <summary>Generates a new random Base32-encoded TOTP secret.</summary>
    string GenerateSecret();

    /// <summary>
    /// Builds the otpauth:// provisioning URI for authenticator apps: issuer "SimpleFinance",
    /// account label the user's email, secret embedded in Base32.
    /// </summary>
    string BuildOtpauthUri(string secret, string accountEmail);

    /// <summary>
    /// Verifies a submitted 6-digit code against the given Base32 secret, tolerating ±1 time
    /// step (30s) of clock drift. Returns false (never throws) for malformed input.
    /// </summary>
    bool VerifyCode(string secret, string code);

    /// <summary>Renders the otpauth:// URI as a PNG QR code image for the "Activate 2FA" screen.</summary>
    byte[] GenerateQrCodePng(string otpauthUri);
}
