namespace SimpleFinance.Infrastructure.Alerts;

/// <summary>
/// Binds to the "Alerts" configuration section (mirrors RecurringOptions's SectionName const
/// pattern). Drives AlertEvaluationJob's LargeTransaction lookback window -- every other
/// evaluator's query shape is fixed (rule.Threshold/DaysBefore already carry the tunable knobs
/// for LowBalance/InvoiceDue/LargeTransaction's threshold), so this is the only cross-cutting
/// tunable AlertEvaluationJob itself owns.
/// </summary>
public sealed class AlertEvaluationOptions
{
    public const string SectionName = "Alerts";

    /// <summary>
    /// How far back (in days) EvaluateLargeTransactionAsync scans for qualifying transactions.
    /// Safe to widen without risk of duplicate alerts -- LargeTransaction's de-dupe key is the
    /// transaction's own Id, not a date-based key, so a transaction can only ever fire once no
    /// matter how many times it re-enters this window on subsequent runs.
    /// </summary>
    public int LargeTransactionLookbackDays { get; set; } = 3;
}
