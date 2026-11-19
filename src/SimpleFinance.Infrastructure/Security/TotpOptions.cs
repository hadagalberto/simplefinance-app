namespace SimpleFinance.Infrastructure.Security;

/// <summary>
/// Binds to the "Totp" configuration section (Totp__EncryptionKey — see .env.example).
/// Deliberately a separate options class/section from JwtOptions: PITFALLS.md #12 requires a
/// distinct encryption key for TOTP secrets, never reused from the JWT signing key.
/// </summary>
public class TotpOptions
{
    public const string SectionName = "Totp";

    /// <summary>Base64-encoded 32-byte (256-bit) key for AES-256-GCM. Generate with `openssl rand -base64 32`.</summary>
    public string EncryptionKey { get; set; } = string.Empty;
}
