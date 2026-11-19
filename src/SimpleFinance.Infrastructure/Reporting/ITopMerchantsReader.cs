namespace SimpleFinance.Infrastructure.Reporting;

/// <summary>
/// Narrow read seam over <see cref="ReportQueries.GetTopMerchantsAsync"/> so the deterministic
/// <c>MonthlySummaryFactBuilder</c> (19-03) can reuse the real top-merchants Dapper aggregate verbatim while
/// staying unit-testable with a fake — the same reader-bridge pattern as <see cref="ICategorySpendReader"/> /
/// <see cref="IPeriodTotalsReader"/>. Implemented by <see cref="ReportQueries"/>; registered as a bridge to
/// the already-registered concrete instance in <c>AddAssistantReadTools</c>.
/// </summary>
public interface ITopMerchantsReader
{
    Task<IReadOnlyList<TopMerchantRow>> GetTopMerchantsAsync(
        Guid userId, DateTime periodStart, DateTime periodEnd, int limit, CancellationToken ct);
}
