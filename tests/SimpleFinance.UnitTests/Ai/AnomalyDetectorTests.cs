using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SimpleFinance.Domain.Entities;
using SimpleFinance.Domain.Enums;
using SimpleFinance.Infrastructure.Ai;
using SimpleFinance.Infrastructure.Ai.Insights;
using SimpleFinance.Infrastructure.Persistence;
using SimpleFinance.Infrastructure.Reporting;

namespace SimpleFinance.UnitTests.Ai;

/// <summary>
/// Behavior tests for <see cref="AnomalyDetector"/> (18-04, AI-INS-02). Proves the four anomaly
/// families over an InMemory transaction/recurring set + a fake <see cref="ICategorySpendReader"/>:
/// a per-merchant robust z-score outlier is flagged (Critical) while a small sample and a below-floor
/// duplicate are not, income CREDITs are never flagged (Debit-only), the RecurringTransaction allowlist
/// excludes legitimate recurring merchants from z-score/duplicate, a duplicate charge fires exactly once
/// on the LATER transaction, a subscription 20→35 increase yields a "75%" anomaly, and only a LARGE
/// category jump (≥50% AND ≥R$200) fires. Every signal carries Kind=Anomaly + a per-occurrence DedupKey.
/// </summary>
public class AnomalyDetectorTests
{
    private static readonly DateTime Reference = new(2026, 7, 15, 12, 0, 0, DateTimeKind.Utc);

    private static SimpleFinanceDbContext CreateDbContext() =>
        new(new DbContextOptionsBuilder<SimpleFinanceDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private static AnomalyDetector Build(SimpleFinanceDbContext db, ICategorySpendReader? catReader = null) =>
        new(db, catReader ?? new FakeCategorySpendReader(), new PiiRedactor());

    private static JsonElement Data(InsightSignal s) => JsonDocument.Parse(s.DataJson).RootElement;

    // ---- per-merchant modified z-score ----------------------------------------------------------

    [Fact]
    public async Task ZScore_ClearOutlierAgainstStableHistory_FlaggedCritical()
    {
        await using var db = CreateDbContext();
        var userId = Guid.NewGuid();
        var acct = Guid.NewGuid();

        // 6 stable ~R$100 charges + 1 R$400 outlier (all Debit, distinct amounts → no duplicates).
        var normals = new[] { 100m, 105m, 98m, 102m, 110m, 99m };
        for (var i = 0; i < normals.Length; i++)
        {
            db.Transactions.Add(Txn(userId, acct, "Mercado", -normals[i], new DateTime(2026, i + 1, 5, 0, 0, 0, DateTimeKind.Utc)));
        }
        var outlier = Txn(userId, acct, "Mercado", -400m, new DateTime(2026, 7, 5, 0, 0, 0, DateTimeKind.Utc));
        db.Transactions.Add(outlier);
        await db.SaveChangesAsync();

        var signals = await Build(db).DetectAsync(userId, Reference, CancellationToken.None);

        var s = Assert.Single(signals);
        Assert.Equal(AiInsightKind.Anomaly, s.Kind);
        Assert.Equal(AiInsightSeverity.Critical, s.Severity);
        Assert.Equal($"Anomaly:zscore:{outlier.Id}", s.DedupKey);
        Assert.Equal(400m, Data(s).GetProperty("amount").GetDecimal());
    }

    [Fact]
    public async Task ZScore_InsufficientHistory_NoSignal()
    {
        await using var db = CreateDbContext();
        var userId = Guid.NewGuid();
        var acct = Guid.NewGuid();

        // Only 4 charges for the merchant → the big one has < 6 history → skipped.
        db.Transactions.Add(Txn(userId, acct, "LojaNova", -100m, new DateTime(2026, 4, 5, 0, 0, 0, DateTimeKind.Utc)));
        db.Transactions.Add(Txn(userId, acct, "LojaNova", -105m, new DateTime(2026, 5, 5, 0, 0, 0, DateTimeKind.Utc)));
        db.Transactions.Add(Txn(userId, acct, "LojaNova", -98m, new DateTime(2026, 6, 5, 0, 0, 0, DateTimeKind.Utc)));
        db.Transactions.Add(Txn(userId, acct, "LojaNova", -400m, new DateTime(2026, 7, 5, 0, 0, 0, DateTimeKind.Utc)));
        await db.SaveChangesAsync();

        var signals = await Build(db).DetectAsync(userId, Reference, CancellationToken.None);

        Assert.Empty(signals);
    }

    [Fact]
    public async Task ZScore_ConstantBaselineMadZero_JumpAboveHalf_Flagged()
    {
        await using var db = CreateDbContext();
        var userId = Guid.NewGuid();
        var acct = Guid.NewGuid();

        // 6 identical R$50 charges spaced monthly (no duplicate window overlap) + 1 R$80 jump.
        for (var i = 0; i < 6; i++)
        {
            db.Transactions.Add(Txn(userId, acct, "AppX", -50m, new DateTime(2026, i + 1, 5, 0, 0, 0, DateTimeKind.Utc)));
        }
        var jump = Txn(userId, acct, "AppX", -80m, new DateTime(2026, 7, 5, 0, 0, 0, DateTimeKind.Utc));
        db.Transactions.Add(jump);
        await db.SaveChangesAsync();

        var signals = await Build(db).DetectAsync(userId, Reference, CancellationToken.None);

        var s = Assert.Single(signals);
        Assert.Equal(AiInsightKind.Anomaly, s.Kind);
        Assert.Equal($"Anomaly:zscore:{jump.Id}", s.DedupKey);
    }

    [Fact]
    public async Task IncomeCredit_NeverFlagged()
    {
        await using var db = CreateDbContext();
        var userId = Guid.NewGuid();
        var acct = Guid.NewGuid();

        // A large recurring salary CREDIT — Debit-only scope must exclude it entirely.
        for (var i = 0; i < 7; i++)
        {
            var salary = Txn(userId, acct, "Empregador", 5000m, new DateTime(2026, i + 1, 5, 0, 0, 0, DateTimeKind.Utc));
            salary.Type = TransactionType.Credit;
            db.Transactions.Add(salary);
        }
        await db.SaveChangesAsync();

        var signals = await Build(db).DetectAsync(userId, Reference, CancellationToken.None);

        Assert.Empty(signals);
    }

    [Fact]
    public async Task RecurringAllowlist_MerchantExcludedFromZScore()
    {
        await using var db = CreateDbContext();
        var userId = Guid.NewGuid();
        var acct = Guid.NewGuid();

        // Same shape as the z-score outlier test, but the merchant is on the RecurringTransaction
        // allowlist (matched by NormalizedMerchant) → excluded from z-score/duplicate detection.
        db.RecurringTransactions.Add(new RecurringTransaction
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            NormalizedMerchant = "SPOTIFY",
            DisplayName = "Spotify",
            AverageAmount = 100m,
            IsActive = true,
            AmountIncreaseDetected = false,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        var normals = new[] { 100m, 105m, 98m, 102m, 110m, 99m };
        for (var i = 0; i < normals.Length; i++)
        {
            db.Transactions.Add(Txn(userId, acct, "Spotify", -normals[i], new DateTime(2026, i + 1, 5, 0, 0, 0, DateTimeKind.Utc)));
        }
        db.Transactions.Add(Txn(userId, acct, "Spotify", -400m, new DateTime(2026, 7, 5, 0, 0, 0, DateTimeKind.Utc)));
        await db.SaveChangesAsync();

        var signals = await Build(db).DetectAsync(userId, Reference, CancellationToken.None);

        Assert.Empty(signals);
    }

    // ---- duplicate charge -----------------------------------------------------------------------

    [Fact]
    public async Task Duplicate_SameMerchantAmountWithinWindow_FlaggedOnceOnLater()
    {
        await using var db = CreateDbContext();
        var userId = Guid.NewGuid();
        var acct = Guid.NewGuid();

        var first = Txn(userId, acct, "Loja", -150m, new DateTime(2026, 5, 10, 10, 0, 0, DateTimeKind.Utc));
        var later = Txn(userId, acct, "Loja", -150m, new DateTime(2026, 5, 10, 12, 0, 0, DateTimeKind.Utc));
        db.Transactions.AddRange(first, later);
        await db.SaveChangesAsync();

        var signals = await Build(db).DetectAsync(userId, Reference, CancellationToken.None);

        var s = Assert.Single(signals);
        Assert.Equal(AiInsightKind.Anomaly, s.Kind);
        Assert.Equal(AiInsightSeverity.Warning, s.Severity);
        Assert.Equal($"Anomaly:dup:{later.Id}", s.DedupKey);
        Assert.DoesNotContain(first.Id.ToString(), s.DedupKey);
    }

    [Fact]
    public async Task Duplicate_BelowAmountFloor_NoSignal()
    {
        await using var db = CreateDbContext();
        var userId = Guid.NewGuid();
        var acct = Guid.NewGuid();

        // R$8 repeated within the window → below the R$20 duplicate floor (coffee/transit).
        db.Transactions.Add(Txn(userId, acct, "Cafe", -8m, new DateTime(2026, 5, 10, 10, 0, 0, DateTimeKind.Utc)));
        db.Transactions.Add(Txn(userId, acct, "Cafe", -8m, new DateTime(2026, 5, 10, 11, 0, 0, DateTimeKind.Utc)));
        await db.SaveChangesAsync();

        var signals = await Build(db).DetectAsync(userId, Reference, CancellationToken.None);

        Assert.Empty(signals);
    }

    [Fact]
    public async Task Duplicate_OutsideWindow_NoSignal()
    {
        await using var db = CreateDbContext();
        var userId = Guid.NewGuid();
        var acct = Guid.NewGuid();

        // Same merchant + amount but 3 days apart → outside the 24h window (a legit repeat purchase).
        db.Transactions.Add(Txn(userId, acct, "Loja", -150m, new DateTime(2026, 5, 10, 10, 0, 0, DateTimeKind.Utc)));
        db.Transactions.Add(Txn(userId, acct, "Loja", -150m, new DateTime(2026, 5, 13, 10, 0, 0, DateTimeKind.Utc)));
        await db.SaveChangesAsync();

        var signals = await Build(db).DetectAsync(userId, Reference, CancellationToken.None);

        Assert.Empty(signals);
    }

    // ---- subscription increase (reuses RecurringTransaction) ------------------------------------

    [Fact]
    public async Task SubscriptionIncrease_TwentyToThirtyFive_FlaggedCriticalWith75Pct()
    {
        await using var db = CreateDbContext();
        var userId = Guid.NewGuid();
        var recId = Guid.NewGuid();
        db.RecurringTransactions.Add(new RecurringTransaction
        {
            Id = recId,
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

        var s = Assert.Single(signals);
        Assert.Equal(AiInsightKind.Anomaly, s.Kind);
        Assert.Equal(AiInsightSeverity.Critical, s.Severity);
        Assert.Equal($"Anomaly:subincrease:{recId}:35.00", s.DedupKey);
        Assert.Contains("75%", s.FallbackBody);
        var data = Data(s);
        Assert.Equal(20m, data.GetProperty("previousAverage").GetDecimal());
        Assert.Equal(35m, data.GetProperty("newAverage").GetDecimal());
    }

    [Fact]
    public async Task SubscriptionIncrease_BelowFloorOrNoIncreaseFlag_NoSignal()
    {
        await using var db = CreateDbContext();
        var userId = Guid.NewGuid();
        db.RecurringTransactions.AddRange(
            new RecurringTransaction
            {
                Id = Guid.NewGuid(), UserId = userId, NormalizedMerchant = "NEWS", DisplayName = "Jornal",
                AverageAmount = 21m, PreviousAverageAmount = 20m, IsActive = true, AmountIncreaseDetected = true,
                CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
            },
            new RecurringTransaction
            {
                Id = Guid.NewGuid(), UserId = userId, NormalizedMerchant = "GYM", DisplayName = "Academia",
                AverageAmount = 40m, PreviousAverageAmount = 20m, IsActive = true, AmountIncreaseDetected = false,
                CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
            });
        await db.SaveChangesAsync();

        var signals = await Build(db).DetectAsync(userId, Reference, CancellationToken.None);

        Assert.Empty(signals);
    }

    // ---- atypical category spend ----------------------------------------------------------------

    [Fact]
    public async Task AtypicalCategory_LargeJumpOnly_Flagged()
    {
        await using var db = CreateDbContext();
        var userId = Guid.NewGuid();
        var foodId = Guid.NewGuid();
        var transportId = Guid.NewGuid();

        var cat = new FakeCategorySpendReader();
        // Alimentação: 300→900 (+200%, Δ R$600) → LARGE jump, flagged.
        cat.Add("2026-07", new CategorySpend(foodId, "Alimentação", null, null, 900m));
        cat.Add("2026-06", new CategorySpend(foodId, "Alimentação", null, null, 300m));
        cat.Add("2026-05", new CategorySpend(foodId, "Alimentação", null, null, 300m));
        cat.Add("2026-04", new CategorySpend(foodId, "Alimentação", null, null, 300m));
        // Transporte: 300→350 (+16.7%) → below the 50% anomaly floor, NOT flagged.
        cat.Add("2026-07", new CategorySpend(transportId, "Transporte", null, null, 350m));
        cat.Add("2026-06", new CategorySpend(transportId, "Transporte", null, null, 300m));
        cat.Add("2026-05", new CategorySpend(transportId, "Transporte", null, null, 300m));
        cat.Add("2026-04", new CategorySpend(transportId, "Transporte", null, null, 300m));

        var signals = await Build(db, cat).DetectAsync(userId, Reference, CancellationToken.None);

        var s = Assert.Single(signals);
        Assert.Equal(AiInsightKind.Anomaly, s.Kind);
        Assert.Equal($"Anomaly:category:{foodId}:2026-07", s.DedupKey);
        var data = Data(s);
        Assert.Equal(900m, data.GetProperty("currentAmount").GetDecimal());
        Assert.Equal(300m, data.GetProperty("baselineAmount").GetDecimal());
    }

    // ---- helpers --------------------------------------------------------------------------------

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

    private sealed class FakeCategorySpendReader : ICategorySpendReader
    {
        private readonly Dictionary<string, List<CategorySpend>> _byMonth = new();

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
}
