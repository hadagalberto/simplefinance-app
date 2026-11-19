namespace SimpleFinance.Infrastructure.Ai.Forecast;

/// <summary>
/// Tunable constants for the deterministic <see cref="ForecastCalculator"/> (19-RESEARCH Q1/Q2, A1).
/// Centralized here as constants — like <c>InsightThresholds</c> — so Phase 20 can re-tune the projection
/// blend + range width against real data WITHOUT touching the calculation logic. Every value is an MVP
/// heuristic (Claude's discretion per CONTEXT); the acceptance bar is "document the method" (<see
/// cref="Method"/>), not statistical rigor.
/// </summary>
public static class ForecastThresholds
{
    /// <summary>
    /// Weight <c>w</c> on the linear run-rate (paced) expense when blending with the trailing 3-month
    /// average: <c>blend = w*pace + (1-w)*avg3mo</c>. 0.5 = equal trust in "this month's pace" vs "history".
    /// </summary>
    public const decimal BlendWeight = 0.5m;

    /// <summary>Range half-width in standard deviations: <c>band = k*σ*(daysRemaining/daysInMonth)</c>. ±1σ.</summary>
    public const decimal RangeSigmaK = 1.0m;

    /// <summary>Sanity floor on the range as a fraction of projected expense — stops a near-zero σ (only ~3
    /// months of flat history) from collapsing the band to zero.</summary>
    public const decimal RangeFloorPct = 0.10m;

    /// <summary>Maximum trailing full months of history pulled for the σ estimate.</summary>
    public const int HistoryMonthsMax = 6;

    /// <summary>Minimum trailing full months required before σ is trusted (below this the floor dominates).</summary>
    public const int HistoryMonthsMin = 3;

    /// <summary>Trailing full months averaged for the avg3mo baseline (expense + income).</summary>
    public const int BaselineMonths = 3;

    /// <summary>Top-N per-category drivers surfaced in <c>ByCategoryJson</c> (ranked by projected spend).</summary>
    public const int TopDrivers = 5;

    /// <summary>The documented projection method stamped on every <c>Forecast</c> row (transparency/auditing).</summary>
    public const string Method = "recurring+3mo-avg";
}
