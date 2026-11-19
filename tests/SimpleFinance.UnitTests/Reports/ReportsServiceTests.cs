using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using SimpleFinance.Domain.Entities;
using SimpleFinance.Domain.Enums;
using SimpleFinance.Infrastructure.Persistence;
using SimpleFinance.Infrastructure.Recurring;
using SimpleFinance.Infrastructure.Reporting;
using SimpleFinance.Infrastructure.Reports;

namespace SimpleFinance.UnitTests.Reports;

/// <summary>
/// Postgres-backed integration tests for ReportsService (07-05) -- ReportsService depends on
/// DashboardQueries/ReportQueries (Dapper, real-Postgres-only), so these run against a real
/// local Postgres exactly like DashboardQueriesTests.cs/ReportQueriesTests.cs
/// (127.0.0.1:5433, ConnectionStrings__Test env var else the docker-compose default). Requires
/// `docker compose up postgres -d` running locally first.
///
/// Every test scopes its rows to a fresh Guid UserId, so tests can run against the same shared
/// database without interfering with each other or requiring cleanup.
/// </summary>
public class ReportsServiceTests
{
    private static string GetConnectionString() =>
        Environment.GetEnvironmentVariable("ConnectionStrings__Test")
        ?? "Host=localhost;Port=5433;Database=simplefinance;Username=simplefinance;Password=changeme";

    private static SimpleFinanceDbContext CreateDbContext() =>
        new(new DbContextOptionsBuilder<SimpleFinanceDbContext>()
            .UseNpgsql(GetConnectionString(), vec => vec.UseVector())
            .Options);

    private static ReportsService CreateService(SimpleFinanceDbContext db)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Default"] = GetConnectionString(),
            })
            .Build();

        var connectionFactory = new NpgsqlDbConnectionFactory(configuration);
        var dashboardQueries = new DashboardQueries(connectionFactory);
        var reportQueries = new ReportQueries(connectionFactory);
        var recurringService = new RecurringService(db);

        return new ReportsService(db, dashboardQueries, reportQueries, recurringService);
    }

    private static Account NewAccount(
        Guid userId, AccountType type, decimal balance, bool isHidden = false) => new()
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
        IsHidden = isHidden,
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

    [Fact]
    public async Task GetSpendingByCategoryAsync_MatchesDashboardQueries_ExcludingTransfer()
    {
        var userId = Guid.NewGuid();
        var periodStart = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc);
        var periodEnd = new DateTime(2026, 6, 30, 23, 59, 59, DateTimeKind.Utc);
        var midPeriod = new DateTime(2026, 6, 15, 12, 0, 0, DateTimeKind.Utc);

        await using var db = CreateDbContext();
        var account = NewAccount(userId, AccountType.Bank, balance: 1000m);
        var category = NewCategory(userId, "Groceries");
        db.Accounts.Add(account);
        db.Categories.Add(category);
        db.Transactions.AddRange(
            NewTransaction(userId, account.Id, midPeriod, amount: 80m, TransactionType.Debit, categoryId: category.Id),
            NewTransaction(userId, account.Id, midPeriod, amount: 5000m, TransactionType.Debit, isTransfer: true, categoryId: category.Id));
        await db.SaveChangesAsync();

        var rows = await CreateService(db).GetSpendingByCategoryAsync(userId, periodStart, periodEnd, CancellationToken.None);

        var row = Assert.Single(rows);
        Assert.Equal(80m, row.Amount);
    }

    [Fact]
    public async Task GetIncomeExpenseAsync_MatchesDashboardQueries_GroupedByMonth()
    {
        var userId = Guid.NewGuid();
        var rangeStart = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc);
        var rangeEnd = new DateTime(2026, 6, 30, 23, 59, 59, DateTimeKind.Utc);
        var midPeriod = new DateTime(2026, 6, 15, 12, 0, 0, DateTimeKind.Utc);

        await using var db = CreateDbContext();
        var account = NewAccount(userId, AccountType.Bank, balance: 1000m);
        db.Accounts.Add(account);
        db.Transactions.AddRange(
            NewTransaction(userId, account.Id, midPeriod, amount: 500m, TransactionType.Credit),
            NewTransaction(userId, account.Id, midPeriod, amount: 200m, TransactionType.Debit));
        await db.SaveChangesAsync();

        var rows = await CreateService(db).GetIncomeExpenseAsync(userId, rangeStart, rangeEnd, CancellationToken.None);

        var row = Assert.Single(rows);
        Assert.Equal(500m, row.Income);
        Assert.Equal(200m, row.Expense);
    }

    [Fact]
    public async Task GetNetWorthEvolutionAsync_ReturnsEmptyList_NotThrow()
    {
        var userId = Guid.NewGuid();
        await using var db = CreateDbContext();

        var rows = await CreateService(db).GetNetWorthEvolutionAsync(userId, 30, CancellationToken.None);

        Assert.Empty(rows);
    }

    [Fact]
    public async Task GetCashFlowByAccountAsync_MatchesReportQueries()
    {
        var userId = Guid.NewGuid();
        var periodStart = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc);
        var periodEnd = new DateTime(2026, 6, 30, 23, 59, 59, DateTimeKind.Utc);
        var midPeriod = new DateTime(2026, 6, 15, 12, 0, 0, DateTimeKind.Utc);

        await using var db = CreateDbContext();
        var account = NewAccount(userId, AccountType.Bank, balance: 1000m);
        db.Accounts.Add(account);
        db.Transactions.Add(NewTransaction(userId, account.Id, midPeriod, amount: 300m, TransactionType.Credit));
        await db.SaveChangesAsync();

        var rows = await CreateService(db).GetCashFlowByAccountAsync(userId, periodStart, periodEnd, CancellationToken.None);

        var row = Assert.Single(rows);
        Assert.Equal(300m, row.Inflow);
        Assert.Equal(300m, row.Net);
    }

    [Fact]
    public async Task GetCardStatementsAsync_ReturnsEmptyList_WhenNoCreditAccounts()
    {
        var userId = Guid.NewGuid();
        var periodStart = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc);
        var periodEnd = new DateTime(2026, 6, 30, 23, 59, 59, DateTimeKind.Utc);

        await using var db = CreateDbContext();

        var rows = await CreateService(db).GetCardStatementsAsync(userId, periodStart, periodEnd, CancellationToken.None);

        Assert.Empty(rows);
    }

    [Fact]
    public async Task GetRecurringReportAsync_DelegatesToRecurringService()
    {
        var userId = Guid.NewGuid();
        await using var db = CreateDbContext();

        var result = await CreateService(db).GetRecurringReportAsync(userId, CancellationToken.None);

        Assert.Equal(0m, result.MonthlyCommittedCost);
        Assert.Empty(result.Items);
    }

    [Fact]
    public async Task GetBudgetRealizedVsPlannedAsync_JoinsBudgetToRealSpend_ExcludingGlobalCap()
    {
        var userId = Guid.NewGuid();
        var year = 2026;
        var month = 6;
        var monthDate = new DateTime(2026, 6, 15, 12, 0, 0, DateTimeKind.Utc);

        await using var db = CreateDbContext();
        var account = NewAccount(userId, AccountType.Bank, balance: 1000m);
        var category = NewCategory(userId, "Groceries");
        db.Accounts.Add(account);
        db.Categories.Add(category);
        db.Transactions.Add(NewTransaction(userId, account.Id, monthDate, amount: 150m, TransactionType.Debit, categoryId: category.Id));
        db.Budgets.AddRange(
            NewBudget(userId, category.Id, year, month, limitAmount: 200m),
            // Global cap -- must be excluded from this report.
            NewBudget(userId, null, year, month, limitAmount: 1000m));
        await db.SaveChangesAsync();

        var rows = await CreateService(db).GetBudgetRealizedVsPlannedAsync(userId, year, month, CancellationToken.None);

        var row = Assert.Single(rows);
        Assert.Equal(category.Id, row.CategoryId);
        Assert.Equal(200m, row.PlannedAmount);
        Assert.Equal(150m, row.RealizedAmount);
        Assert.Equal(50m, row.Variance);
    }

    [Fact]
    public async Task GetBudgetRealizedVsPlannedAsync_ZeroRealized_WhenNoSpendThisMonth()
    {
        var userId = Guid.NewGuid();
        var year = 2026;
        var month = 6;

        await using var db = CreateDbContext();
        var category = NewCategory(userId, "Entertainment");
        db.Categories.Add(category);
        db.Budgets.Add(NewBudget(userId, category.Id, year, month, limitAmount: 100m));
        await db.SaveChangesAsync();

        var rows = await CreateService(db).GetBudgetRealizedVsPlannedAsync(userId, year, month, CancellationToken.None);

        var row = Assert.Single(rows);
        Assert.Equal(0m, row.RealizedAmount);
        Assert.Equal(100m, row.Variance);
    }
}
