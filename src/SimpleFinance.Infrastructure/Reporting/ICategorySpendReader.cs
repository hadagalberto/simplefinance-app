namespace SimpleFinance.Infrastructure.Reporting;

/// <summary>
/// Narrow read seam over <see cref="DashboardQueries.GetCategorySpendAsync"/> so the assistant's
/// <c>sum_by_category</c> tool (17-02) can be unit-tested with a fake while production reuses the real
/// Dapper aggregate verbatim. Implemented by <see cref="DashboardQueries"/>; registered as a bridge to
/// the already-registered concrete instance in <c>AddAssistantReadTools</c>.
/// </summary>
public interface ICategorySpendReader
{
    Task<IReadOnlyList<CategorySpend>> GetCategorySpendAsync(
        Guid userId, DateTime periodStart, DateTime periodEnd, CancellationToken ct);
}
