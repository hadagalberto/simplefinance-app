namespace SimpleFinance.Infrastructure.Ai.Insights;

/// <summary>
/// Tunable cut-offs + floors for <see cref="AnomalyDetector"/> (18-RESEARCH A1, AI-INS-02).
/// Centralized as constants so Phase 20 can re-tune with real data without touching detection logic.
/// The floors are the false-positive spine: an absolute-amount floor + a minimum history + the
/// RecurringTransaction allowlist stop legitimate small/recurring charges from ever firing.
/// </summary>
public static class AnomalyThresholds
{
    // --- per-merchant modified z-score ---

    /// <summary>Modified z-score above which a charge is an anomaly (Warning tier).</summary>
    public const double ModZWarn = 3.5;

    /// <summary>Modified z-score above which a charge escalates to Critical.</summary>
    public const double ModZCritical = 5.0;

    /// <summary>Minimum same-merchant history required before a z-score is meaningful.</summary>
    public const int MinHistory = 6;

    // --- duplicate charge ---

    /// <summary>Window (hours) inside which two identical merchant+amount charges are a duplicate.</summary>
    public const int DuplicateWindowHours = 24;

    /// <summary>Charges below this amount (R$) are ignored for duplicate detection (coffee/tolls/transit).</summary>
    public const decimal DuplicateAmountFloor = 20m;

    /// <summary>A duplicate at/above this amount (R$) is Critical instead of Warning.</summary>
    public const decimal DuplicateCriticalAmount = 500m;

    // --- subscription increase (reuses RecurringTransaction fields) ---

    /// <summary>Minimum relative increase (new avg vs previous avg) to flag a subscription increase.</summary>
    public const decimal SubIncreaseMinPct = 0.15m;

    /// <summary>At/above this relative increase a subscription increase escalates to Critical.</summary>
    public const decimal SubIncreaseCriticalPct = 0.50m;

    // --- atypical category spend (month vs trailing 3-month avg) ---

    /// <summary>Minimum relative jump vs the 3-month average to flag an atypical category (kept high
    /// so it does not double-fire with the milder SpendingChange insight from 18-02).</summary>
    public const decimal CategoryAnomalyMinPct = 0.50m;

    /// <summary>Minimum absolute jump (R$) — kills big-percentage jumps on small categories.</summary>
    public const decimal CategoryAnomalyMinAbsolute = 200m;

    /// <summary>Minimum 3-month baseline (R$) — kills division noise from near-empty categories.</summary>
    public const decimal CategoryAnomalyMinBaseline = 50m;

    /// <summary>At/above this relative jump an atypical-category anomaly escalates to Critical.</summary>
    public const decimal CategoryAnomalyCriticalPct = 1.00m;

    // --- window ---

    /// <summary>Bounded lookback (months) for the per-merchant transaction pull — keeps it cheap.</summary>
    public const int LookbackMonths = 12;
}
