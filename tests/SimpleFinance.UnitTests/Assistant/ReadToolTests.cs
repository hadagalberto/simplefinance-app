using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SimpleFinance.Domain.Entities;
using SimpleFinance.Domain.Enums;
using SimpleFinance.Infrastructure.Ai;
using SimpleFinance.Infrastructure.Ai.Assistant.Tools;
using SimpleFinance.Infrastructure.Budgets;
using SimpleFinance.Infrastructure.Investments;
using SimpleFinance.Infrastructure.NetWorth;
using SimpleFinance.Infrastructure.Persistence;
using SimpleFinance.Infrastructure.Recurring;
using SimpleFinance.Infrastructure.Reporting;
using SimpleFinance.Application.Budgets;

namespace SimpleFinance.UnitTests.Assistant;

/// <summary>
/// Behavior tests for the seven UserId-scoped read tools (17-02 Task 2, AI-CHAT-02). Dapper-backed
/// tools are exercised against a fake reader (the real queries need Postgres); the EF-backed tools
/// (query_transactions, list_recurring) run against the InMemory provider with two seeded users to
/// prove isolation. Each test asserts: the right service is called with the caller's userId + resolved
/// period, the output is a COMPACT aggregate (never a raw dump), unknown entity names are rejected, and
/// free-text that could carry PII is redacted before it enters the model context.
/// </summary>
public class ReadToolTests
{
    private static SimpleFinanceDbContext CreateDbContext() =>
        new(new DbContextOptionsBuilder<SimpleFinanceDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    // ---- sum_by_category -----------------------------------------------------------------------

    [Fact]
    public async Task SumByCategory_ResolvesMonth_CallsService_ReturnsCompactRoundedItems()
    {
        var userId = Guid.NewGuid();
        var reader = new FakeCategorySpendReader(
        [
            new CategorySpend(Guid.NewGuid(), "Alimentação", null, null, 150.559m),
            new CategorySpend(Guid.NewGuid(), "Transporte", null, null, 80.001m),
            new CategorySpend(null, null, null, null, 12.5m),
        ]);
        var tool = new SumByCategoryTool(reader);

        var json = await tool.ExecuteAsync("""{"period":"2026-05","type":"expense"}""", userId, CancellationToken.None);

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        Assert.Equal("BRL", root.GetProperty("currency").GetString());
        Assert.Equal("expense", root.GetProperty("type").GetString());
        var items = root.GetProperty("items");
        Assert.Equal(3, items.GetArrayLength());
        Assert.Equal("Alimentação", items[0].GetProperty("category").GetString());
        Assert.Equal(150.56m, items[0].GetProperty("amount").GetDecimal()); // rounded to 2dp
        // null category name is labelled, never emitted as null
        Assert.Equal("Sem categoria", items[2].GetProperty("category").GetString());

        // the caller's userId + the resolved May 2026 range reached the service
        Assert.Equal(userId, reader.LastUserId);
        Assert.Equal(new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc), reader.LastStart);
        Assert.Equal(2026, reader.LastEnd.Year);
        Assert.Equal(5, reader.LastEnd.Month);
        Assert.Equal(31, reader.LastEnd.Day);
    }

    [Fact]
    public async Task SumByCategory_InvalidPeriod_ReturnsError_NoServiceCall()
    {
        var reader = new FakeCategorySpendReader([]);
        var tool = new SumByCategoryTool(reader);

        var json = await tool.ExecuteAsync("""{"period":"not-a-period","type":"expense"}""", Guid.NewGuid(), CancellationToken.None);

        Assert.Contains("error", json);
        Assert.False(reader.WasCalled);
    }

    // ---- query_transactions --------------------------------------------------------------------

    [Fact]
    public async Task QueryTransactions_ReturnsAggregate_ScopedToUser_NeverDumpsAllRows()
    {
        await using var db = CreateDbContext();
        var userA = Guid.NewGuid();
        var userB = Guid.NewGuid();

        var food = new Category { Id = Guid.NewGuid(), UserId = userA, Name = "Alimentação", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
        db.Categories.Add(food);
        for (var i = 0; i < 10; i++)
        {
            db.Transactions.Add(NewTxn(userA, new DateTime(2026, 5, 10, 12, 0, 0, DateTimeKind.Utc), -(i + 1) * 10m, "Compra mercado", food.Id));
        }
        // user B rows in the same month must never appear in user A's result
        db.Transactions.Add(NewTxn(userB, new DateTime(2026, 5, 10, 12, 0, 0, DateTimeKind.Utc), -9999m, "Outro usuário", null));
        await db.SaveChangesAsync();

        var tool = new QueryTransactionsTool(db, new PiiRedactor());
        var json = await tool.ExecuteAsync("""{"period":"2026-05"}""", userA, CancellationToken.None);

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        Assert.Equal(10, root.GetProperty("count").GetInt32());
        Assert.Equal(550m, root.GetProperty("total").GetDecimal()); // 10+20+...+100
        // aggregate returns a bounded top-N, not all 10 rows
        Assert.True(root.GetProperty("top").GetArrayLength() <= 5);
        Assert.DoesNotContain("9999", json); // user B's amount never leaked
        Assert.DoesNotContain("Outro usuário", json);
    }

    [Fact]
    public async Task QueryTransactions_UnknownCategoryName_ReturnsError_NotSilentMatch()
    {
        await using var db = CreateDbContext();
        var userA = Guid.NewGuid();
        db.Categories.Add(new Category { Id = Guid.NewGuid(), UserId = userA, Name = "Alimentação", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow });
        await db.SaveChangesAsync();

        var tool = new QueryTransactionsTool(db, new PiiRedactor());
        var json = await tool.ExecuteAsync("""{"period":"2026-05","category":"Categoria Inventada"}""", userA, CancellationToken.None);

        using var doc = JsonDocument.Parse(json);
        Assert.Equal("unknown_category", doc.RootElement.GetProperty("error").GetString());
    }

    [Fact]
    public async Task QueryTransactions_RedactsPiiInDescriptions()
    {
        await using var db = CreateDbContext();
        var userA = Guid.NewGuid();
        db.Transactions.Add(NewTxn(userA, new DateTime(2026, 5, 10, 12, 0, 0, DateTimeKind.Utc), -100m, "Pagamento CPF 123.456.789-09", null));
        await db.SaveChangesAsync();

        var tool = new QueryTransactionsTool(db, new PiiRedactor());
        var json = await tool.ExecuteAsync("""{"period":"2026-05"}""", userA, CancellationToken.None);

        Assert.DoesNotContain("123.456.789-09", json);
        Assert.Contains("REDACTED", json);
    }

    // ---- budget_status -------------------------------------------------------------------------

    [Fact]
    public async Task BudgetStatus_ReturnsLimitVsSpentPerCategory_ForResolvedMonth()
    {
        var userId = Guid.NewGuid();
        var result = new BudgetsResult(2026, 5, false,
        [
            new BudgetRowResult(Guid.NewGuid(), Guid.NewGuid(), "Alimentação", null, null,
                500m, 150.5m, 30.1m, 349.5m, 300m, 60m, BudgetState.Ok, false),
            new BudgetRowResult(Guid.NewGuid(), null, null, null, null,
                2000m, 1000m, 50m, 1000m, 2000m, 100m, BudgetState.Attention, true),
        ]);
        var reader = new FakeBudgetReader(result);
        var tool = new BudgetStatusTool(reader);

        var json = await tool.ExecuteAsync("""{"month":"2026-05"}""", userId, CancellationToken.None);

        using var doc = JsonDocument.Parse(json);
        var items = doc.RootElement.GetProperty("items");
        Assert.Equal(2, items.GetArrayLength());
        Assert.Equal("Alimentação", items[0].GetProperty("category").GetString());
        Assert.Equal(500m, items[0].GetProperty("limit").GetDecimal());
        Assert.Equal(150.5m, items[0].GetProperty("spent").GetDecimal());
        Assert.Equal("Ok", items[0].GetProperty("state").GetString());

        Assert.Equal(userId, reader.LastUserId);
        Assert.Equal(2026, reader.LastYear);
        Assert.Equal(5, reader.LastMonth);
    }

    // ---- net_worth -----------------------------------------------------------------------------

    [Fact]
    public async Task NetWorth_ReturnsCompactSummary_ScopedToUser()
    {
        var userId = Guid.NewGuid();
        var reader = new FakeNetWorthReader(
            new NetWorthSummaryResult(1000m, 5000m, 200m, 5800m),
            new NetWorthEvolutionResult([]));
        var tool = new NetWorthTool(reader);

        var json = await tool.ExecuteAsync("{}", userId, CancellationToken.None);

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        Assert.Equal(5800m, root.GetProperty("netWorth").GetDecimal());
        Assert.Equal("BRL", root.GetProperty("currency").GetString());
        Assert.Equal(userId, reader.LastSummaryUserId);
    }

    [Fact]
    public async Task NetWorthEvolution_MapsRangeToDays_ReturnsCompactPoints()
    {
        var userId = Guid.NewGuid();
        var reader = new FakeNetWorthReader(
            new NetWorthSummaryResult(0, 0, 0, 0),
            new NetWorthEvolutionResult(
            [
                new NetWorthEvolutionPointResult(new DateTime(2026, 4, 1, 0, 0, 0, DateTimeKind.Utc), 1000m),
                new NetWorthEvolutionPointResult(new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc), 1100m),
            ]));
        var tool = new NetWorthEvolutionTool(reader);

        var json = await tool.ExecuteAsync("""{"range":"3m"}""", userId, CancellationToken.None);

        using var doc = JsonDocument.Parse(json);
        Assert.Equal(2, doc.RootElement.GetProperty("points").GetArrayLength());
        Assert.Equal(userId, reader.LastEvolutionUserId);
        Assert.Equal(90, reader.LastEvolutionDays); // 3m -> 90 days
    }

    // ---- investments_summary -------------------------------------------------------------------

    [Fact]
    public async Task InvestmentsSummary_ReturnsCompactTotalsAndDistribution()
    {
        var userId = Guid.NewGuid();
        var reader = new FakeInvestmentsReader(new InvestmentsSummaryResult(
            10000m, 12000m, 2000m, 20m,
            [new InvestmentDistributionItem("Fixed Income", 8000m, 66.67m)],
            [new InvestmentDistributionItem("Banco X", 8000m, 66.67m)]));
        var tool = new InvestmentsSummaryTool(reader);

        var json = await tool.ExecuteAsync("{}", userId, CancellationToken.None);

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        Assert.Equal(12000m, root.GetProperty("totalCurrentValue").GetDecimal());
        Assert.Equal(2000m, root.GetProperty("totalGain").GetDecimal());
        Assert.Equal(1, root.GetProperty("byType").GetArrayLength());
        Assert.Equal(userId, reader.LastUserId);
    }

    // ---- list_recurring ------------------------------------------------------------------------

    [Fact]
    public async Task ListRecurring_ScopedToUser_FiltersActiveOnly_RedactsNames()
    {
        await using var db = CreateDbContext();
        var userA = Guid.NewGuid();
        var userB = Guid.NewGuid();

        db.RecurringTransactions.Add(NewRecurring(userA, "Netflix 4111111111111111", 39.90m, true));
        db.RecurringTransactions.Add(NewRecurring(userA, "Academia inativa", 99m, false));
        db.RecurringTransactions.Add(NewRecurring(userB, "Spotify do outro", 19.90m, true));
        await db.SaveChangesAsync();

        var tool = new ListRecurringTool(new RecurringService(db), new PiiRedactor());
        var json = await tool.ExecuteAsync("""{"activeOnly":true}""", userA, CancellationToken.None);

        using var doc = JsonDocument.Parse(json);
        var items = doc.RootElement.GetProperty("items");
        Assert.Equal(1, items.GetArrayLength()); // only the active one for user A
        Assert.DoesNotContain("Spotify do outro", json); // user B isolation
        Assert.DoesNotContain("4111111111111111", json); // card-like PAN redacted
    }

    [Fact]
    public async Task ListRecurring_DefaultsToAllRows_WhenActiveOnlyOmitted()
    {
        await using var db = CreateDbContext();
        var userA = Guid.NewGuid();
        db.RecurringTransactions.Add(NewRecurring(userA, "Netflix", 39.90m, true));
        db.RecurringTransactions.Add(NewRecurring(userA, "Academia", 99m, false));
        await db.SaveChangesAsync();

        var tool = new ListRecurringTool(new RecurringService(db), new PiiRedactor());
        var json = await tool.ExecuteAsync("{}", userA, CancellationToken.None);

        using var doc = JsonDocument.Parse(json);
        Assert.Equal(2, doc.RootElement.GetProperty("items").GetArrayLength());
    }

    // ---- helpers -------------------------------------------------------------------------------

    private static Transaction NewTxn(Guid userId, DateTime date, decimal amount, string description, Guid? categoryId) =>
        new()
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            AccountId = Guid.NewGuid(),
            Date = date,
            Description = description,
            Amount = amount,
            Type = amount < 0 ? TransactionType.Debit : TransactionType.Credit,
            CurrencyCode = "BRL",
            Status = TransactionStatus.Posted,
            CategoryId = categoryId,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };

    private static RecurringTransaction NewRecurring(Guid userId, string displayName, decimal amount, bool isActive) =>
        new()
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            DisplayName = displayName,
            AverageAmount = amount,
            Frequency = RecurringFrequency.Monthly,
            FirstSeenDate = DateTime.UtcNow.AddMonths(-6),
            LastSeenDate = DateTime.UtcNow,
            NextExpectedDate = DateTime.UtcNow.AddMonths(1),
            OccurrenceCount = 6,
            IsActive = isActive,
            IsSubscription = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };

    // ---- fakes ---------------------------------------------------------------------------------

    private sealed class FakeCategorySpendReader(IReadOnlyList<CategorySpend> rows) : ICategorySpendReader
    {
        public bool WasCalled { get; private set; }
        public Guid LastUserId { get; private set; }
        public DateTime LastStart { get; private set; }
        public DateTime LastEnd { get; private set; }

        public Task<IReadOnlyList<CategorySpend>> GetCategorySpendAsync(Guid userId, DateTime periodStart, DateTime periodEnd, CancellationToken ct)
        {
            WasCalled = true;
            LastUserId = userId;
            LastStart = periodStart;
            LastEnd = periodEnd;
            return Task.FromResult(rows);
        }
    }

    private sealed class FakeBudgetReader(BudgetsResult result) : IBudgetReader
    {
        public Guid LastUserId { get; private set; }
        public int LastYear { get; private set; }
        public int LastMonth { get; private set; }

        public Task<BudgetsResult> GetBudgetsForMonthAsync(Guid userId, int year, int month, CancellationToken ct)
        {
            LastUserId = userId;
            LastYear = year;
            LastMonth = month;
            return Task.FromResult(result);
        }
    }

    private sealed class FakeNetWorthReader(NetWorthSummaryResult summary, NetWorthEvolutionResult evolution) : INetWorthReader
    {
        public Guid LastSummaryUserId { get; private set; }
        public Guid LastEvolutionUserId { get; private set; }
        public int LastEvolutionDays { get; private set; }

        public Task<NetWorthSummaryResult> GetSummaryAsync(Guid userId, CancellationToken ct)
        {
            LastSummaryUserId = userId;
            return Task.FromResult(summary);
        }

        public Task<NetWorthEvolutionResult> GetEvolutionAsync(Guid userId, int days, CancellationToken ct)
        {
            LastEvolutionUserId = userId;
            LastEvolutionDays = days;
            return Task.FromResult(evolution);
        }
    }

    private sealed class FakeInvestmentsReader(InvestmentsSummaryResult summary) : IInvestmentsReader
    {
        public Guid LastUserId { get; private set; }

        public Task<InvestmentsSummaryResult> GetSummaryAsync(Guid userId, CancellationToken ct)
        {
            LastUserId = userId;
            return Task.FromResult(summary);
        }
    }
}
