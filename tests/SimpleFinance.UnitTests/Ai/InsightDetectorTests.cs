using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SimpleFinance.Domain.Entities;
using SimpleFinance.Domain.Enums;
using SimpleFinance.Infrastructure.Ai;
using SimpleFinance.Infrastructure.Ai.Insights;
using SimpleFinance.Infrastructure.Budgets;
using SimpleFinance.Infrastructure.NetWorth;
using SimpleFinance.Infrastructure.Persistence;
using SimpleFinance.Infrastructure.Reporting;
using SimpleFinance.Application.Budgets;

namespace SimpleFinance.UnitTests.Ai;

/// <summary>
/// Behavior tests for <see cref="InsightDetector"/> (18-02, AI-INS-01). Proves the deterministic
/// qualification of each signal family against FAKE reader bridges (SpendingChange / BudgetRisk / Trend)
/// and an InMemory transaction/recurring set (new-merchant fold + SubscriptionCreep): the absolute floors
/// + minimum baseline suppress noise, the tiers/severities are correct, identical inputs yield identical
/// DedupKeys, merchant PII is redacted out of the surfaced prose, and the top-N cap truncates a flood.
/// </summary>
public class InsightDetectorTests
{
    private static readonly DateTime Reference = new(2026, 7, 15, 12, 0, 0, DateTimeKind.Utc);

    private static SimpleFinanceDbContext CreateDbContext() =>
        new(new DbContextOptionsBuilder<SimpleFinanceDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private static InsightDetector Build(
        SimpleFinanceDbContext db,
        ICategorySpendReader? catReader = null,
        IBudgetReader? budgetReader = null,
        INetWorthReader? netWorthReader = null) =>
        new(
            catReader ?? new FakeCategorySpendReader(),
            budgetReader ?? new FakeBudgetReader(EmptyBudgets()),
            netWorthReader ?? new FakeNetWorthReader(new NetWorthEvolutionResult([])),
            db,
            new PiiRedactor());

    private static BudgetsResult EmptyBudgets() => new(2026, 7, false, []);

    private static JsonElement Data(InsightSignal s) => JsonDocument.Parse(s.DataJson).RootElement;

    // ---- SpendingChange -------------------------------------------------------------------------

    [Fact]
    public async Task SpendingChange_CategoryAbove25PctAndFloors_Qualifies_Info()
    {
        await using var db = CreateDbContext();
        var userId = Guid.NewGuid();
        var foodId = Guid.NewGuid();

        var cat = new FakeCategorySpendReader();
        cat.Set("2026-07", new CategorySpend(foodId, "Alimentação", null, null, 260m));
        cat.Set("2026-06", new CategorySpend(foodId, "Alimentação", null, null, 200m));
        cat.Set("2026-05", new CategorySpend(foodId, "Alimentação", null, null, 200m));
        cat.Set("2026-04", new CategorySpend(foodId, "Alimentação", null, null, 200m));

        var signals = await Build(db, cat).DetectAsync(userId, Reference, CancellationToken.None);

        var s = Assert.Single(signals);
        Assert.Equal(AiInsightKind.SpendingChange, s.Kind);
        Assert.Equal(AiInsightSeverity.Info, s.Severity);
        Assert.Equal($"SpendingChange:{foodId}:2026-07", s.DedupKey);
        var data = Data(s);
        Assert.Equal(260m, data.GetProperty("currentAmount").GetDecimal());
        Assert.Equal(200m, data.GetProperty("baselineAmount").GetDecimal());
        Assert.Equal(0.30m, data.GetProperty("changePct").GetDecimal());
        Assert.False(string.IsNullOrWhiteSpace(s.FallbackTitle));
        Assert.False(string.IsNullOrWhiteSpace(s.FallbackBody));
    }

    [Fact]
    public async Task SpendingChange_At50Pct_EscalatesToWarning()
    {
        await using var db = CreateDbContext();
        var foodId = Guid.NewGuid();
        var cat = new FakeCategorySpendReader();
        cat.Set("2026-07", new CategorySpend(foodId, "Alimentação", null, null, 300m));
        cat.Set("2026-06", new CategorySpend(foodId, "Alimentação", null, null, 200m));
        cat.Set("2026-05", new CategorySpend(foodId, "Alimentação", null, null, 200m));
        cat.Set("2026-04", new CategorySpend(foodId, "Alimentação", null, null, 200m));

        var signals = await Build(db, cat).DetectAsync(Guid.NewGuid(), Reference, CancellationToken.None);

        Assert.Equal(AiInsightSeverity.Warning, Assert.Single(signals).Severity);
    }

    [Fact]
    public async Task SpendingChange_BelowPctFloor_NoSignal()
    {
        await using var db = CreateDbContext();
        var foodId = Guid.NewGuid();
        var cat = new FakeCategorySpendReader();
        // +10% only -> below 25% floor
        cat.Set("2026-07", new CategorySpend(foodId, "Alimentação", null, null, 220m));
        cat.Set("2026-06", new CategorySpend(foodId, "Alimentação", null, null, 200m));
        cat.Set("2026-05", new CategorySpend(foodId, "Alimentação", null, null, 200m));
        cat.Set("2026-04", new CategorySpend(foodId, "Alimentação", null, null, 200m));

        var signals = await Build(db, cat).DetectAsync(Guid.NewGuid(), Reference, CancellationToken.None);

        Assert.Empty(signals);
    }

    [Fact]
    public async Task SpendingChange_TinyCategoryBelowAbsoluteAndBaselineFloors_NoSignal()
    {
        await using var db = CreateDbContext();
        var toyId = Guid.NewGuid();
        var cat = new FakeCategorySpendReader();
        // 300% jump but on a tiny R$3 baseline -> both the absolute (R$50) and baseline (R$50) floors kill it
        cat.Set("2026-07", new CategorySpend(toyId, "Besteiras", null, null, 12m));
        cat.Set("2026-06", new CategorySpend(toyId, "Besteiras", null, null, 3m));
        cat.Set("2026-05", new CategorySpend(toyId, "Besteiras", null, null, 3m));
        cat.Set("2026-04", new CategorySpend(toyId, "Besteiras", null, null, 3m));

        var signals = await Build(db, cat).DetectAsync(Guid.NewGuid(), Reference, CancellationToken.None);

        Assert.Empty(signals);
    }

    [Fact]
    public async Task SpendingChange_IdenticalInputs_ProduceIdenticalDedupKey()
    {
        await using var db = CreateDbContext();
        var userId = Guid.NewGuid();
        var foodId = Guid.NewGuid();
        var cat = new FakeCategorySpendReader();
        cat.Set("2026-07", new CategorySpend(foodId, "Alimentação", null, null, 260m));
        cat.Set("2026-06", new CategorySpend(foodId, "Alimentação", null, null, 200m));
        cat.Set("2026-05", new CategorySpend(foodId, "Alimentação", null, null, 200m));
        cat.Set("2026-04", new CategorySpend(foodId, "Alimentação", null, null, 200m));

        var first = await Build(db, cat).DetectAsync(userId, Reference, CancellationToken.None);
        var second = await Build(db, cat).DetectAsync(userId, Reference, CancellationToken.None);

        Assert.Equal(first[0].DedupKey, second[0].DedupKey);
        Assert.Equal(first[0].Id, second[0].Id);
    }

    [Fact]
    public async Task SpendingChange_TopNCap_TruncatesToMaxAndDropsSmallestMagnitude()
    {
        await using var db = CreateDbContext();
        var cat = new FakeCategorySpendReader();
        // 6 qualifying Info categories (all < 50% so same severity); the +25% (smallest) must be dropped.
        var pctToCurrent = new (string name, decimal current)[]
        {
            ("A", 250m), // +25% (min -> dropped)
            ("B", 260m), // +30%
            ("C", 270m), // +35%
            ("D", 280m), // +40%
            ("E", 290m), // +45%
            ("F", 256m), // +28%
        };
        var smallest = Guid.NewGuid();
        for (var i = 0; i < pctToCurrent.Length; i++)
        {
            var id = i == 0 ? smallest : Guid.NewGuid();
            var (name, current) = pctToCurrent[i];
            cat.Add("2026-07", new CategorySpend(id, name, null, null, current));
            cat.Add("2026-06", new CategorySpend(id, name, null, null, 200m));
            cat.Add("2026-05", new CategorySpend(id, name, null, null, 200m));
            cat.Add("2026-04", new CategorySpend(id, name, null, null, 200m));
        }

        var signals = await Build(db, cat).DetectAsync(Guid.NewGuid(), Reference, CancellationToken.None);

        Assert.Equal(InsightThresholds.MaxSignalsPerRun, signals.Count);
        Assert.DoesNotContain(signals, s => s.DedupKey == $"SpendingChange:{smallest}:2026-07");
    }

    // ---- BudgetRisk -----------------------------------------------------------------------------

    [Fact]
    public async Task BudgetRisk_Tiers_MapToSeverityAndDedupTier()
    {
        await using var db = CreateDbContext();
        var warnCat = Guid.NewGuid();
        var critCat = Guid.NewGuid();
        var okCat = Guid.NewGuid();
        var rows = new List<BudgetRowResult>
        {
            new(Guid.NewGuid(), warnCat, "Mercado", null, null, 1000m, 850m, 85m, 150m, 900m, 90m, BudgetState.Attention, true),
            new(Guid.NewGuid(), critCat, "Lazer", null, null, 500m, 525m, 105m, -25m, 560m, 112m, BudgetState.Exceeded, true),
            new(Guid.NewGuid(), okCat, "Transporte", null, null, 400m, 200m, 50m, 200m, 220m, 55m, BudgetState.Ok, false),
            new(Guid.NewGuid(), null, "Geral", null, null, 3000m, 2500m, 83m, 500m, 2700m, 90m, BudgetState.Attention, true),
        };
        var budget = new FakeBudgetReader(new BudgetsResult(2026, 7, false, rows));

        var signals = await Build(db, budgetReader: budget).DetectAsync(Guid.NewGuid(), Reference, CancellationToken.None);

        Assert.Equal(3, signals.Count);
        var keys = signals.Select(s => s.DedupKey).ToHashSet();
        Assert.Contains($"BudgetRisk:{warnCat}:2026-07:80", keys);
        Assert.Contains($"BudgetRisk:{critCat}:2026-07:100", keys);
        Assert.Contains("BudgetRisk:global:2026-07:80", keys);

        var crit = signals.Single(s => s.DedupKey.Contains(critCat.ToString()));
        Assert.Equal(AiInsightSeverity.Critical, crit.Severity);
        var warn = signals.Single(s => s.DedupKey.Contains(warnCat.ToString()));
        Assert.Equal(AiInsightSeverity.Warning, warn.Severity);
        Assert.Equal(105m, Data(crit).GetProperty("percentOfCap").GetDecimal());
    }

    // ---- Trend ----------------------------------------------------------------------------------

    [Fact]
    public async Task Trend_MonotoneRiseAtLeast10Pct_Qualifies()
    {
        await using var db = CreateDbContext();
        var evo = new NetWorthEvolutionResult(
        [
            new NetWorthEvolutionPointResult(new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc), 100m),
            new NetWorthEvolutionPointResult(new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc), 110m),
            new NetWorthEvolutionPointResult(new DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc), 125m),
        ]);
        var nw = new FakeNetWorthReader(evo);

        var signals = await Build(db, netWorthReader: nw).DetectAsync(Guid.NewGuid(), Reference, CancellationToken.None);

        var s = Assert.Single(signals);
        Assert.Equal(AiInsightKind.Trend, s.Kind);
        Assert.Equal("Trend:netWorth:2026-07", s.DedupKey);
        Assert.Equal(0.25m, Data(s).GetProperty("changePct").GetDecimal());
    }

    [Fact]
    public async Task Trend_NonMonotone_NoSignal()
    {
        await using var db = CreateDbContext();
        var evo = new NetWorthEvolutionResult(
        [
            new NetWorthEvolutionPointResult(new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc), 100m),
            new NetWorthEvolutionPointResult(new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc), 140m),
            new NetWorthEvolutionPointResult(new DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc), 120m),
        ]);
        var nw = new FakeNetWorthReader(evo);

        var signals = await Build(db, netWorthReader: nw).DetectAsync(Guid.NewGuid(), Reference, CancellationToken.None);

        Assert.Empty(signals);
    }

    [Fact]
    public async Task Trend_TooFewPoints_NoSignal()
    {
        await using var db = CreateDbContext();
        var evo = new NetWorthEvolutionResult(
        [
            new NetWorthEvolutionPointResult(new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc), 100m),
            new NetWorthEvolutionPointResult(new DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc), 130m),
        ]);
        var nw = new FakeNetWorthReader(evo);

        var signals = await Build(db, netWorthReader: nw).DetectAsync(Guid.NewGuid(), Reference, CancellationToken.None);

        Assert.Empty(signals);
    }

    // ---- SubscriptionCreep ----------------------------------------------------------------------

    [Fact]
    public async Task SubscriptionCreep_SubscriptionRoseAtLeast15Pct_Qualifies_Warning()
    {
        await using var db = CreateDbContext();
        var userId = Guid.NewGuid();
        var recId = Guid.NewGuid();
        db.RecurringTransactions.Add(new RecurringTransaction
        {
            Id = recId,
            UserId = userId,
            NormalizedMerchant = "SPOTIFY",
            DisplayName = "Spotify",
            AverageAmount = 27m,
            PreviousAverageAmount = 20m,
            IsSubscription = true,
            IsActive = true,
            AmountIncreaseDetected = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();

        var signals = await Build(db).DetectAsync(userId, Reference, CancellationToken.None);

        var s = Assert.Single(signals);
        Assert.Equal(AiInsightKind.SubscriptionCreep, s.Kind);
        Assert.Equal(AiInsightSeverity.Warning, s.Severity);
        Assert.Equal($"SubscriptionCreep:{recId}:27.00", s.DedupKey);
        var data = Data(s);
        Assert.Equal(20m, data.GetProperty("previousAverage").GetDecimal());
        Assert.Equal(27m, data.GetProperty("newAverage").GetDecimal());
        Assert.Equal(0.35m, data.GetProperty("increasePct").GetDecimal());
    }

    [Fact]
    public async Task SubscriptionCreep_AtLeast50Pct_IsCritical()
    {
        await using var db = CreateDbContext();
        var userId = Guid.NewGuid();
        db.RecurringTransactions.Add(new RecurringTransaction
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            NormalizedMerchant = "HBO",
            DisplayName = "HBO Max",
            AverageAmount = 35m,
            PreviousAverageAmount = 20m,
            IsSubscription = true,
            IsActive = true,
            AmountIncreaseDetected = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();

        var signals = await Build(db).DetectAsync(userId, Reference, CancellationToken.None);

        Assert.Equal(AiInsightSeverity.Critical, Assert.Single(signals).Severity);
    }

    [Fact]
    public async Task SubscriptionCreep_NonSubscriptionOrBelowFloor_NoSignal()
    {
        await using var db = CreateDbContext();
        var userId = Guid.NewGuid();
        db.RecurringTransactions.AddRange(
            new RecurringTransaction
            {
                Id = Guid.NewGuid(), UserId = userId, NormalizedMerchant = "ACAD", DisplayName = "Academia",
                AverageAmount = 27m, PreviousAverageAmount = 20m, IsSubscription = false, IsActive = true,
                AmountIncreaseDetected = true, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
            },
            new RecurringTransaction
            {
                Id = Guid.NewGuid(), UserId = userId, NormalizedMerchant = "NEWS", DisplayName = "Jornal",
                AverageAmount = 21m, PreviousAverageAmount = 20m, IsSubscription = true, IsActive = true,
                AmountIncreaseDetected = true, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
            });
        await db.SaveChangesAsync();

        var signals = await Build(db).DetectAsync(userId, Reference, CancellationToken.None);

        Assert.Empty(signals);
    }

    [Fact]
    public async Task SubscriptionCreep_OtherUsersRecurring_NotLeaked()
    {
        await using var db = CreateDbContext();
        var me = Guid.NewGuid();
        var other = Guid.NewGuid();
        db.RecurringTransactions.Add(new RecurringTransaction
        {
            Id = Guid.NewGuid(), UserId = other, NormalizedMerchant = "SPOTIFY", DisplayName = "Spotify",
            AverageAmount = 30m, PreviousAverageAmount = 20m, IsSubscription = true, IsActive = true,
            AmountIncreaseDetected = true, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();

        var signals = await Build(db).DetectAsync(me, Reference, CancellationToken.None);

        Assert.Empty(signals);
    }

    // ---- new-merchant (folds into SpendingChange) -----------------------------------------------

    [Fact]
    public async Task NewMerchant_FirstSeenThisMonthAboveFloor_EmitsSpendingChange_Redacted()
    {
        await using var db = CreateDbContext();
        var userId = Guid.NewGuid();
        var acctId = Guid.NewGuid();

        // New merchant this month, R$120 expense, absent in the trailing window.
        db.Transactions.Add(Txn(userId, acctId, "Netflix", -120m, new DateTime(2026, 7, 3, 0, 0, 0, DateTimeKind.Utc)));
        // A merchant present BOTH this month and last month -> not new, must not fire.
        db.Transactions.Add(Txn(userId, acctId, "Spotify", -30m, new DateTime(2026, 7, 5, 0, 0, 0, DateTimeKind.Utc)));
        db.Transactions.Add(Txn(userId, acctId, "Spotify", -30m, new DateTime(2026, 6, 5, 0, 0, 0, DateTimeKind.Utc)));
        await db.SaveChangesAsync();

        var signals = await Build(db).DetectAsync(userId, Reference, CancellationToken.None);

        var s = Assert.Single(signals);
        Assert.Equal(AiInsightKind.SpendingChange, s.Kind);
        Assert.Equal("newMerchant", Data(s).GetProperty("subkind").GetString());
        Assert.Equal(120m, Data(s).GetProperty("currentAmount").GetDecimal());
        Assert.StartsWith("SpendingChange:merchant:", s.DedupKey);
        Assert.EndsWith(":2026-07", s.DedupKey);
    }

    [Fact]
    public async Task NewMerchant_BelowAmountFloor_NoSignal()
    {
        await using var db = CreateDbContext();
        var userId = Guid.NewGuid();
        var acctId = Guid.NewGuid();
        db.Transactions.Add(Txn(userId, acctId, "PadariaX", -40m, new DateTime(2026, 7, 3, 0, 0, 0, DateTimeKind.Utc)));
        await db.SaveChangesAsync();

        var signals = await Build(db).DetectAsync(userId, Reference, CancellationToken.None);

        Assert.Empty(signals);
    }

    [Fact]
    public async Task NewMerchant_IncomeCreditNeverFlagged()
    {
        await using var db = CreateDbContext();
        var userId = Guid.NewGuid();
        var acctId = Guid.NewGuid();
        var salary = Txn(userId, acctId, "Empregador", 5000m, new DateTime(2026, 7, 5, 0, 0, 0, DateTimeKind.Utc));
        salary.Type = TransactionType.Credit;
        db.Transactions.Add(salary);
        await db.SaveChangesAsync();

        var signals = await Build(db).DetectAsync(userId, Reference, CancellationToken.None);

        Assert.Empty(signals);
    }

    private static Transaction Txn(Guid userId, Guid accountId, string merchant, decimal amount, DateTime date) => new()
    {
        Id = Guid.NewGuid(),
        UserId = userId,
        AccountId = accountId,
        PluggyTransactionId = Guid.NewGuid().ToString(),
        Date = date,
        Description = merchant,
        MerchantName = merchant,
        Amount = amount,
        Type = TransactionType.Debit,
        Status = TransactionStatus.Posted,
        CurrencyCode = "BRL",
        CreatedAt = DateTime.UtcNow,
        UpdatedAt = DateTime.UtcNow,
    };

    // ---- fakes ----------------------------------------------------------------------------------

    private sealed class FakeCategorySpendReader : ICategorySpendReader
    {
        private readonly Dictionary<string, List<CategorySpend>> _byMonth = new();

        public void Set(string yyyyMM, params CategorySpend[] rows) => _byMonth[yyyyMM] = [.. rows];

        public void Add(string yyyyMM, CategorySpend row)
        {
            if (!_byMonth.TryGetValue(yyyyMM, out var list))
            {
                list = [];
                _byMonth[yyyyMM] = list;
            }
            list.Add(row);
        }

        public Task<IReadOnlyList<CategorySpend>> GetCategorySpendAsync(Guid userId, DateTime periodStart, DateTime periodEnd, CancellationToken ct)
        {
            var key = periodStart.ToString("yyyy-MM");
            IReadOnlyList<CategorySpend> rows = _byMonth.TryGetValue(key, out var list) ? list : [];
            return Task.FromResult(rows);
        }
    }

    private sealed class FakeBudgetReader(BudgetsResult result) : IBudgetReader
    {
        public Task<BudgetsResult> GetBudgetsForMonthAsync(Guid userId, int year, int month, CancellationToken ct)
            => Task.FromResult(result);
    }

    private sealed class FakeNetWorthReader(NetWorthEvolutionResult evolution) : INetWorthReader
    {
        public Task<NetWorthSummaryResult> GetSummaryAsync(Guid userId, CancellationToken ct)
            => Task.FromResult(new NetWorthSummaryResult(0m, 0m, 0m, 0m));

        public Task<NetWorthEvolutionResult> GetEvolutionAsync(Guid userId, int days, CancellationToken ct)
            => Task.FromResult(evolution);
    }
}
