namespace SimpleFinance.Infrastructure.Ai.Insights;

/// <summary>
/// The pure, deterministic insight-signal detector (18-02, AI-INS-01). Given a user and a reference date,
/// computes every qualifying <see cref="InsightSignal"/> (SpendingChange incl. new-merchant fold, Trend,
/// BudgetRisk, SubscriptionCreep) by reusing the EXISTING reporting reader bridges
/// (<c>ICategorySpendReader</c>/<c>IBudgetReader</c>/<c>INetWorthReader</c>) and
/// <see cref="Domain.Entities.RecurringTransaction"/> fields — never a second, divergent formula. No LLM,
/// no persistence: the caller (18-03's job) feeds the returned list to the prose layer and Upserts it.
/// Fully unit-testable against fake readers + an in-memory transaction/recurring set.
/// </summary>
public interface IInsightDetector
{
    /// <summary>
    /// Detect the qualifying signals for <paramref name="userId"/> as of <paramref name="referenceDate"/>
    /// (the "current" month is the calendar month of that date; baselines are the trailing full months).
    /// Every read is UserId-scoped. The list is ranked by severity then magnitude and truncated to
    /// <see cref="InsightThresholds.MaxSignalsPerRun"/>.
    /// </summary>
    Task<IReadOnlyList<InsightSignal>> DetectAsync(Guid userId, DateTime referenceDate, CancellationToken ct);
}
