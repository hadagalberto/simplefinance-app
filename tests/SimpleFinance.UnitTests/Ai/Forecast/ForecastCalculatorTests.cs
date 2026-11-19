using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SimpleFinance.Domain.Entities;
using SimpleFinance.Infrastructure.Ai.Forecast;
using SimpleFinance.Infrastructure.Persistence;
using SimpleFinance.Infrastructure.Reporting;

namespace SimpleFinance.UnitTests.Ai.Forecast;

/// <summary>
/// Behavior tests for <see cref="ForecastCalculator"/> (19-02, AI-FCT-01). Proves the deterministic
/// projection against FAKE reader bridges (realized totals + trailing history) and an InMemory recurring
/// set: the blend/floor is coherent, projectedBalance = income − expense, a recurring already posted this
/// month is NOT double-counted (only future-dated ones add to upcoming), income is never sourced from
/// recurring, the range straddles the projection with the sanity floor respected, ByCategoryJson is valid
/// JSON with the documented drivers, and identical inputs yield identical numbers.
/// </summary>
public class ForecastCalculatorTests
{
    // Day 10 of a 31-day month: daysElapsed=10, daysRemaining=21.
    private static readonly DateTime Reference = new(2026, 7, 10, 12, 0, 0, DateTimeKind.Utc);

    private static SimpleFinanceDbContext CreateDbContext() =>
        new(new DbContextOptionsBuilder<SimpleFinanceDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private static ForecastCalculator Build(
        SimpleFinanceDbContext db, IPeriodTotalsReader totals, ICategorySpendReader cat) =>
        new(totals, cat, db);

    private static RecurringTransaction Recurring(Guid userId, decimal amount, DateTime nextExpected, Guid? categoryId = null, bool active = true) => new()
    {
        Id = Guid.NewGuid(),
        UserId = userId,
        NormalizedMerchant = "SUB" + Guid.NewGuid().ToString("N")[..6],
        DisplayName = "Assinatura",
        AverageAmount = amount,
        NextExpectedDate = nextExpected,
        CategoryId = categoryId,
        IsActive = active,
        CreatedAt = DateTime.UtcNow,
        UpdatedAt = DateTime.UtcNow,
    };

    [Fact]
    public async Task Compute_BlendDominates_ProjectionIsCoherentAndBalanceIsIncomeMinusExpense()
    {
        await using var db = CreateDbContext();
        var userId = Guid.NewGuid();

        // Realized this month: income 8000, expense 2000. pace = 2000/10*31 = 6200.
        var totals = new FakePeriodTotalsReader();
        totals.Set("2026-07", income: 8000m, expense: 2000m);
        // 3 prior full months: expense 3000 each (avg3mo=3000), income 8000 each.
        totals.Set("2026-06", income: 8000m, expense: 3000m);
        totals.Set("2026-05", income: 8000m, expense: 3000m);
        totals.Set("2026-04", income: 8000m, expense: 3000m);

        // One future-dated recurring (07-20 > today 07-10): +500 to the floor only.
        db.RecurringTransactions.Add(Recurring(userId, 500m, new DateTime(2026, 7, 20, 0, 0, 0, DateTimeKind.Utc)));
        await db.SaveChangesAsync();

        var f = await Build(db, totals, new FakeCategorySpendReader()).ComputeAsync(userId, Reference, CancellationToken.None);

        // blend = 0.5*6200 + 0.5*3000 = 4600 ; floor = 2000+500 = 2500 ; projectedExpense = max = 4600.
        Assert.Equal(4600m, f.ProjectedExpense);
        // projectedIncome = max(realized 8000, avg3mo 8000) = 8000.
        Assert.Equal(8000m, f.ProjectedIncome);
        Assert.Equal(3400m, f.ProjectedBalance);
        Assert.Equal(f.ProjectedIncome - f.ProjectedExpense, f.ProjectedBalance);
        // σ over three equal 3000s = 0 → band = floorPct*projectedExpense = 0.10*4600 = 460.
        Assert.Equal(460m, f.BalanceHigh - f.ProjectedBalance);
        Assert.True(f.BalanceLow < f.ProjectedBalance && f.ProjectedBalance < f.BalanceHigh);
        Assert.Equal(new DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc), f.Month);
        Assert.Equal("recurring+3mo-avg", f.Method);
        Assert.Null(f.Narrative);
    }

    [Fact]
    public async Task Compute_RecurringAlreadyPostedThisMonth_NotDoubleCounted()
    {
        await using var db = CreateDbContext();
        var userId = Guid.NewGuid();
        var foodId = Guid.NewGuid();

        // Make the FLOOR dominate so upcomingRecurring is directly observable in projectedExpense.
        // Realized expense 1000, pace = 1000/10*31 = 3100 ; avg3mo = 100 ; blend = 0.5*3100+0.5*100 = 1600.
        var totals = new FakePeriodTotalsReader();
        totals.Set("2026-07", income: 5000m, expense: 1000m);
        totals.Set("2026-06", income: 5000m, expense: 100m);
        totals.Set("2026-05", income: 5000m, expense: 100m);
        totals.Set("2026-04", income: 5000m, expense: 100m);

        // Future-dated (07-20) recurring +2000 → floor = 1000+2000 = 3000 > blend 1600.
        db.RecurringTransactions.Add(Recurring(userId, 2000m, new DateTime(2026, 7, 20, 0, 0, 0, DateTimeKind.Utc), foodId));
        // Already-posted (07-05 < today 07-10) recurring +9999 → MUST be excluded (already in realized).
        db.RecurringTransactions.Add(Recurring(userId, 9999m, new DateTime(2026, 7, 5, 0, 0, 0, DateTimeKind.Utc), foodId));
        await db.SaveChangesAsync();

        var cat = new FakeCategorySpendReader();
        cat.Set("2026-07", new CategorySpend(foodId, "Alimentação", null, null, 1000m));

        var f = await Build(db, totals, cat).ComputeAsync(userId, Reference, CancellationToken.None);

        // floor = realized 1000 + upcoming 2000 (past 9999 excluded) = 3000, dominates blend 1600.
        Assert.Equal(3000m, f.ProjectedExpense);

        // Driver carries only the future recurring in recurringUpcoming.
        var driver = Drivers(f).Single(d => d.GetProperty("category").GetString() == "Alimentação");
        Assert.Equal(2000m, driver.GetProperty("recurringUpcoming").GetDecimal());
    }

    [Fact]
    public async Task Compute_IncomeNeverSourcedFromRecurring_UsesMaxOfRealizedAndHistory()
    {
        await using var db = CreateDbContext();
        var userId = Guid.NewGuid();

        // Paycheck not yet landed: realized income 0, history 7000 → projectedIncome = 7000.
        var totals = new FakePeriodTotalsReader();
        totals.Set("2026-07", income: 0m, expense: 500m);
        totals.Set("2026-06", income: 7000m, expense: 500m);
        totals.Set("2026-05", income: 7000m, expense: 500m);
        totals.Set("2026-04", income: 7000m, expense: 500m);

        // A big recurring DEBIT must never raise income.
        db.RecurringTransactions.Add(Recurring(userId, 3000m, new DateTime(2026, 7, 20, 0, 0, 0, DateTimeKind.Utc)));
        await db.SaveChangesAsync();

        var f = await Build(db, totals, new FakeCategorySpendReader()).ComputeAsync(userId, Reference, CancellationToken.None);

        Assert.Equal(7000m, f.ProjectedIncome);
    }

    [Fact]
    public async Task Compute_IsDeterministic_IdenticalInputsYieldIdenticalNumbers()
    {
        await using var db = CreateDbContext();
        var userId = Guid.NewGuid();
        var foodId = Guid.NewGuid();

        var totals = new FakePeriodTotalsReader();
        totals.Set("2026-07", income: 8000m, expense: 2100m);
        totals.Set("2026-06", income: 7900m, expense: 2600m);
        totals.Set("2026-05", income: 8100m, expense: 3100m);
        totals.Set("2026-04", income: 8000m, expense: 2800m);
        db.RecurringTransactions.Add(Recurring(userId, 350m, new DateTime(2026, 7, 25, 0, 0, 0, DateTimeKind.Utc), foodId));
        await db.SaveChangesAsync();

        var cat = new FakeCategorySpendReader();
        cat.Set("2026-07", new CategorySpend(foodId, "Alimentação", null, null, 1200m));
        cat.Set("2026-06", new CategorySpend(foodId, "Alimentação", null, null, 1300m));

        var a = await Build(db, totals, cat).ComputeAsync(userId, Reference, CancellationToken.None);
        var b = await Build(db, totals, cat).ComputeAsync(userId, Reference, CancellationToken.None);

        Assert.Equal(a.ProjectedIncome, b.ProjectedIncome);
        Assert.Equal(a.ProjectedExpense, b.ProjectedExpense);
        Assert.Equal(a.ProjectedBalance, b.ProjectedBalance);
        Assert.Equal(a.BalanceLow, b.BalanceLow);
        Assert.Equal(a.BalanceHigh, b.BalanceHigh);
        Assert.Equal(a.ByCategoryJson, b.ByCategoryJson);
    }

    [Fact]
    public async Task Compute_ByCategoryJson_IsValidJsonWithMethodTotalsAndDrivers()
    {
        await using var db = CreateDbContext();
        var userId = Guid.NewGuid();
        var foodId = Guid.NewGuid();

        var totals = new FakePeriodTotalsReader();
        totals.Set("2026-07", income: 8000m, expense: 2000m);
        totals.Set("2026-06", income: 8000m, expense: 3000m);
        totals.Set("2026-05", income: 8000m, expense: 3000m);
        totals.Set("2026-04", income: 8000m, expense: 3000m);
        db.RecurringTransactions.Add(Recurring(userId, 500m, new DateTime(2026, 7, 20, 0, 0, 0, DateTimeKind.Utc), foodId));
        await db.SaveChangesAsync();

        var cat = new FakeCategorySpendReader();
        cat.Set("2026-07", new CategorySpend(foodId, "Alimentação", null, null, 820m), new CategorySpend(null, null, null, null, 210m));
        cat.Set("2026-06", new CategorySpend(foodId, "Alimentação", null, null, 1300m));

        var f = await Build(db, totals, cat).ComputeAsync(userId, Reference, CancellationToken.None);

        using var doc = JsonDocument.Parse(f.ByCategoryJson);
        var root = doc.RootElement;
        Assert.Equal("recurring+3mo-avg", root.GetProperty("method").GetString());
        Assert.Equal(10, root.GetProperty("reference").GetProperty("daysElapsed").GetInt32());
        Assert.Equal(31, root.GetProperty("reference").GetProperty("daysInMonth").GetInt32());

        var totalsEl = root.GetProperty("totals");
        Assert.Equal(f.ProjectedExpense, totalsEl.GetProperty("projectedExpense").GetDecimal());
        Assert.Equal(f.ProjectedBalance, totalsEl.GetProperty("projectedBalance").GetDecimal());
        Assert.Equal(f.BalanceLow, totalsEl.GetProperty("balanceLow").GetDecimal());
        Assert.Equal(f.BalanceHigh, totalsEl.GetProperty("balanceHigh").GetDecimal());

        var drivers = Drivers(f);
        Assert.NotEmpty(drivers);
        Assert.True(drivers.Count <= 5);
        var food = drivers.Single(d => d.GetProperty("category").GetString() == "Alimentação");
        foreach (var prop in new[] { "realized", "baseline3mo", "recurringUpcoming", "projected", "shareOfExpense" })
        {
            Assert.True(food.TryGetProperty(prop, out _), $"driver missing {prop}");
        }
        // Uncategorized spend surfaces as a "Sem categoria" driver.
        Assert.Contains(drivers, d => d.GetProperty("category").GetString() == "Sem categoria");
    }

    private static List<JsonElement> Drivers(Domain.Entities.Forecast f)
    {
        using var doc = JsonDocument.Parse(f.ByCategoryJson);
        return doc.RootElement.GetProperty("drivers").EnumerateArray().Select(e => e.Clone()).ToList();
    }

    // ---- fakes ---------------------------------------------------------------------------------

    private sealed class FakePeriodTotalsReader : IPeriodTotalsReader
    {
        private readonly Dictionary<string, DashboardTotals> _byMonth = new();

        public void Set(string yyyyMM, decimal income, decimal expense) =>
            _byMonth[yyyyMM] = new DashboardTotals(0m, 0m, 0m, income, expense);

        public Task<DashboardTotals> GetTotalsAsync(Guid userId, DateTime periodStart, DateTime periodEnd, CancellationToken ct)
        {
            var key = periodStart.ToString("yyyy-MM");
            var totals = _byMonth.TryGetValue(key, out var t) ? t : new DashboardTotals(0m, 0m, 0m, 0m, 0m);
            return Task.FromResult(totals);
        }
    }

    private sealed class FakeCategorySpendReader : ICategorySpendReader
    {
        private readonly Dictionary<string, List<CategorySpend>> _byMonth = new();

        public void Set(string yyyyMM, params CategorySpend[] rows) => _byMonth[yyyyMM] = [.. rows];

        public Task<IReadOnlyList<CategorySpend>> GetCategorySpendAsync(Guid userId, DateTime periodStart, DateTime periodEnd, CancellationToken ct)
        {
            var key = periodStart.ToString("yyyy-MM");
            IReadOnlyList<CategorySpend> rows = _byMonth.TryGetValue(key, out var list) ? list : [];
            return Task.FromResult(rows);
        }
    }
}
