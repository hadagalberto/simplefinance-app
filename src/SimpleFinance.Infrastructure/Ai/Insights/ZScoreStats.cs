namespace SimpleFinance.Infrastructure.Ai.Insights;

/// <summary>
/// Pure, unit-testable robust-statistics primitive for anomaly detection (18-04, AI-INS-02).
/// Implements the Iglewicz–Hoaglin <b>modified z-score</b> (median + MAD) rather than the classic
/// mean/stddev z-score: on the tiny single-user samples this project produces (5–15 same-merchant
/// charges) a single large outlier inflates the classic stddev and masks itself, whereas the
/// median/MAD variant is outlier-resistant. See NIST/SEMATECH e-Handbook §1.3.5.17.
///
/// Deliberately has NO dependency on EF, the domain, or currency — it operates on plain
/// <see cref="double"/> history so it can be tested in complete isolation.
/// </summary>
public static class ZScoreStats
{
    /// <summary>The scaling constant Φ⁻¹(0.75) that makes MAD comparable to the standard deviation.</summary>
    private const double MadToSigma = 0.6745;

    /// <summary>Below this many historical points a modified z-score is not meaningful — returns null.</summary>
    public const int MinHistory = 6;

    /// <summary>
    /// The robust modified z-score of <paramref name="value"/> against <paramref name="history"/>.
    /// Returns <c>null</c> when there is insufficient history (&lt; <see cref="MinHistory"/> points).
    /// When the MAD is 0 (a constant baseline — common for identical subscription charges) the
    /// classic formula is undefined, so it falls back to a relative rule: only above a 50% jump does
    /// it emit <see cref="double.PositiveInfinity"/> (a definite flag); otherwise 0. Never returns ∞
    /// for a below-baseline value.
    /// </summary>
    public static double? ModifiedZScore(double value, IReadOnlyList<double> history)
    {
        if (history.Count < MinHistory)
        {
            return null;
        }

        var median = Median(history);
        var mad = Median(history.Select(x => Math.Abs(x - median)).ToList());
        if (mad == 0)
        {
            return value > median * 1.5 ? double.PositiveInfinity : 0;
        }

        return MadToSigma * (value - median) / mad;
    }

    private static double Median(IReadOnlyList<double> xs)
    {
        var sorted = xs.OrderBy(x => x).ToArray();
        var n = sorted.Length;
        var mid = n / 2;
        return n % 2 == 1 ? sorted[mid] : (sorted[mid - 1] + sorted[mid]) / 2.0;
    }
}
