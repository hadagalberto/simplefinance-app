using Microsoft.EntityFrameworkCore;
using SimpleFinance.Application.Budgets;
using SimpleFinance.Domain.Entities;
using SimpleFinance.Infrastructure.Persistence;
using SimpleFinance.Infrastructure.Reporting;

namespace SimpleFinance.Infrastructure.Budgets;

/// <summary>
/// The full derived-at-read-time picture for a single Budget row (category-scoped or the
/// CategoryId=null global cap), per 05-05-PLAN.md's interfaces block. LimitAmount is the only
/// field actually persisted on <see cref="Budget"/> -- everything else is recomputed live by
/// <see cref="BudgetCalculator"/> against this month's real spend (via
/// <see cref="DashboardQueries.GetCategorySpendAsync"/>), so it can never go stale.
/// </summary>
public sealed record BudgetRowResult(
    Guid Id,
    Guid? CategoryId,
    string? CategoryName,
    string? CategoryIcon,
    string? CategoryColor,
    decimal LimitAmount,
    decimal SpentAmount,
    decimal PercentOfCap,
    decimal Remaining,
    decimal ProjectedSpend,
    decimal ProjectedPercent,
    BudgetState State,
    bool IsAtRisk);

/// <summary>
/// The full month's budget picture, per 05-05-PLAN.md's interfaces block. CopiedFromPreviousMonth
/// is true only on the exact call that performed the copy-forward (05-CONTEXT.md's locked
/// default) -- a second call for the same (user, year, month) always returns false, since rows
/// now exist for that month and no further copy is attempted.
/// </summary>
public sealed record BudgetsResult(int Year, int Month, bool CopiedFromPreviousMonth, IReadOnlyList<BudgetRowResult> Items);

/// <summary>
/// The real, directly-testable Budget business logic (05-05 Task 1) -- BUD-01's set/edit limit,
/// BUD-02/03/04's live-computed spend/state/at-risk read, and 05-CONTEXT.md's copy-forward
/// default. Deliberately placed in Infrastructure (not Application), mirroring
/// SyncConnectionUseCase's (02-04) documented rationale: it depends directly on
/// SimpleFinanceDbContext, and Application cannot reference Infrastructure without a circular
/// project reference. BudgetsEndpoints.cs (Task 2) is a thin HTTP wrapper over this class only --
/// no business logic duplicated there.
/// </summary>
public sealed class BudgetService(SimpleFinanceDbContext db, DashboardQueries dashboardQueries) : IBudgetReader
{
    /// <summary>
    /// BUD-02/03/04 read path + 05-CONTEXT.md's copy-forward default. If no Budget rows exist yet
    /// for (userId, year, month), the prior month's rows (if any) are copied forward verbatim
    /// (same CategoryId/LimitAmount, new Year/Month) and persisted immediately -- so a SECOND call
    /// for the same month always finds rows already present and returns
    /// CopiedFromPreviousMonth=false (idempotent, no re-copy).
    /// </summary>
    public async Task<BudgetsResult> GetBudgetsForMonthAsync(Guid userId, int year, int month, CancellationToken ct)
    {
        var budgets = await db.Budgets
            .Where(b => b.UserId == userId && b.Year == year && b.Month == month)
            .ToListAsync(ct);

        var copiedFromPreviousMonth = false;

        if (budgets.Count == 0)
        {
            var (prevYear, prevMonth) = PreviousMonth(year, month);

            var priorBudgets = await db.Budgets
                .Where(b => b.UserId == userId && b.Year == prevYear && b.Month == prevMonth)
                .ToListAsync(ct);

            if (priorBudgets.Count > 0)
            {
                var now = DateTime.UtcNow;
                var copied = priorBudgets.Select(p => new Budget
                {
                    Id = Guid.NewGuid(),
                    UserId = userId,
                    CategoryId = p.CategoryId,
                    Year = year,
                    Month = month,
                    LimitAmount = p.LimitAmount,
                    CreatedAt = now,
                    UpdatedAt = now,
                }).ToList();

                db.Budgets.AddRange(copied);
                await db.SaveChangesAsync(ct);

                budgets = copied;
                copiedFromPreviousMonth = true;
            }
        }

        if (budgets.Count == 0)
        {
            return new BudgetsResult(year, month, copiedFromPreviousMonth, []);
        }

        var (periodStart, periodEnd) = GetPeriodRange(year, month);
        var spendRows = await dashboardQueries.GetCategorySpendAsync(userId, periodStart, periodEnd, ct);

        var categoryIds = budgets
            .Where(b => b.CategoryId is not null)
            .Select(b => b.CategoryId!.Value)
            .Distinct()
            .ToList();

        var categoriesById = categoryIds.Count == 0
            ? new Dictionary<Guid, Category>()
            : await db.Categories.Where(c => categoryIds.Contains(c.Id)).ToDictionaryAsync(c => c.Id, ct);

        var items = budgets.Select(b => ComputeRow(b, spendRows, categoriesById)).ToList();

        return new BudgetsResult(year, month, copiedFromPreviousMonth, items);
    }

    /// <summary>
    /// BUD-01's set/edit-limit write path. When categoryId is non-null, first confirms it exists
    /// and belongs to userId -- returns null (never throws) when it doesn't, mirroring
    /// RecategorizeTransactionValidator's ownership-check shape but performed inline since
    /// BudgetService has no FluentValidation pipeline of its own (T-05-07's mitigation).
    /// Finds an existing row by the exact (UserId, CategoryId, Year, Month) tuple -- an EF Core
    /// nullable-FK comparison correctly matches both a specific category and the CategoryId=null
    /// global cap -- and updates it in place rather than creating a duplicate (proves the
    /// unique-index-backed lookup, not just relying on the DB constraint to throw).
    /// </summary>
    public async Task<BudgetRowResult?> UpsertBudgetAsync(
        Guid userId, Guid? categoryId, int year, int month, decimal limitAmount, CancellationToken ct)
    {
        if (categoryId is not null)
        {
            var categoryOwned = await db.Categories.AnyAsync(c => c.Id == categoryId.Value && c.UserId == userId, ct);
            if (!categoryOwned)
            {
                return null;
            }
        }

        var existing = await db.Budgets.FirstOrDefaultAsync(
            b => b.UserId == userId && b.CategoryId == categoryId && b.Year == year && b.Month == month, ct);

        var now = DateTime.UtcNow;
        Budget budget;

        if (existing is not null)
        {
            existing.LimitAmount = limitAmount;
            existing.UpdatedAt = now;
            budget = existing;
        }
        else
        {
            budget = new Budget
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                CategoryId = categoryId,
                Year = year,
                Month = month,
                LimitAmount = limitAmount,
                CreatedAt = now,
                UpdatedAt = now,
            };
            db.Budgets.Add(budget);
        }

        await db.SaveChangesAsync(ct);

        return await ComputeSingleRowAsync(budget, userId, ct);
    }

    /// <summary>Scoped by both budgetId and userId (T-05-08) -- returns false rather than
    /// throwing when the row doesn't exist or belongs to someone else, so the caller maps this
    /// to a 404 without needing a separate existence check.</summary>
    public async Task<bool> DeleteBudgetAsync(Guid userId, Guid budgetId, CancellationToken ct)
    {
        var budget = await db.Budgets.FirstOrDefaultAsync(b => b.Id == budgetId && b.UserId == userId, ct);
        if (budget is null)
        {
            return false;
        }

        db.Budgets.Remove(budget);
        await db.SaveChangesAsync(ct);
        return true;
    }

    // Single-row variant used only by UpsertBudgetAsync -- fetches just enough spend/category
    // data for the one row just written, then delegates to the exact same ComputeRow helper
    // GetBudgetsForMonthAsync uses, so POST and GET can never diverge on the spend/state math.
    private async Task<BudgetRowResult> ComputeSingleRowAsync(Budget budget, Guid userId, CancellationToken ct)
    {
        var (periodStart, periodEnd) = GetPeriodRange(budget.Year, budget.Month);
        var spendRows = await dashboardQueries.GetCategorySpendAsync(userId, periodStart, periodEnd, ct);

        var categoriesById = new Dictionary<Guid, Category>();
        if (budget.CategoryId is not null)
        {
            var category = await db.Categories.FirstOrDefaultAsync(c => c.Id == budget.CategoryId.Value, ct);
            if (category is not null)
            {
                categoriesById[category.Id] = category;
            }
        }

        return ComputeRow(budget, spendRows, categoriesById);
    }

    // Shared row-computation helper (per the plan's explicit instruction) so GET and POST never
    // diverge. A CategoryId=null row (global cap) sums EVERY spend row returned for the month
    // (total expense across all categories, per 05-CONTEXT.md's "optional global monthly cap"
    // semantics); a category row looks up its own matching spend row, defaulting to 0 when the
    // category had no spend at all this month.
    private static BudgetRowResult ComputeRow(
        Budget budget, IReadOnlyList<CategorySpend> spendRows, IReadOnlyDictionary<Guid, Category> categoriesById)
    {
        decimal spentAmount;
        string? categoryName = null;
        string? categoryIcon = null;
        string? categoryColor = null;

        if (budget.CategoryId is null)
        {
            spentAmount = spendRows.Sum(r => r.Amount);
        }
        else
        {
            spentAmount = spendRows.FirstOrDefault(r => r.CategoryId == budget.CategoryId)?.Amount ?? 0m;

            if (categoriesById.TryGetValue(budget.CategoryId.Value, out var category))
            {
                categoryName = category.Name;
                categoryIcon = category.Icon;
                categoryColor = category.Color;
            }
        }

        var dayOfMonth = DateTime.UtcNow.Day;
        var daysInMonth = DateTime.DaysInMonth(budget.Year, budget.Month);
        var progress = BudgetCalculator.Calculate(budget.LimitAmount, spentAmount, dayOfMonth, daysInMonth);

        return new BudgetRowResult(
            budget.Id,
            budget.CategoryId,
            categoryName,
            categoryIcon,
            categoryColor,
            budget.LimitAmount,
            progress.SpentAmount,
            progress.PercentOfCap,
            progress.Remaining,
            progress.ProjectedSpend,
            progress.ProjectedPercent,
            progress.State,
            progress.IsAtRisk);
    }

    // Inclusive [first day 00:00:00 UTC, last day 23:59:59 UTC] range -- matches
    // DashboardQueries.cs's documented "periodStart/periodEnd... always inclusive on both ends"
    // convention exactly.
    private static (DateTime Start, DateTime End) GetPeriodRange(int year, int month)
    {
        var start = new DateTime(year, month, 1, 0, 0, 0, DateTimeKind.Utc);
        var end = new DateTime(year, month, DateTime.DaysInMonth(year, month), 23, 59, 59, DateTimeKind.Utc);
        return (start, end);
    }

    // January's rollover: month 1 -> month 12 of year-1.
    private static (int Year, int Month) PreviousMonth(int year, int month) =>
        month == 1 ? (year - 1, 12) : (year, month - 1);
}
