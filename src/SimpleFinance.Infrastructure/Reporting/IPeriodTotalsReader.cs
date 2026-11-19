namespace SimpleFinance.Infrastructure.Reporting;

/// <summary>
/// Narrow read seam over <see cref="DashboardQueries.GetTotalsAsync"/> so the deterministic
/// <c>ForecastCalculator</c> (19-02) can reuse the SAME realized income/expense aggregate the dashboard
/// uses — never a second, divergent formula — while staying unit-testable with a fake. Implemented by
/// <see cref="DashboardQueries"/>; registered as a bridge to the already-registered concrete instance in
/// <c>AddAssistantReadTools</c>, exactly like <see cref="ICategorySpendReader"/>.
/// </summary>
public interface IPeriodTotalsReader
{
    Task<DashboardTotals> GetTotalsAsync(
        Guid userId, DateTime periodStart, DateTime periodEnd, CancellationToken ct);
}
