using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using SimpleFinance.Application.Budgets;
using SimpleFinance.Domain.Entities;
using SimpleFinance.Domain.Enums;
using SimpleFinance.Infrastructure.Dashboard;
using SimpleFinance.Infrastructure.Persistence;
using SimpleFinance.Infrastructure.Reporting;

namespace SimpleFinance.UnitTests.Dashboard;

/// <summary>
/// Postgres-backed integration tests for DashboardService (05-06) -- DashboardService delegates
/// every actual aggregate to DashboardQueries (Dapper), which requires a REAL Postgres connection
/// (EF Core's InMemory provider has no ADO.NET connection behind it for Dapper to use). Same
/// connection-string-resolution pattern as DashboardQueriesTests.cs/BudgetServiceTests.cs:
/// ConnectionStrings__Test env var, else the docker-compose default at 127.0.0.1:5433. Requires
/// `docker compose up postgres -d` running locally first.
///
/// Every test scopes its rows to a fresh Guid UserId, so tests can run against the same shared
/// database without interfering with each other or requiring cleanup (matches
/// DashboardQueriesTests.cs/BudgetServiceTests.cs precedent).
/// </summary>
public class DashboardServiceTests
{
    private static string GetConnectionString() =>
        Environment.GetEnvironmentVariable("ConnectionStrings__Test")
        ?? "Host=localhost;Port=5433;Database=simplefinance;Username=simplefinance;Password=changeme";

    private static SimpleFinanceDbContext CreateDbContext() =>
        new(new DbContextOptionsBuilder<SimpleFinanceDbContext>()
            .UseNpgsql(GetConnectionString(), vec => vec.UseVector())
            .Options);

    private static DashboardQueries CreateDashboardQueries()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Default"] = GetConnectionString(),
            })
            .Build();

        return new DashboardQueries(new NpgsqlDbConnectionFactory(configuration));
    }

    private static DashboardService CreateService(SimpleFinanceDbContext db) =>
        new(db, CreateDashboardQueries());

    private static Account NewAccount(Guid userId, AccountType type, decimal balance) => new()
    {
        Id = Guid.NewGuid(),
        UserId = userId,
        ConnectionId = Guid.NewGuid(),
        PluggyAccountId = $"acct-{Guid.NewGuid():N}",
        Type = type,
        Subtype = type == AccountType.Credit ? AccountSubtype.CreditCard : AccountSubtype.CheckingAccount,
        Name = "Test Account",
        Balance = balance,
        CurrencyCode = "BRL",
        CreatedAt = DateTime.UtcNow,
        UpdatedAt = DateTime.UtcNow,
    };

    private static Category NewCategory(Guid userId, string name) => new()
    {
        Id = Guid.NewGuid(),
        UserId = userId,
        Name = name,
        Type = CategoryType.Expense,
        Icon = "icon",
        Color = "#000000",
        CreatedAt = DateTime.UtcNow,
        UpdatedAt = DateTime.UtcNow,
    };

    private static Transaction NewTransaction(
        Guid userId, Guid accountId, DateTime date, decimal amount, TransactionType type,
        bool isTransfer = false, bool isHidden = false, Guid? categoryId = null) => new()
    {
        Id = Guid.NewGuid(),
        UserId = userId,
        AccountId = accountId,
        PluggyTransactionId = $"txn-{Guid.NewGuid():N}",
        Date = date,
        Description = "Test Transaction",
        Amount = amount,
        Type = type,
        CurrencyCode = "BRL",
        Status = TransactionStatus.Posted,
        CategorySource = CategorySource.SystemMapping,
        CategoryId = categoryId,
        IsTransfer = isTransfer,
        IsHidden = isHidden,
        CreatedAt = DateTime.UtcNow,
        UpdatedAt = DateTime.UtcNow,
    };

    private static Budget NewBudget(Guid userId, Guid? categoryId, int year, int month, decimal limitAmount) => new()
    {
        Id = Guid.NewGuid(),
        UserId = userId,
        CategoryId = categoryId,
        Year = year,
        Month = month,
        LimitAmount = limitAmount,
        CreatedAt = DateTime.UtcNow,
        UpdatedAt = DateTime.UtcNow,
    };

    private static CreditCardDetail NewCreditCardDetail(
        Guid accountId, DateTime? balanceDueDate, decimal? lastStatementBalance) => new()
    {
        Id = Guid.NewGuid(),
        AccountId = accountId,
        BalanceDueDate = balanceDueDate,
        LastStatementBalance = lastStatementBalance,
        Status = CreditCardStatus.Active,
        CreatedAt = DateTime.UtcNow,
        UpdatedAt = DateTime.UtcNow,
    };

    private static Account NewAccount(
        Guid userId, AccountType type, decimal balance, Guid connectionId, bool isHidden = false) => new()
    {
        Id = Guid.NewGuid(),
        UserId = userId,
        ConnectionId = connectionId,
        PluggyAccountId = $"acct-{Guid.NewGuid():N}",
        Type = type,
        Subtype = type == AccountType.Credit ? AccountSubtype.CreditCard : AccountSubtype.CheckingAccount,
        Name = "Test Card Account",
        Balance = balance,
        CurrencyCode = "BRL",
        IsHidden = isHidden,
        CreatedAt = DateTime.UtcNow,
        UpdatedAt = DateTime.UtcNow,
    };

    private static Connection NewConnection(Guid userId, string institutionName) => new()
    {
        Id = Guid.NewGuid(),
        UserId = userId,
        PluggyItemId = $"item-{Guid.NewGuid():N}",
        InstitutionName = institutionName,
        Status = ConnectionStatus.Updated,
        CreatedAt = DateTime.UtcNow,
        UpdatedAt = DateTime.UtcNow,
    };

    [Fact]
    public async Task GetSummaryAsync_PeriodNet_IsAlwaysIncomeMinusExpense()
    {
        var userId = Guid.NewGuid();
        var periodStart = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc);
        var periodEnd = new DateTime(2026, 6, 30, 23, 59, 59, DateTimeKind.Utc);
        var midPeriod = new DateTime(2026, 6, 15, 12, 0, 0, DateTimeKind.Utc);

        await using var db = CreateDbContext();
        var account = NewAccount(userId, AccountType.Bank, balance: 1000m);
        db.Accounts.Add(account);
        db.Transactions.AddRange(
            NewTransaction(userId, account.Id, midPeriod, amount: 500m, TransactionType.Credit),
            NewTransaction(userId, account.Id, midPeriod, amount: 200m, TransactionType.Debit));
        await db.SaveChangesAsync();

        var result = await CreateService(db).GetSummaryAsync(userId, periodStart, periodEnd, CancellationToken.None);

        Assert.Equal(500m, result.PeriodIncome);
        Assert.Equal(200m, result.PeriodExpense);
        Assert.Equal(300m, result.PeriodNet);
    }

    [Fact]
    public async Task GetSpendingByCategoryAsync_ExactlyFiveCategories_NoOthersRow()
    {
        var userId = Guid.NewGuid();
        var periodStart = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc);
        var periodEnd = new DateTime(2026, 6, 30, 23, 59, 59, DateTimeKind.Utc);
        var midPeriod = new DateTime(2026, 6, 15, 12, 0, 0, DateTimeKind.Utc);

        await using var db = CreateDbContext();
        var account = NewAccount(userId, AccountType.Bank, balance: 1000m);
        db.Accounts.Add(account);

        decimal[] amounts = [100m, 90m, 80m, 70m, 60m];
        foreach (var amount in amounts)
        {
            var category = NewCategory(userId, $"Category-{amount}");
            db.Categories.Add(category);
            db.Transactions.Add(NewTransaction(userId, account.Id, midPeriod, amount, TransactionType.Debit, categoryId: category.Id));
        }

        await db.SaveChangesAsync();

        var result = await CreateService(db).GetSpendingByCategoryAsync(userId, periodStart, periodEnd, CancellationToken.None);

        Assert.Equal(5, result.Items.Count);
        Assert.DoesNotContain(result.Items, i => i.CategoryName == "Others");
        Assert.Equal(400m, result.TotalAmount);
    }

    [Fact]
    public async Task GetSpendingByCategoryAsync_SevenCategories_TopFivePlusOthersBucket()
    {
        var userId = Guid.NewGuid();
        var periodStart = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc);
        var periodEnd = new DateTime(2026, 6, 30, 23, 59, 59, DateTimeKind.Utc);
        var midPeriod = new DateTime(2026, 6, 15, 12, 0, 0, DateTimeKind.Utc);

        await using var db = CreateDbContext();
        var account = NewAccount(userId, AccountType.Bank, balance: 1000m);
        db.Accounts.Add(account);

        decimal[] amounts = [100m, 90m, 80m, 70m, 60m, 50m, 40m];
        foreach (var amount in amounts)
        {
            var category = NewCategory(userId, $"Category-{amount}");
            db.Categories.Add(category);
            db.Transactions.Add(NewTransaction(userId, account.Id, midPeriod, amount, TransactionType.Debit, categoryId: category.Id));
        }

        await db.SaveChangesAsync();

        var result = await CreateService(db).GetSpendingByCategoryAsync(userId, periodStart, periodEnd, CancellationToken.None);

        Assert.Equal(6, result.Items.Count);
        var namedItems = result.Items.Where(i => i.CategoryName != "Others").ToList();
        Assert.Equal(5, namedItems.Count);
        var others = Assert.Single(result.Items, i => i.CategoryName == "Others");
        Assert.Equal(90m, others.Amount); // 50 + 40, the two smallest
        Assert.Null(others.CategoryId);
        Assert.Equal(490m, result.TotalAmount);
    }

    [Fact]
    public async Task GetSpendingByCategoryAsync_UncategorizedInTopFive_UncategorizedAndOthersAreDistinct()
    {
        var userId = Guid.NewGuid();
        var periodStart = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc);
        var periodEnd = new DateTime(2026, 6, 30, 23, 59, 59, DateTimeKind.Utc);
        var midPeriod = new DateTime(2026, 6, 15, 12, 0, 0, DateTimeKind.Utc);

        await using var db = CreateDbContext();
        var account = NewAccount(userId, AccountType.Bank, balance: 1000m);
        db.Accounts.Add(account);

        // 6 categorized transactions with amounts 100/90/80/70/10/5, plus one uncategorized
        // transaction of 60 -- sorted descending: 100,90,80,70,60(uncategorized),10,5. The
        // uncategorized group ranks 5th (within the top 5), so it must appear as "Uncategorized"
        // distinct from the trailing "Others" bucket (10+5=15).
        decimal[] categorizedAmounts = [100m, 90m, 80m, 70m, 10m, 5m];
        foreach (var amount in categorizedAmounts)
        {
            var category = NewCategory(userId, $"Category-{amount}");
            db.Categories.Add(category);
            db.Transactions.Add(NewTransaction(userId, account.Id, midPeriod, amount, TransactionType.Debit, categoryId: category.Id));
        }

        db.Transactions.Add(NewTransaction(userId, account.Id, midPeriod, 60m, TransactionType.Debit, categoryId: null));

        await db.SaveChangesAsync();

        var result = await CreateService(db).GetSpendingByCategoryAsync(userId, periodStart, periodEnd, CancellationToken.None);

        Assert.Equal(6, result.Items.Count);
        var uncategorized = Assert.Single(result.Items, i => i.CategoryName == "Uncategorized");
        Assert.Null(uncategorized.CategoryId);
        Assert.Equal(60m, uncategorized.Amount);
        var others = Assert.Single(result.Items, i => i.CategoryName == "Others");
        Assert.Null(others.CategoryId);
        Assert.Equal(15m, others.Amount);
        Assert.NotEqual(uncategorized, others);
    }

    [Fact]
    public async Task GetIncomeExpenseAsync_SixMonthRange_GapFillsMissingMonthsWithZero()
    {
        var userId = Guid.NewGuid();
        var now = DateTime.UtcNow;
        var currentMonthStart = new DateTime(now.Year, now.Month, 1, 0, 0, 0, DateTimeKind.Utc);
        var threeMonthsBack = currentMonthStart.AddMonths(-3);

        await using var db = CreateDbContext();
        var account = NewAccount(userId, AccountType.Bank, balance: 1000m);
        db.Accounts.Add(account);
        db.Transactions.AddRange(
            NewTransaction(userId, account.Id, currentMonthStart.AddDays(5), amount: 1000m, TransactionType.Credit),
            NewTransaction(userId, account.Id, currentMonthStart.AddDays(5), amount: 400m, TransactionType.Debit),
            NewTransaction(userId, account.Id, threeMonthsBack.AddDays(5), amount: 300m, TransactionType.Debit));
        await db.SaveChangesAsync();

        var result = await CreateService(db).GetIncomeExpenseAsync(userId, monthsBack: 6, CancellationToken.None);

        Assert.Equal(6, result.Items.Count);

        var current = result.Items[5];
        Assert.Equal(currentMonthStart.Year, current.Year);
        Assert.Equal(currentMonthStart.Month, current.Month);
        Assert.Equal(1000m, current.Income);
        Assert.Equal(400m, current.Expense);

        var threeBack = result.Items[2];
        Assert.Equal(threeMonthsBack.Year, threeBack.Year);
        Assert.Equal(threeMonthsBack.Month, threeBack.Month);
        Assert.Equal(0m, threeBack.Income);
        Assert.Equal(300m, threeBack.Expense);

        // The 4 remaining months (indices 0,1,3,4) must be zeroed, never omitted/reordered.
        foreach (var index in new[] { 0, 1, 3, 4 })
        {
            Assert.Equal(0m, result.Items[index].Income);
            Assert.Equal(0m, result.Items[index].Expense);
        }
    }

    [Fact]
    public async Task GetNetWorthEvolutionAsync_EmptySnapshotTable_ReturnsEmptyPoints_NotThrow()
    {
        var userId = Guid.NewGuid();

        await using var db = CreateDbContext();

        // No NetWorthSnapshot rows are seeded for this userId -- the table is schema-only until
        // Phase 6's INV-04 job. This must return an empty list, never throw.
        var result = await CreateService(db).GetNetWorthEvolutionAsync(userId, monthsBack: 6, CancellationToken.None);

        Assert.Empty(result.Points);
    }

    [Fact]
    public async Task GetAtRiskBudgetsAsync_IncludesAttentionAndProjectedToExceed_ExcludesPlainOk()
    {
        var userId = Guid.NewGuid();
        var now = DateTime.UtcNow;
        var (year, month) = (now.Year, now.Month);
        var midMonth = new DateTime(year, month, 1, 12, 0, 0, DateTimeKind.Utc);
        var dayOfMonth = Math.Max(1, now.Day);
        var daysInMonth = DateTime.DaysInMonth(year, month);
        var ratio = (decimal)daysInMonth / dayOfMonth; // >= 1

        const decimal limit = 1000m;

        // OK: percentOfCap safely below both 80% (Attention threshold) and 100/ratio% (the
        // projection threshold), so it is never at-risk nor projected to exceed regardless of
        // how far into the month "today" happens to be.
        var okPercent = Math.Min(30m, 90m / ratio);
        var okSpent = limit * okPercent / 100m;

        // Attention: fixed 85% of cap -- triggers BudgetState.Attention (>= 80%) regardless of
        // day-of-month/projection.
        var attentionSpent = limit * 0.85m;

        // OK-but-projected-to-exceed: percentOfCap strictly between (dayOfMonth/daysInMonth)*100
        // and 80%, so State stays Ok but the linear end-of-month projection exceeds 100%.
        var lowerBoundPercent = 100m / ratio;
        var projectedPercent = (lowerBoundPercent + 80m) / 2m;
        var projectedSpent = limit * projectedPercent / 100m;

        await using var db = CreateDbContext();
        var okCategory = NewCategory(userId, "OkCategory");
        var attentionCategory = NewCategory(userId, "AttentionCategory");
        var projectedCategory = NewCategory(userId, "ProjectedCategory");
        var account = NewAccount(userId, AccountType.Bank, balance: 1000m);
        db.Categories.AddRange(okCategory, attentionCategory, projectedCategory);
        db.Accounts.Add(account);
        db.Budgets.AddRange(
            NewBudget(userId, okCategory.Id, year, month, limit),
            NewBudget(userId, attentionCategory.Id, year, month, limit),
            NewBudget(userId, projectedCategory.Id, year, month, limit));
        db.Transactions.AddRange(
            NewTransaction(userId, account.Id, midMonth, okSpent, TransactionType.Debit, categoryId: okCategory.Id),
            NewTransaction(userId, account.Id, midMonth, attentionSpent, TransactionType.Debit, categoryId: attentionCategory.Id),
            NewTransaction(userId, account.Id, midMonth, projectedSpent, TransactionType.Debit, categoryId: projectedCategory.Id));
        await db.SaveChangesAsync();

        var result = await CreateService(db).GetAtRiskBudgetsAsync(userId, CancellationToken.None);

        Assert.Equal(2, result.Items.Count);
        Assert.DoesNotContain(result.Items, i => i.CategoryId == okCategory.Id);
        var attentionItem = Assert.Single(result.Items, i => i.CategoryId == attentionCategory.Id);
        Assert.Equal(BudgetState.Attention, attentionItem.State);
        var projectedItem = Assert.Single(result.Items, i => i.CategoryId == projectedCategory.Id);
        Assert.Equal(BudgetState.Ok, projectedItem.State);
        Assert.True(projectedItem.ProjectedPercent > 100m);
    }

    [Fact]
    public async Task GetAtRiskBudgetsAsync_NoBudgetsThisMonth_ReturnsEmptyItems_NotThrow()
    {
        var userId = Guid.NewGuid();

        await using var db = CreateDbContext();

        var result = await CreateService(db).GetAtRiskBudgetsAsync(userId, CancellationToken.None);

        Assert.Empty(result.Items);
    }

    [Fact]
    public async Task GetUpcomingDueDatesAsync_RespectsSevenDayWindow_AndExcludesHiddenAccount()
    {
        var userId = Guid.NewGuid();
        var today = DateTime.UtcNow.Date;

        await using var db = CreateDbContext();
        var connection = NewConnection(userId, "Test Bank");
        db.Connections.Add(connection);

        var withinWindowAccount = NewAccount(userId, AccountType.Credit, balance: 0m, connection.Id);
        var outsideWindowAccount = NewAccount(userId, AccountType.Credit, balance: 0m, connection.Id);
        var hiddenAccount = NewAccount(userId, AccountType.Credit, balance: 0m, connection.Id, isHidden: true);
        db.Accounts.AddRange(withinWindowAccount, outsideWindowAccount, hiddenAccount);

        db.CreditCardDetails.AddRange(
            // Exactly 7 days out -- must be INCLUDED (inclusive window).
            NewCreditCardDetail(withinWindowAccount.Id, today.AddDays(7), lastStatementBalance: 250m),
            // 8 days out -- must be EXCLUDED.
            NewCreditCardDetail(outsideWindowAccount.Id, today.AddDays(8), lastStatementBalance: 999m),
            // Within window, but the account is hidden -- must be EXCLUDED.
            NewCreditCardDetail(hiddenAccount.Id, today.AddDays(3), lastStatementBalance: 777m));
        await db.SaveChangesAsync();

        var result = await CreateService(db).GetUpcomingDueDatesAsync(userId, CancellationToken.None);

        var item = Assert.Single(result.Items);
        Assert.Equal(withinWindowAccount.Id, item.AccountId);
        Assert.Equal("Test Bank", item.InstitutionName);
        Assert.Equal(250m, item.StatementBalance);
    }
}
