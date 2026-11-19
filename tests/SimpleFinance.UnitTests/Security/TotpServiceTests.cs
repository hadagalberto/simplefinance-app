using System.Security.Cryptography;
using Microsoft.Extensions.Options;
using OtpNet;
using SimpleFinance.Infrastructure.Security;

namespace SimpleFinance.UnitTests.Security;

public class TotpServiceTests
{
    private readonly TotpService _sut = new();

    [Fact]
    public void GenerateSecret_ReturnsValidBase32UsableForOtpauthUriWithIssuerAndEmailLabel()
    {
        var secret = _sut.GenerateSecret();

        // Must be decodable as Base32 (OtpNet's own decoder — throws on invalid input).
        var decoded = Base32Encoding.ToBytes(secret);
        Assert.True(decoded.Length > 0);

        const string email = "owner@example.com";
        var uri = _sut.BuildOtpauthUri(secret, email);

        Assert.StartsWith("otpauth://totp/", uri);
        Assert.Contains("issuer=SimpleFinance", uri);
        Assert.Contains("SimpleFinance", uri);
        Assert.Contains(Uri.EscapeDataString(email), uri);
        Assert.Contains($"secret={secret}", uri);
    }

    [Fact]
    public void VerifyCode_CodeGeneratedByOtpNetForKnownSecretNow_IsAccepted()
    {
        var secret = _sut.GenerateSecret();
        var keyBytes = Base32Encoding.ToBytes(secret);
        var totp = new Totp(keyBytes, step: 30, totpSize: 6);
        var code = totp.ComputeTotp(DateTime.UtcNow);

        Assert.True(_sut.VerifyCode(secret, code));
    }

    [Fact]
    public void VerifyCode_CodeFrom60SecondsOutsideWindow_IsRejected()
    {
        var secret = _sut.GenerateSecret();
        var keyBytes = Base32Encoding.ToBytes(secret);
        var totp = new Totp(keyBytes, step: 30, totpSize: 6);
        var staleCode = totp.ComputeTotp(DateTime.UtcNow.AddSeconds(-90));

        Assert.False(_sut.VerifyCode(secret, staleCode));
    }

    [Fact]
    public void AesTotpSecretProtector_EncryptThenDecrypt_RoundTripsAndNeverEqualsPlaintext()
    {
        var protector = new AesTotpSecretProtector(Options.Create(new TotpOptions
        {
            EncryptionKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)),
        }));

        var secret = _sut.GenerateSecret();
        var encrypted = protector.Encrypt(secret);

        Assert.NotEqual(secret, encrypted);

        var decrypted = protector.Decrypt(encrypted);
        Assert.Equal(secret, decrypted);
    }
}
