using SimpleFinance.Infrastructure.Recurring;

namespace SimpleFinance.UnitTests.Recurring;

/// <summary>
/// RECUR-01 (07-02-PLAN.md Task 1): proves MerchantNormalizer.Normalize strips POS-terminal
/// noise (`*`), installment suffixes (`PARC N/M`), and trailing store/terminal numeric codes,
/// while leaving genuinely stable merchant strings (e.g. domain-shaped names with no trailing
/// digit run) untouched besides case/whitespace normalization -- the highest-leverage
/// correctness lever per research/FEATURES.md for grouping the same real-world merchant across
/// slightly different raw descriptions.
/// </summary>
public class MerchantNormalizerTests
{
    [Fact]
    public void Normalize_StripsPosNoiseAndTrailingStoreNumber()
    {
        var result = MerchantNormalizer.Normalize("IFOOD *RESTAURANTE XYZ 1234");

        Assert.Equal("IFOOD RESTAURANTE XYZ", result);
    }

    [Fact]
    public void Normalize_StripsInstallmentSuffix()
    {
        var result = MerchantNormalizer.Normalize("Spotify AB PARC 03/12");

        Assert.Equal("SPOTIFY AB", result);
    }

    [Fact]
    public void Normalize_CollapsesWhitespaceAndUppercases()
    {
        var result = MerchantNormalizer.Normalize("  Uber   *Trip   Help.uber.com  ");

        Assert.Equal("UBER TRIP HELP.UBER.COM", result);
    }

    [Fact]
    public void Normalize_LeavesStableDomainShapedNameUntouched()
    {
        var result = MerchantNormalizer.Normalize("NETFLIX.COM");

        Assert.Equal("NETFLIX.COM", result);
    }

    [Fact]
    public void Normalize_DoesNotStripEmbeddedMidStringDigits()
    {
        var result = MerchantNormalizer.Normalize("99TAXIS");

        Assert.Equal("99TAXIS", result);
    }

    [Fact]
    public void Normalize_NullReturnsEmpty()
    {
        var result = MerchantNormalizer.Normalize(null);

        Assert.Equal(string.Empty, result);
    }

    [Fact]
    public void Normalize_WhitespaceOnlyReturnsEmpty()
    {
        var result = MerchantNormalizer.Normalize("   ");

        Assert.Equal(string.Empty, result);
    }

    [Fact]
    public void Normalize_StripsInstallmentSuffixWithPeriodAndSingleDigits()
    {
        var result = MerchantNormalizer.Normalize("SOME STORE PARC. 3/12");

        Assert.Equal("SOME STORE", result);
    }
}
