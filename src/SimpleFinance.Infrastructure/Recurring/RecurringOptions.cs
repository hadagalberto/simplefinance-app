namespace SimpleFinance.Infrastructure.Recurring;

/// <summary>
/// Binds to the "Recurring" configuration section (mirrors TotpOptions's SectionName const
/// pattern). Drives both RecurringDetector's cadence/amount-tolerance classification (via
/// RecurringDetectionOptions, built from the first six properties below) and
/// RecurringDetectionJob's own rolling-window/amount-increase/cron behavior. Defaults here match
/// 07-CONTEXT.md's example thresholds -- tunable via appsettings.json/env vars without a code
/// change once real data suggests better values (PRD §20).
/// </summary>
public sealed class RecurringOptions
{
    public const string SectionName = "Recurring";

    /// <summary>How far back (in months) RecurringDetectionJob looks for eligible transactions.</summary>
    public int LookbackMonths { get; set; } = 18;

    public int MinOccurrences { get; set; } = 3;

    public decimal AmountTolerancePercent { get; set; } = 0.10m;

    public int WeeklyToleranceDays { get; set; } = 2;

    public int MonthlyMinDays { get; set; } = 25;

    public int MonthlyMaxDays { get; set; } = 35;

    public int YearlyToleranceDays { get; set; } = 15;

    /// <summary>
    /// RECUR-04's "amount increase beyond a threshold" trigger -- deliberately distinct from
    /// AmountTolerancePercent (the grouping tolerance used to decide which transactions belong to
    /// the same recurring group in the first place).
    /// </summary>
    public decimal AmountIncreaseThresholdPercent { get; set; } = 0.15m;

    /// <summary>Default: every Monday at 03:00 server-local time.</summary>
    public string DetectionCronExpression { get; set; } = "0 3 * * 1";
}
