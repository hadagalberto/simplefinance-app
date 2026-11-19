using System.Text.RegularExpressions;

namespace SimpleFinance.Infrastructure.Ai;

/// <summary>
/// AI-FND-06 hard privacy invariant (T-15-04). Source-generated (compiled, AOT-safe) regexes strip
/// PII from any free-text string before it can leave the VPS to a third-party LLM/embeddings API.
/// Patterns are applied in a deliberate order — CNPJ, CPF, card-like (Luhn-confirmed), token-like,
/// then long digit runs — so specific 14/11-digit Brazilian document formats are masked before the
/// generic 6+ digit fallback, and the Luhn check keeps long non-card order/account numbers from
/// being masked as PANs. The provider clients (15-05) call <see cref="Redact"/> internally so no
/// caller can bypass it.
/// </summary>
public sealed partial class PiiRedactor : IPiiRedactor
{
    private const string Mask = "«REDACTED»";

    // 14 digits with optional . / - formatting (##.###.###/####-## or bare).
    [GeneratedRegex(@"\b\d{2}\.?\d{3}\.?\d{3}/?\d{4}-?\d{2}\b")]
    private static partial Regex Cnpj();

    // 11 digits with optional . - formatting (###.###.###-## or bare).
    [GeneratedRegex(@"\b\d{3}\.?\d{3}\.?\d{3}-?\d{2}\b")]
    private static partial Regex Cpf();

    // 13–19 digit runs with optional space/dash separators; confirmed by Luhn before masking.
    [GeneratedRegex(@"\b(?:\d[ -]?){13,19}\b")]
    private static partial Regex CardLike();

    // UUIDs and long hex secrets/tokens (item IDs, connect tokens) accidentally present in text.
    [GeneratedRegex(@"\b[0-9a-fA-F-]{32,}\b")]
    private static partial Regex TokenLike();

    // Bare 6+ digit account/agency runs left after the specific passes above.
    [GeneratedRegex(@"\b\d{6,}\b")]
    private static partial Regex LongDigits();

    public string Redact(string? input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return string.Empty;
        }

        var s = Cnpj().Replace(input, Mask);        // CNPJ (14) before CPF (11) before generic runs
        s = Cpf().Replace(s, Mask);
        s = CardLike().Replace(s, m => IsLuhnValid(m.Value) ? Mask : m.Value);
        s = TokenLike().Replace(s, Mask);
        s = LongDigits().Replace(s, Mask);
        return s;
    }

    /// <summary>Standard Luhn mod-10 check over the digits of <paramref name="candidate"/> (ignores separators).</summary>
    private static bool IsLuhnValid(string candidate)
    {
        var sum = 0;
        var digitCount = 0;
        var alternate = false;

        // Walk right-to-left, doubling every second digit.
        for (var i = candidate.Length - 1; i >= 0; i--)
        {
            var c = candidate[i];
            if (c < '0' || c > '9')
            {
                continue;
            }

            digitCount++;
            var d = c - '0';
            if (alternate)
            {
                d *= 2;
                if (d > 9)
                {
                    d -= 9;
                }
            }

            sum += d;
            alternate = !alternate;
        }

        return digitCount > 0 && sum % 10 == 0;
    }
}
