using System.Text.RegularExpressions;

namespace SimpleFinance.Infrastructure.Recurring;

/// <summary>
/// RECUR-01 (07-02-PLAN.md Task 1): merchant-name normalization -- research/FEATURES.md's stated
/// highest-leverage lever for recurring-detection quality, since raw Pluggy descriptions vary
/// per-charge (POS-terminal noise, installment suffixes, trailing store/terminal numeric codes)
/// even when the underlying real-world merchant is identical. `RecurringDetector` groups by this
/// function's output; never throws on null/empty/whitespace-only input.
/// </summary>
public static partial class MerchantNormalizer
{
    // "PARC" (with an optional trailing period) followed by an N/M-shaped installment token,
    // e.g. "PARC 03/12" or "PARC. 3/12". Matched against the already-uppercased string.
    [GeneratedRegex(@"PARC\.?\s*\d{1,2}\s*/\s*\d{1,2}")]
    private static partial Regex InstallmentSuffixRegex();

    // A trailing run of 3+ consecutive digits anchored to the very end of the string (POS
    // terminal/store number noise) -- never digits embedded mid-string, so "NETFLIX.COM" and
    // "99TAXIS" are left untouched.
    [GeneratedRegex(@"\s*\d{3,}\s*$")]
    private static partial Regex TrailingStoreNumberRegex();

    // Collapses any run of whitespace to a single space; run last since a strip can leave a
    // double space (or leading/trailing space) behind.
    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRunRegex();

    public static string Normalize(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return string.Empty;
        }

        var normalized = raw.ToUpperInvariant().Replace('*', ' ');
        normalized = InstallmentSuffixRegex().Replace(normalized, string.Empty);
        normalized = TrailingStoreNumberRegex().Replace(normalized, string.Empty);
        normalized = WhitespaceRunRegex().Replace(normalized, " ").Trim();

        return normalized;
    }
}
