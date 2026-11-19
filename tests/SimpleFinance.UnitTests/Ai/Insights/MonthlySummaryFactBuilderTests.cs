using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SimpleFinance.Domain.Entities;
using SimpleFinance.Infrastructure.Ai;
using SimpleFinance.Infrastructure.Ai.Insights;
using SimpleFinance.Infrastructure.Budgets;
using SimpleFinance.Infrastructure.Persistence;
using SimpleFinance.Infrastructure.Reporting;

namespace SimpleFinance.UnitTests.Ai.Insights;

/// <summary>
/// Behavior tests for <see cref="MonthlySummaryFactBuilder"/> (19-03, AI-FCT-03). Proves the deterministic
/// prior-month fact package against FAKE reader bridges + an InMemory recurring/snapshot set: every merchant
/// string (top merchants + subscription DisplayName) is routed through <see cref="IPiiRedactor"/> BEFORE it
/// can enter the package (RESEARCH Pitfall 6 / T-19-07), month-over-month deltas are computed from the two
/// windows, and the package serializes to valid JSON carrying all fact groups.
/// </summary>
public class MonthlySummaryFactBuilderTests
{
    // Day-1 run in July → the prior month is June 2026.
    private static readonly DateTime PriorMonthStart = new(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc);

    private static SimpleFinanceDbContext CreateDbContext() =>
        new(new DbContextOptionsBuilder<SimpleFinanceDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private static MonthlySummaryFactBuilder Build(
        SimpleFinanceDbContext db,
        IPeriodTotalsReader totals,
        ICategorySpendReader cat,
        IBudgetReader budgets,
        ITopMerchantsReader merchants,
        IPiiRedactor redactor) =>
        new(totals, cat, budgets, merchants, db, redactor);

    private static RecurringTransaction Recurring(
        Guid userId, string displayName, decimal amount, bool isNew, bool increased,
        decimal? previous = null) => new()
    {
        Id = Guid.NewGuid(),
        UserId = userId,
        NormalizedMerchant = "N" + Guid.NewGuid().ToString("N")[..6],
        DisplayName = displayName,
        AverageAmount = amount,
        PreviousAverageAmount = previous,
        IsNewlyDetected = isNew,
        AmountIncreaseDetected = increased,
        NextExpectedDate = new DateTime(2026, 7, 5, 0, 0, 0, DateTimeKind.Utc),
        CreatedAt = DateTime.UtcNow,
        UpdatedAt = DateTime.UtcNow,
    };

    private static NetWorthSnapshot Snapshot(Guid userId, DateTime date, decimal netWorth) => new()
    {
        Id = Guid.NewGuid(),
        UserId = userId,
        Date = date,
        NetWorth = netWorth,
        CreatedAt = DateTime.UtcNow,
        UpdatedAt = DateTime.UtcNow,
    };

    [Fact]
    public async Task Build_RedactsEveryMerchantString_TopMerchantsAndSubscriptions()
    {
        await using var db = CreateDbContext();
        var userId = Guid.NewGuid();

        db.RecurringTransactions.Add(Recurring(userId, "RAW_SUB_MERCHANT", 50m, isNew: true, increased: false));
        await db.SaveChangesAsync();

        var totals = new FakeTotals();
        totals.Set("2026-06", income: 8000m, expense: 5000m);

        var merchants = new FakeTopMerchants([new TopMerchantRow("RAW_TOP_MERCHANT", 900m, 4)]);

        var f = await Build(db, totals, new FakeCategorySpend(), new FakeBudgets(),
            merchants, new MaskRedactor()).BuildAsync(userId, PriorMonthStart, CancellationToken.None);

        // Raw merchant strings must NEVER appear; every merchant is masked at source.
        Assert.DoesNotContain("RAW_TOP_MERCHANT", f.Json);
        Assert.DoesNotContain("RAW_SUB_MERCHANT", f.Json);

        using var doc = JsonDocument.Parse(f.Json);
        var root = doc.RootElement;
        var topMerchant = root.GetProperty("topMerchants").EnumerateArray().Single();
        Assert.Equal("[MASKED]", topMerchant.GetProperty("merchant").GetString());
        var sub = root.GetProperty("subscriptions").EnumerateArray().Single();
        Assert.Equal("[MASKED]", sub.GetProperty("merchant").GetString());
    }

    [Fact]
    public async Task Build_ComputesMonthOverMonthDeltas()
    {
        await using var db = CreateDbContext();
        var userId = Guid.NewGuid();
        var foodId = Guid.NewGuid();

        var totals = new FakeTotals();
        totals.Set("2026-06", income: 8000m, expense: 5000m); // balance 3000
        totals.Set("2026-05", income: 7000m, expense: 5000m); // balance 2000

        var cat = new FakeCategorySpend();
        cat.Set("2026-06", new CategorySpend(foodId, "Alimentação", null, null, 1200m));
        cat.Set("2026-05", new CategorySpend(foodId, "Alimentação", null, null, 1000m));

        var f = await Build(db, totals, cat, new FakeBudgets(),
            new FakeTopMerchants([]), new PiiRedactor()).BuildAsync(userId, PriorMonthStart, CancellationToken.None);

        Assert.Equal(8000m, f.Income);
        Assert.Equal(5000m, f.Expense);
        Assert.Equal(3000m, f.Balance);

        using var doc = JsonDocument.Parse(f.Json);
        var t = doc.RootElement.GetProperty("totals");
        Assert.Equal(1000m, t.GetProperty("incomeMoM").GetDecimal());
        Assert.Equal(0m, t.GetProperty("expenseMoM").GetDecimal());
        Assert.Equal(1000m, t.GetProperty("balanceMoM").GetDecimal());

        var food = doc.RootElement.GetProperty("byCategory").EnumerateArray()
            .Single(c => c.GetProperty("category").GetString() == "Alimentação");
        Assert.Equal(1200m, food.GetProperty("amount").GetDecimal());
        Assert.Equal(1000m, food.GetProperty("previousAmount").GetDecimal());
        Assert.Equal(200m, food.GetProperty("delta").GetDecimal());
    }

    [Fact]
    public async Task Build_ProducesValidJsonWithAllFactGroups()
    {
        await using var db = CreateDbContext();
        var userId = Guid.NewGuid();
        var foodId = Guid.NewGuid();

        // Net worth: 10000 at end of May, 12000 at end of June → change +2000.
        db.NetWorthSnapshots.Add(Snapshot(userId, new DateTime(2026, 5, 31, 0, 0, 0, DateTimeKind.Utc), 10000m));
        db.NetWorthSnapshots.Add(Snapshot(userId, new DateTime(2026, 6, 30, 0, 0, 0, DateTimeKind.Utc), 12000m));
        db.RecurringTransactions.Add(Recurring(userId, "Netflix", 55m, isNew: false, increased: true, previous: 45m));
        await db.SaveChangesAsync();

        var totals = new FakeTotals();
        totals.Set("2026-06", income: 8000m, expense: 5000m);
        totals.Set("2026-05", income: 7000m, expense: 4500m);

        var cat = new FakeCategorySpend();
        cat.Set("2026-06", new CategorySpend(foodId, "Alimentação", null, null, 1200m));

        var budgets = new FakeBudgets(new BudgetsResult(2026, 6, false,
        [
            new BudgetRowResult(Guid.NewGuid(), foodId, "Alimentação", null, null,
                1000m, 1200m, 120m, -200m, 1200m, 120m, SimpleFinance.Application.Budgets.BudgetState.Exceeded, true),
        ]));

        var merchants = new FakeTopMerchants([new TopMerchantRow("Padaria", 300m, 8)]);

        var f = await Build(db, totals, cat, budgets, merchants, new PiiRedactor())
            .BuildAsync(userId, PriorMonthStart, CancellationToken.None);

        using var doc = JsonDocument.Parse(f.Json);
        var root = doc.RootElement;
        Assert.Equal("2026-06", root.GetProperty("period").GetString());
        Assert.True(root.TryGetProperty("totals", out _));
        Assert.True(root.TryGetProperty("byCategory", out _));
        Assert.True(root.TryGetProperty("budgets", out _));
        Assert.True(root.TryGetProperty("netWorth", out _));
        Assert.True(root.TryGetProperty("subscriptions", out _));
        Assert.True(root.TryGetProperty("topMerchants", out _));

        var nw = root.GetProperty("netWorth");
        Assert.Equal(12000m, nw.GetProperty("current").GetDecimal());
        Assert.Equal(10000m, nw.GetProperty("previous").GetDecimal());
        Assert.Equal(2000m, nw.GetProperty("change").GetDecimal());

        var budget = root.GetProperty("budgets").EnumerateArray().Single();
        Assert.Equal(1000m, budget.GetProperty("limit").GetDecimal());
        Assert.Equal(1200m, budget.GetProperty("spent").GetDecimal());

        var sub = root.GetProperty("subscriptions").EnumerateArray().Single();
        Assert.True(sub.GetProperty("amountIncreased").GetBoolean());

        // The package also exposes fallback fields for the degrade-safe summary.
        Assert.Equal(new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc), f.PeriodStart);
        Assert.Equal("2026-06", f.PeriodLabel);
        Assert.NotEmpty(f.TopCategories);
    }

    // ---- fakes ---------------------------------------------------------------------------------

    private sealed class FakeTotals : IPeriodTotalsReader
    {
        private readonly Dictionary<string, DashboardTotals> _byMonth = new();

        public void Set(string yyyyMM, decimal income, decimal expense) =>
            _byMonth[yyyyMM] = new DashboardTotals(0m, 0m, 0m, income, expense);

        public Task<DashboardTotals> GetTotalsAsync(Guid userId, DateTime periodStart, DateTime periodEnd, CancellationToken ct)
        {
            var key = periodStart.ToString("yyyy-MM");
            var t = _byMonth.TryGetValue(key, out var v) ? v : new DashboardTotals(0m, 0m, 0m, 0m, 0m);
            return Task.FromResult(t);
        }
    }

    private sealed class FakeCategorySpend : ICategorySpendReader
    {
        private readonly Dictionary<string, List<CategorySpend>> _byMonth = new();

        public void Set(string yyyyMM, params CategorySpend[] rows) => _byMonth[yyyyMM] = [.. rows];

        public Task<IReadOnlyList<CategorySpend>> GetCategorySpendAsync(Guid userId, DateTime periodStart, DateTime periodEnd, CancellationToken ct)
        {
            var key = periodStart.ToString("yyyy-MM");
            IReadOnlyList<CategorySpend> rows = _byMonth.TryGetValue(key, out var v) ? v : [];
            return Task.FromResult(rows);
        }
    }

    private sealed class FakeBudgets(BudgetsResult? result = null) : IBudgetReader
    {
        public Task<BudgetsResult> GetBudgetsForMonthAsync(Guid userId, int year, int month, CancellationToken ct) =>
            Task.FromResult(result ?? new BudgetsResult(year, month, false, []));
    }

    private sealed class FakeTopMerchants(IReadOnlyList<TopMerchantRow> rows) : ITopMerchantsReader
    {
        public Task<IReadOnlyList<TopMerchantRow>> GetTopMerchantsAsync(
            Guid userId, DateTime periodStart, DateTime periodEnd, int limit, CancellationToken ct) =>
            Task.FromResult(rows);
    }

    // Masks every input to a fixed sentinel — proves the builder routes each merchant string through Redact.
    private sealed class MaskRedactor : IPiiRedactor
    {
        public string Redact(string? input) => "[MASKED]";
    }
}
