namespace SimpleFinance.Infrastructure.Budgets;

/// <summary>
/// Narrow read seam over <see cref="BudgetService.GetBudgetsForMonthAsync"/> so the assistant's
/// <c>budget_status</c> tool (17-02) can be unit-tested with a fake while production reuses the real
/// derived-at-read-time budget picture. Implemented by <see cref="BudgetService"/>.
/// </summary>
public interface IBudgetReader
{
    Task<BudgetsResult> GetBudgetsForMonthAsync(Guid userId, int year, int month, CancellationToken ct);
}
