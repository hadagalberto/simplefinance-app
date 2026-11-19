using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using SimpleFinance.Application.Budgets;
using SimpleFinance.Domain.Entities;
using SimpleFinance.Domain.Enums;
using SimpleFinance.Infrastructure.Budgets;
using SimpleFinance.Infrastructure.Persistence;
using SimpleFinance.Infrastructure.Reporting;

namespace SimpleFinance.UnitTests.Budgets;

/// <summary>
/// Postgres-backed integration tests for BudgetService (05-05 Task 1) -- BudgetService reads
/// spend via DashboardQueries' Dapper-backed GetCategorySpendAsync, which requires a REAL
/// Postgres connection (EF Core's InMemory provider has no ADO.NET connection behind it for
/// Dapper to use). Same connection-string-resolution pattern as ConcurrentSyncTests.cs/
/// DashboardQueriesTests.cs: ConnectionStrings__Test env var, else the docker-compose default at
/// 127.0.0.1:5433. Requires `docker compose up postgres -d` running locally first.
///
/// Every test scopes its rows to a fresh Guid UserId, so tests can run against the same shared
/// database without interfering with each other or requiring cleanup (matches
/// ConcurrentSyncTests.cs/DashboardQueriesTests.cs precedent).
/// </summary>
public class BudgetServiceTests
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

    private static BudgetService CreateService(SimpleFinanceDbContext db) =>
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

    [Fact]
    public async Task GetBudgetsForMonthAsync_NoRowsForMonth_CopiesForwardFromPriorMonth_AndIsIdempotent()
    {
        var userId = Guid.NewGuid();
        var now = DateTime.UtcNow;
        var (year, month) = (now.Year, now.Month);
        var (prevYear, prevMonth) = month == 1 ? (year - 1, 12) : (year, month - 1);
        var currentMonthDate = new DateTime(year, month, 1, 12, 0, 0, DateTimeKind.Utc);

        await using var db = CreateDbContext();
        var category = NewCategory(userId, "Groceries");
        var account = NewAccount(userId, AccountType.Bank, 1000m);
        db.Categories.Add(category);
        db.Accounts.Add(account);
        db.Budgets.Add(NewBudget(userId, category.Id, prevYear, prevMonth, 500m));
        // Current-month expense -- the copied row's SpentAmount must reflect THIS transaction,
        // never a stale prior-month figure.
        db.Transactions.Add(NewTransaction(userId, account.Id, currentMonthDate, 120m, TransactionType.Debit, categoryId: category.Id));
        await db.SaveChangesAsync();

        var service = CreateService(db);

        var firstCall = await service.GetBudgetsForMonthAsync(userId, year, month, CancellationToken.None);
        Assert.True(firstCall.CopiedFromPreviousMonth);
        var copiedRow = Assert.Single(firstCall.Items);
        Assert.Equal(500m, copiedRow.LimitAmount);
        Assert.Equal(120m, copiedRow.SpentAmount);

        var secondCall = await service.GetBudgetsForMonthAsync(userId, year, month, CancellationToken.None);
        Assert.False(secondCall.CopiedFromPreviousMonth);
        Assert.Single(secondCall.Items);
    }

    [Fact]
    public async Task GetBudgetsForMonthAsync_GlobalCapRow_SumsSpendAcrossAllBudgetedCategories()
    {
        var userId = Guid.NewGuid();
        var now = DateTime.UtcNow;
        var (year, month) = (now.Year, now.Month);
        var midMonth = new DateTime(year, month, 1, 12, 0, 0, DateTimeKind.Utc);

        await using var db = CreateDbContext();
        var categoryA = NewCategory(userId, "Groceries");
        var categoryB = NewCategory(userId, "Transport");
        var account = NewAccount(userId, AccountType.Bank, 1000m);
        db.Categories.AddRange(categoryA, categoryB);
        db.Accounts.Add(account);
        db.Budgets.AddRange(
            NewBudget(userId, categoryA.Id, year, month, 300m),
            NewBudget(userId, categoryB.Id, year, month, 200m),
            NewBudget(userId, null, year, month, 1000m));
        db.Transactions.AddRange(
            NewTransaction(userId, account.Id, midMonth, 100m, TransactionType.Debit, categoryId: categoryA.Id),
            NewTransaction(userId, account.Id, midMonth, 50m, TransactionType.Debit, categoryId: categoryB.Id));
        await db.SaveChangesAsync();

        var result = await CreateService(db).GetBudgetsForMonthAsync(userId, year, month, CancellationToken.None);

        Assert.Equal(3, result.Items.Count);
        var globalRow = Assert.Single(result.Items, r => r.CategoryId == null);
        Assert.Equal(150m, globalRow.SpentAmount);
    }

    [Fact]
    public async Task GetBudgetsForMonthAsync_ExcludesTransferAndHiddenTransactions_FromSpentAmount()
    {
        var userId = Guid.NewGuid();
        var now = DateTime.UtcNow;
        var (year, month) = (now.Year, now.Month);
        var midMonth = new DateTime(year, month, 1, 12, 0, 0, DateTimeKind.Utc);

        await using var db = CreateDbContext();
        var category = NewCategory(userId, "Groceries");
        var account = NewAccount(userId, AccountType.Bank, 1000m);
        db.Categories.Add(category);
        db.Accounts.Add(account);
        db.Budgets.Add(NewBudget(userId, category.Id, year, month, 300m));
        db.Transactions.AddRange(
            NewTransaction(userId, account.Id, midMonth, 80m, TransactionType.Debit, categoryId: category.Id),
            // Neither of these must contribute to SpentAmount, end-to-end through BudgetService.
            NewTransaction(userId, account.Id, midMonth, 999m, TransactionType.Debit, categoryId: category.Id, isTransfer: true),
            NewTransaction(userId, account.Id, midMonth, 777m, TransactionType.Debit, categoryId: category.Id, isHidden: true));
        await db.SaveChangesAsync();

        var result = await CreateService(db).GetBudgetsForMonthAsync(userId, year, month, CancellationToken.None);

        var row = Assert.Single(result.Items);
        Assert.Equal(80m, row.SpentAmount);
    }

    [Fact]
    public async Task GetBudgetsForMonthAsync_SpendExactlyAt80Percent_RendersStateAttention()
    {
        var userId = Guid.NewGuid();
        var now = DateTime.UtcNow;
        var (year, month) = (now.Year, now.Month);
        var midMonth = new DateTime(year, month, 1, 12, 0, 0, DateTimeKind.Utc);

        await using var db = CreateDbContext();
        var category = NewCategory(userId, "Groceries");
        var account = NewAccount(userId, AccountType.Bank, 1000m);
        db.Categories.Add(category);
        db.Accounts.Add(account);
        db.Budgets.Add(NewBudget(userId, category.Id, year, month, 100m));
        db.Transactions.Add(NewTransaction(userId, account.Id, midMonth, 80m, TransactionType.Debit, categoryId: category.Id));
        await db.SaveChangesAsync();

        var result = await CreateService(db).GetBudgetsForMonthAsync(userId, year, month, CancellationToken.None);

        var row = Assert.Single(result.Items);
        Assert.Equal(BudgetState.Attention, row.State);
    }

    [Fact]
    public async Task UpsertBudgetAsync_CategoryBelongsToDifferentUser_ReturnsNull()
    {
        var userId = Guid.NewGuid();
        var otherUserId = Guid.NewGuid();
        var now = DateTime.UtcNow;

        await using var db = CreateDbContext();
        var foreignCategory = NewCategory(otherUserId, "Foreign");
        db.Categories.Add(foreignCategory);
        await db.SaveChangesAsync();

        var result = await CreateService(db).UpsertBudgetAsync(
            userId, foreignCategory.Id, now.Year, now.Month, 100m, CancellationToken.None);

        Assert.Null(result);
    }

    [Fact]
    public async Task UpsertBudgetAsync_CalledTwiceForSameTuple_UpdatesExistingRow_NeverDuplicates()
    {
        var userId = Guid.NewGuid();
        var now = DateTime.UtcNow;
        var (year, month) = (now.Year, now.Month);

        await using var db = CreateDbContext();
        var category = NewCategory(userId, "Groceries");
        db.Categories.Add(category);
        await db.SaveChangesAsync();

        var service = CreateService(db);
        var first = await service.UpsertBudgetAsync(userId, category.Id, year, month, 100m, CancellationToken.None);
        var second = await service.UpsertBudgetAsync(userId, category.Id, year, month, 250m, CancellationToken.None);

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.Equal(first!.Id, second!.Id);
        Assert.Equal(250m, second.LimitAmount);

        await using var assertDb = CreateDbContext();
        var count = await assertDb.Budgets.CountAsync(
            b => b.UserId == userId && b.CategoryId == category.Id && b.Year == year && b.Month == month);
        Assert.Equal(1, count);
    }

    [Fact]
    public async Task DeleteBudgetAsync_ExistingOwnedBudget_RemovesRow_ReturnsTrue()
    {
        var userId = Guid.NewGuid();
        var now = DateTime.UtcNow;

        await using var db = CreateDbContext();
        var budget = NewBudget(userId, null, now.Year, now.Month, 500m);
        db.Budgets.Add(budget);
        await db.SaveChangesAsync();

        var deleted = await CreateService(db).DeleteBudgetAsync(userId, budget.Id, CancellationToken.None);

        Assert.True(deleted);

        await using var assertDb = CreateDbContext();
        Assert.False(await assertDb.Budgets.AnyAsync(b => b.Id == budget.Id));
    }

    [Fact]
    public async Task DeleteBudgetAsync_NonExistentOrForeignId_ReturnsFalse()
    {
        var userId = Guid.NewGuid();
        var otherUserId = Guid.NewGuid();
        var now = DateTime.UtcNow;

        await using var db = CreateDbContext();
        var foreignBudget = NewBudget(otherUserId, null, now.Year, now.Month, 500m);
        db.Budgets.Add(foreignBudget);
        await db.SaveChangesAsync();

        var deletedForeign = await CreateService(db).DeleteBudgetAsync(userId, foreignBudget.Id, CancellationToken.None);
        var deletedMissing = await CreateService(db).DeleteBudgetAsync(userId, Guid.NewGuid(), CancellationToken.None);

        Assert.False(deletedForeign);
        Assert.False(deletedMissing);
    }
}
