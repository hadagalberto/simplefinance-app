using Microsoft.EntityFrameworkCore;
using SimpleFinance.Domain.Entities;
using SimpleFinance.Domain.Enums;
using SimpleFinance.Infrastructure.Persistence;

namespace SimpleFinance.Infrastructure.Recurring;

/// <summary>A single recurring row for the panel, joined with its Category (null-safe, left-join
/// style -- mirrors DashboardQueries.GetCategorySpendAsync's null-CategoryId handling) plus a
/// computed YearlyTotal.</summary>
public sealed record RecurringItemResult(
    Guid Id,
    string DisplayName,
    Guid? CategoryId,
    string? CategoryName,
    string? CategoryIcon,
    string? CategoryColor,
    decimal AverageAmount,
    decimal? PreviousAverageAmount,
    string Frequency,
    DateTime FirstSeenDate,
    DateTime LastSeenDate,
    DateTime NextExpectedDate,
    int OccurrenceCount,
    decimal YearlyTotal,
    bool IsSubscription,
    bool IsActive,
    bool IsNewlyDetected,
    bool AmountIncreaseDetected);

/// <summary>RECUR-02/03: the full recurring-panel read shape -- MonthlyCommittedCost (RECUR-03)
/// sums every active item's AverageAmount normalized to a monthly figure.</summary>
public sealed record RecurringListResult(decimal MonthlyCommittedCost, IReadOnlyList<RecurringItemResult> Items);

/// <summary>
/// The real, directly-testable Recurring business logic (07-03 Task 2) behind GET/PATCH
/// /api/recurring/* -- RecurringEndpoints.cs is a thin HTTP wrapper over this class only, no
/// business logic duplicated there. RecurringDetectionJob (Task 1) is the sole writer of
/// RecurringTransaction rows; this service only reads and toggles user-owned flags
/// (IsSubscription/IsActive).
/// </summary>
public sealed class RecurringService(SimpleFinanceDbContext db)
{
    /// <summary>
    /// RECUR-02/03: lists every RecurringTransaction row for userId, joined with its Category
    /// (null-safe when CategoryId is null), plus RECUR-03's MonthlyCommittedCost -- the sum,
    /// across IsActive=true rows only, of each row's AverageAmount normalized to a monthly figure
    /// (Weekly * 52/12, Monthly * 1, Yearly / 12).
    /// </summary>
    public async Task<RecurringListResult> GetRecurringAsync(Guid userId, CancellationToken ct)
    {
        var rows = await db.RecurringTransactions.Where(r => r.UserId == userId).ToListAsync(ct);

        var categoryIds = rows
            .Where(r => r.CategoryId is not null)
            .Select(r => r.CategoryId!.Value)
            .Distinct()
            .ToList();

        var categoriesById = categoryIds.Count == 0
            ? new Dictionary<Guid, Category>()
            : await db.Categories.Where(c => categoryIds.Contains(c.Id)).ToDictionaryAsync(c => c.Id, ct);

        var items = rows.Select(r => ToItemResult(r, categoriesById)).ToList();

        var monthlyCommittedCost = rows
            .Where(r => r.IsActive)
            .Sum(r => ToMonthlyAmount(r.AverageAmount, r.Frequency));

        return new RecurringListResult(monthlyCommittedCost, items);
    }

    /// <summary>Scoped by both id and userId (T-07-04) -- returns false rather than throwing when
    /// the row doesn't exist or belongs to someone else, mirroring BudgetService.DeleteBudgetAsync's
    /// not-found contract, so the caller maps this to a 404 without a separate existence check.</summary>
    public async Task<bool> ToggleSubscriptionAsync(Guid userId, Guid id, bool isSubscription, CancellationToken ct)
    {
        var row = await db.RecurringTransactions.FirstOrDefaultAsync(r => r.Id == id && r.UserId == userId, ct);
        if (row is null)
        {
            return false;
        }

        row.IsSubscription = isSubscription;
        row.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        return true;
    }

    /// <summary>Same not-found contract as <see cref="ToggleSubscriptionAsync"/>.</summary>
    public async Task<bool> ToggleActiveAsync(Guid userId, Guid id, bool isActive, CancellationToken ct)
    {
        var row = await db.RecurringTransactions.FirstOrDefaultAsync(r => r.Id == id && r.UserId == userId, ct);
        if (row is null)
        {
            return false;
        }

        row.IsActive = isActive;
        row.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        return true;
    }

    private static RecurringItemResult ToItemResult(
        RecurringTransaction row, IReadOnlyDictionary<Guid, Category> categoriesById)
    {
        string? categoryName = null;
        string? categoryIcon = null;
        string? categoryColor = null;

        if (row.CategoryId is not null && categoriesById.TryGetValue(row.CategoryId.Value, out var category))
        {
            categoryName = category.Name;
            categoryIcon = category.Icon;
            categoryColor = category.Color;
        }

        var yearlyTotal = row.Frequency switch
        {
            RecurringFrequency.Weekly => row.AverageAmount * 52m,
            RecurringFrequency.Monthly => row.AverageAmount * 12m,
            RecurringFrequency.Yearly => row.AverageAmount,
            _ => row.AverageAmount,
        };

        return new RecurringItemResult(
            row.Id,
            row.DisplayName,
            row.CategoryId,
            categoryName,
            categoryIcon,
            categoryColor,
            row.AverageAmount,
            row.PreviousAverageAmount,
            row.Frequency.ToString(),
            row.FirstSeenDate,
            row.LastSeenDate,
            row.NextExpectedDate,
            row.OccurrenceCount,
            yearlyTotal,
            row.IsSubscription,
            row.IsActive,
            row.IsNewlyDetected,
            row.AmountIncreaseDetected);
    }

    private static decimal ToMonthlyAmount(decimal averageAmount, RecurringFrequency frequency) => frequency switch
    {
        RecurringFrequency.Weekly => averageAmount * (52m / 12m),
        RecurringFrequency.Monthly => averageAmount,
        RecurringFrequency.Yearly => averageAmount / 12m,
        _ => averageAmount,
    };
}
