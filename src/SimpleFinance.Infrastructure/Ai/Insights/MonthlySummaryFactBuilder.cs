using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SimpleFinance.Infrastructure.Budgets;
using SimpleFinance.Infrastructure.Persistence;
using SimpleFinance.Infrastructure.Reporting;

namespace SimpleFinance.Infrastructure.Ai.Insights;

/// <summary>
/// The deterministic prior-month fact package for the monthly summary (19-03, AI-FCT-03). Assembles the
/// PRIOR month's (m0) totals + month-over-month deltas vs the month before (m1), spend-by-category + MoM,
/// budget vs actual, net-worth change, new/changed subscriptions, and top merchants into ONE JSON document
/// by reusing the existing reader bridges (<see cref="IPeriodTotalsReader"/> / <see cref="ICategorySpendReader"/>
/// / <see cref="IBudgetReader"/> / <see cref="ITopMerchantsReader"/>) + the <c>NetWorthSnapshots</c> /
/// <c>RecurringTransactions</c> tables verbatim — never a second, divergent formula. The NUMBERS are
/// authoritative and become <see cref="Domain.Entities.AiInsight.DataJson"/>; the reasoner only phrases prose
/// over them.
///
/// <b>PII (RESEARCH Pitfall 6 / T-19-07):</b> every merchant string — top-merchant names AND subscription
/// <c>DisplayName</c>s — passes through <see cref="IPiiRedactor.Redact"/> BEFORE it enters the package, so no
/// raw merchant text can leave the VPS. Category/budget/net-worth figures are first-party aggregates and carry
/// no account/card numbers (the package is aggregates only).
/// </summary>
public sealed class MonthlySummaryFactBuilder(
    IPeriodTotalsReader totals,
    ICategorySpendReader categorySpend,
    IBudgetReader budgets,
    ITopMerchantsReader topMerchants,
    SimpleFinanceDbContext db,
    IPiiRedactor redactor)
{
    private const int TopMerchantsLimit = 5;
    private const int TopCategoriesForFallback = 3;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Build the deterministic <see cref="MonthlyFactPackage"/> for the full month starting at
    /// <paramref name="priorMonthStart"/> (normalized to the 1st-of-month UTC). Identical inputs always yield
    /// identical numbers/JSON. Never invents a figure — every value is read from the deterministic sources.
    /// </summary>
    public async Task<MonthlyFactPackage> BuildAsync(Guid userId, DateTime priorMonthStart, CancellationToken ct)
    {
        var m0Start = new DateTime(priorMonthStart.Year, priorMonthStart.Month, 1, 0, 0, 0, DateTimeKind.Utc);
        var m0End = m0Start.AddMonths(1).AddTicks(-1);
        var m1Start = m0Start.AddMonths(-1);
        var m1End = m0Start.AddTicks(-1);
        var period = m0Start.ToString("yyyy-MM", CultureInfo.InvariantCulture);

        // --- totals + MoM (same aggregate the dashboard uses) ---
        var t0 = await totals.GetTotalsAsync(userId, m0Start, m0End, ct);
        var t1 = await totals.GetTotalsAsync(userId, m1Start, m1End, ct);
        var income = Round(t0.PeriodIncome);
        var expense = Round(t0.PeriodExpense);
        var balance = income - expense;
        var prevBalance = Round(t1.PeriodIncome) - Round(t1.PeriodExpense);

        var totalsFact = new
        {
            income,
            expense,
            balance,
            incomeMoM = income - Round(t1.PeriodIncome),
            expenseMoM = expense - Round(t1.PeriodExpense),
            balanceMoM = balance - prevBalance,
        };

        // --- spend by category + MoM delta (key by CategoryId, null => "uncategorized") ---
        var c0 = await categorySpend.GetCategorySpendAsync(userId, m0Start, m0End, ct);
        var c1 = await categorySpend.GetCategorySpendAsync(userId, m1Start, m1End, ct);
        var prevByCat = new Dictionary<string, decimal>();
        foreach (var row in c1)
        {
            prevByCat[CategoryKey(row.CategoryId)] = prevByCat.GetValueOrDefault(CategoryKey(row.CategoryId)) + row.Amount;
        }

        var byCategory = c0
            .Select(row =>
            {
                var prev = Round(prevByCat.GetValueOrDefault(CategoryKey(row.CategoryId)));
                var amount = Round(row.Amount);
                return new
                {
                    categoryId = row.CategoryId,
                    category = row.CategoryName ?? "Sem categoria",
                    amount,
                    previousAmount = prev,
                    delta = amount - prev,
                };
            })
            .OrderByDescending(x => x.amount)
            .ToList();

        // --- budget vs actual for m0 ---
        var budgetPicture = await budgets.GetBudgetsForMonthAsync(userId, m0Start.Year, m0Start.Month, ct);
        var budgetFacts = budgetPicture.Items
            .Select(b => new
            {
                category = b.CategoryName ?? "Global",
                limit = Round(b.LimitAmount),
                spent = Round(b.SpentAmount),
                percentOfCap = Round(b.PercentOfCap),
            })
            .ToList();

        // --- net-worth change: nearest snapshot at/ before each month-end ---
        var current = await NetWorthAtAsync(userId, m0End, ct);
        var previous = await NetWorthAtAsync(userId, m1End, ct);
        var netWorthFact = new { current, previous, change = current - previous };

        // --- new / changed subscriptions (DisplayName redacted) ---
        var subs = await db.RecurringTransactions
            .Where(r => r.UserId == userId && (r.IsNewlyDetected || r.AmountIncreaseDetected))
            .Select(r => new { r.DisplayName, r.AverageAmount, r.PreviousAverageAmount, r.IsNewlyDetected, r.AmountIncreaseDetected })
            .ToListAsync(ct);
        var subscriptionFacts = subs
            .Select(s => new
            {
                merchant = redactor.Redact(s.DisplayName),
                averageAmount = Round(s.AverageAmount),
                previousAverageAmount = s.PreviousAverageAmount.HasValue ? Round(s.PreviousAverageAmount.Value) : (decimal?)null,
                isNew = s.IsNewlyDetected,
                amountIncreased = s.AmountIncreaseDetected,
            })
            .ToList();

        // --- top merchants (names redacted at source) ---
        var merchants = await topMerchants.GetTopMerchantsAsync(userId, m0Start, m0End, TopMerchantsLimit, ct);
        var merchantFacts = merchants
            .Select(m => new
            {
                merchant = redactor.Redact(m.MerchantName),
                totalSpent = Round(m.TotalSpent),
                txnCount = m.TxnCount,
            })
            .ToList();

        var payload = new
        {
            period,
            totals = totalsFact,
            byCategory,
            budgets = budgetFacts,
            netWorth = netWorthFact,
            subscriptions = subscriptionFacts,
            topMerchants = merchantFacts,
        };

        var json = JsonSerializer.Serialize(payload, JsonOptions);

        var topCategories = byCategory
            .Take(TopCategoriesForFallback)
            .Select(x => new FallbackCategory(x.category, x.amount))
            .ToList();

        return new MonthlyFactPackage
        {
            PeriodStart = m0Start,
            PeriodEnd = m0End,
            PeriodLabel = period,
            Income = income,
            Expense = expense,
            Balance = balance,
            TopCategories = topCategories,
            Json = json,
        };
    }

    private async Task<decimal> NetWorthAtAsync(Guid userId, DateTime asOf, CancellationToken ct)
    {
        var snapshot = await db.NetWorthSnapshots
            .Where(s => s.UserId == userId && s.Date <= asOf)
            .OrderByDescending(s => s.Date)
            .Select(s => (decimal?)s.NetWorth)
            .FirstOrDefaultAsync(ct);
        return Round(snapshot ?? 0m);
    }

    private static string CategoryKey(Guid? categoryId) => categoryId?.ToString() ?? "uncategorized";

    private static decimal Round(decimal value) => Math.Round(value, 2, MidpointRounding.AwayFromZero);
}

/// <summary>
/// The deterministic prior-month fact package produced by <see cref="MonthlySummaryFactBuilder"/>. <see
/// cref="Json"/> is the full fact document persisted as <c>AiInsight.DataJson</c> and sent to the reasoner;
/// the flat fields (<see cref="Income"/>/<see cref="Expense"/>/<see cref="Balance"/>/<see cref="TopCategories"/>)
/// back the deterministic fallback summary so an over-budget/failed reasoner run still persists a grounded
/// Title/Body.
/// </summary>
public sealed class MonthlyFactPackage
{
    public DateTime PeriodStart { get; init; }

    public DateTime PeriodEnd { get; init; }

    public string PeriodLabel { get; init; } = string.Empty;

    public decimal Income { get; init; }

    public decimal Expense { get; init; }

    public decimal Balance { get; init; }

    public IReadOnlyList<FallbackCategory> TopCategories { get; init; } = [];

    public string Json { get; init; } = "{}";
}

/// <summary>A top category (first-party name, no PII) carried for the deterministic fallback body.</summary>
public sealed record FallbackCategory(string Name, decimal Amount);
