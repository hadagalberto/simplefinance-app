using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using SimpleFinance.Application.Auth;

namespace SimpleFinance.Infrastructure.Security;

/// <summary>
/// AES-256-GCM (authenticated encryption) protector for TOTP secrets at rest, keyed by
/// Totp__EncryptionKey — a distinct secret from Jwt__SigningKey (PITFALLS.md #12). Output is
/// Base64(nonce || ciphertext || tag), self-contained per call with a fresh random nonce.
/// </summary>
public class AesTotpSecretProtector : ITotpSecretProtector
{
    private const int NonceSizeBytes = 12;
    private const int TagSizeBytes = 16;

    private readonly byte[] _key;

    public AesTotpSecretProtector(IOptions<TotpOptions> options)
    {
        _key = Convert.FromBase64String(options.Value.EncryptionKey);
    }

    public string Encrypt(string plaintextSecret)
    {
        var plaintextBytes = Encoding.UTF8.GetBytes(plaintextSecret);
        var nonce = RandomNumberGenerator.GetBytes(NonceSizeBytes);
        var ciphertext = new byte[plaintextBytes.Length];
        var tag = new byte[TagSizeBytes];

        using (var aesGcm = new AesGcm(_key, TagSizeBytes))
        {
            aesGcm.Encrypt(nonce, plaintextBytes, ciphertext, tag);
        }

        var combined = new byte[NonceSizeBytes + ciphertext.Length + TagSizeBytes];
        Buffer.BlockCopy(nonce, 0, combined, 0, NonceSizeBytes);
        Buffer.BlockCopy(ciphertext, 0, combined, NonceSizeBytes, ciphertext.Length);
        Buffer.BlockCopy(tag, 0, combined, NonceSizeBytes + ciphertext.Length, TagSizeBytes);

        return Convert.ToBase64String(combined);
    }

    public string Decrypt(string encryptedSecret)
    {
        var combined = Convert.FromBase64String(encryptedSecret);

        var nonce = combined.AsSpan(0, NonceSizeBytes).ToArray();
        var ciphertextLength = combined.Length - NonceSizeBytes - TagSizeBytes;
        var ciphertext = combined.AsSpan(NonceSizeBytes, ciphertextLength).ToArray();
        var tag = combined.AsSpan(NonceSizeBytes + ciphertextLength, TagSizeBytes).ToArray();

        var plaintextBytes = new byte[ciphertextLength];

        using (var aesGcm = new AesGcm(_key, TagSizeBytes))
        {
            aesGcm.Decrypt(nonce, ciphertext, tag, plaintextBytes);
        }

        return Encoding.UTF8.GetString(plaintextBytes);
    }
}
