using System.Text.RegularExpressions;
using SimpleFinance.Infrastructure.Ai;

namespace SimpleFinance.UnitTests.Ai;

/// <summary>
/// AI-FND-06 (15-03 Task 1): the PiiRedactor is the hard privacy invariant — the provider clients
/// (15-05) call it internally on every outbound string so no caller can bypass it. These tests pin
/// the guarantee: CPF, CNPJ, Luhn-valid card/PAN, token/UUID strings, and long account/agency runs
/// are masked with the stable <c>«REDACTED»</c> token, while benign merchant text is untouched.
/// </summary>
public class PiiRedactorTests
{
    private const string Mask = "«REDACTED»";

    private static readonly PiiRedactor Redactor = new();

    [Theory]
    [InlineData("CPF 123.456.789-09")]   // formatted
    [InlineData("CPF 12345678909")]      // bare
    public void Redact_MasksCpf_NoDigitsSurvive(string input)
    {
        var result = Redactor.Redact(input);

        Assert.Contains(Mask, result);
        Assert.DoesNotMatch(new Regex(@"\d"), result); // none of the CPF digits survive
    }

    [Theory]
    [InlineData("CNPJ 12.345.678/0001-95")] // formatted
    [InlineData("CNPJ 12345678000195")]     // bare (14 digits)
    public void Redact_MasksCnpj(string input)
    {
        var result = Redactor.Redact(input);

        Assert.Contains(Mask, result);
        Assert.DoesNotMatch(new Regex(@"\d"), result);
    }

    [Fact]
    public void Redact_MasksLuhnValidCardPan()
    {
        var result = Redactor.Redact("Cartao 4111 1111 1111 1111");

        Assert.Contains(Mask, result);
        Assert.DoesNotContain("4111", result);
    }

    [Fact]
    public void Redact_LeavesLongNonLuhnOrderIdIntact()
    {
        // 16 grouped digits that fail Luhn: the card stage leaves them (Luhn guard), and the
        // 4-digit groups escape the 6+ long-digit-run pass — proving Luhn cuts false positives.
        const string orderId = "Pedido 1234 5678 9012 3456";

        var result = Redactor.Redact(orderId);

        Assert.Equal(orderId, result);
    }

    [Theory]
    [InlineData("Ref 550e8400-e29b-41d4-a716-446655440000")]           // UUID
    [InlineData("Token a1b2c3d4e5f6a7b8c9d0e1f2a3b4c5d6e7f8a9b0")]     // 40-char hex secret
    public void Redact_MasksTokenLikeStrings(string input)
    {
        var result = Redactor.Redact(input);

        Assert.Contains(Mask, result);
    }

    [Fact]
    public void Redact_MasksBareLongDigitAccountRun()
    {
        var result = Redactor.Redact("Conta 1234567");

        Assert.Contains(Mask, result);
        Assert.DoesNotContain("1234567", result);
    }

    [Theory]
    [InlineData("Padaria do Bairro")]
    [InlineData("Amount 12.34")]
    [InlineData("Uber *trip SP")]
    public void Redact_LeavesBenignTextUnchanged(string input)
    {
        var result = Redactor.Redact(input);

        Assert.Equal(input, result);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Redact_NullOrWhitespace_ReturnsEmptyString_WithoutThrowing(string? input)
    {
        var result = Redactor.Redact(input);

        Assert.Equal(string.Empty, result);
    }
}
