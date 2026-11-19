namespace SimpleFinance.Application.Auth;

/// <summary>
/// Encrypts/decrypts a plaintext TOTP secret for at-rest storage on User.TotpSecret. Uses a key
/// distinct from Jwt__SigningKey (PITFALLS.md #12 — never reuse secrets across purposes; a leak
/// of one key must not compromise the other).
/// </summary>
public interface ITotpSecretProtector
{
    /// <summary>Encrypts a plaintext Base32 TOTP secret. Never returns the plaintext unchanged.</summary>
    string Encrypt(string plaintextSecret);

    /// <summary>Decrypts a value previously produced by <see cref="Encrypt"/>.</summary>
    string Decrypt(string encryptedSecret);
}
