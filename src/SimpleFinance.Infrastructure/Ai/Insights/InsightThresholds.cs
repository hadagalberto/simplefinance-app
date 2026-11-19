namespace SimpleFinance.Infrastructure.Ai.Insights;

/// <summary>
/// Tunable qualification floors + severity cut-offs for <see cref="InsightDetector"/> (18-RESEARCH A1).
/// Centralized here as constants so Phase 20 can re-tune with real data without touching detection logic.
/// The floors are the anti-noise spine: an absolute-amount floor + a minimum baseline stop a large
/// percentage move on a tiny category from ever firing, and <see cref="MaxSignalsPerRun"/> caps the strip.
/// </summary>
public static class InsightThresholds
{
    // --- SpendingChange (category month-over-3-month) ---

    /// <summary>Minimum relative rise (current vs 3-month avg) to qualify a SpendingChange.</summary>
    public const decimal SpendingChangeMinPct = 0.25m;

    /// <summary>Minimum absolute rise (R$) — kills big-percentage jumps on tiny amounts.</summary>
    public const decimal SpendingChangeMinAbsolute = 50m;

    /// <summary>Minimum 3-month baseline (R$) — kills division noise from near-empty categories.</summary>
    public const decimal MinBaseline = 50m;

    /// <summary>At/above this relative rise a SpendingChange escalates from Info to Warning.</summary>
    public const decimal SpendingChangeWarningPct = 0.50m;

    // --- BudgetRisk ---

    /// <summary>PercentOfCap (0-100 scale) at which a BudgetRisk starts (Warning tier).</summary>
    public const decimal BudgetRiskWarnTier = 80m;

    // --- Trend (net-worth / spend) ---

    /// <summary>Minimum total relative change across the trailing points to qualify a Trend.</summary>
    public const decimal TrendMinPct = 0.10m;

    /// <summary>Minimum number of monotone points required for a Trend.</summary>
    public const int TrendMinPoints = 3;

    // --- SubscriptionCreep ---

    /// <summary>Minimum relative increase (new avg vs previous avg) to qualify a SubscriptionCreep.</summary>
    public const decimal SubscriptionCreepMinPct = 0.15m;

    /// <summary>At/above this relative increase a SubscriptionCreep escalates to Critical.</summary>
    public const decimal SubscriptionCreepCriticalPct = 0.50m;

    // --- new-merchant (folds into SpendingChange) ---

    /// <summary>Minimum amount (R$) for a first-seen merchant this month to be worth surfacing.</summary>
    public const decimal NewMerchantMinAmount = 100m;

    // --- global cap ---

    /// <summary>Top-N signals (ranked by severity then magnitude) surfaced per run — anti-flood.</summary>
    public const int MaxSignalsPerRun = 5;
}
