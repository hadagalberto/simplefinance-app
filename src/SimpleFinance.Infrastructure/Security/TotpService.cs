using OtpNet;
using QRCoder;
using SimpleFinance.Application.Auth;

namespace SimpleFinance.Infrastructure.Security;

/// <summary>
/// Otp.NET-backed TOTP generation/verification + QRCoder-rendered provisioning QR codes.
/// SHA1/6-digit/30s step (Otp.NET defaults), matching Google/Microsoft/Authy authenticator
/// compatibility per research/STACK.md.
/// </summary>
public class TotpService : ITotpService
{
    private const string Issuer = "SimpleFinance";
    private const int Digits = 6;
    private const int StepSeconds = 30;

    // ±1 step (30s) of clock drift tolerance, per this plan's Test 3 ("60+ seconds outside the
    // current window, well beyond ±1 step tolerance, is rejected").
    private static readonly VerificationWindow ToleranceWindow = new(previous: 1, future: 1);

    public string GenerateSecret()
    {
        var key = KeyGeneration.GenerateRandomKey(20); // 160-bit, standard authenticator secret size.
        return Base32Encoding.ToString(key);
    }

    public string BuildOtpauthUri(string secret, string accountEmail)
    {
        var label = $"{Issuer}:{Uri.EscapeDataString(accountEmail)}";
        return $"otpauth://totp/{label}?secret={secret}&issuer={Issuer}&digits={Digits}&period={StepSeconds}";
    }

    public bool VerifyCode(string secret, string code)
    {
        if (string.IsNullOrWhiteSpace(secret) || string.IsNullOrWhiteSpace(code))
        {
            return false;
        }

        try
        {
            var keyBytes = Base32Encoding.ToBytes(secret);
            var totp = new Totp(keyBytes, step: StepSeconds, totpSize: Digits);
            return totp.VerifyTotp(code, out _, ToleranceWindow);
        }
        catch (Exception)
        {
            // Malformed secret (not valid Base32) or malformed code must never throw out of a
            // verification call — just fail closed like any other invalid code.
            return false;
        }
    }

    public byte[] GenerateQrCodePng(string otpauthUri)
    {
        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(otpauthUri, QRCodeGenerator.ECCLevel.Q);
        var pngQrCode = new PngByteQRCode(data);
        return pngQrCode.GetGraphic(20);
    }
}
