using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using SimpleFinance.Domain.Entities;
using SimpleFinance.Domain.Enums;
using SimpleFinance.Infrastructure.Persistence;
using SimpleFinance.Infrastructure.Reporting;

namespace SimpleFinance.UnitTests.Reporting;

/// <summary>
/// Postgres-backed integration tests for ReportQueries (07-05) -- Dapper's raw SQL isn't
/// exercised by EF Core's InMemory provider, so these run against a real local Postgres, exactly
/// like DashboardQueriesTests.cs (127.0.0.1:5433, ConnectionStrings__Test env var else the
/// docker-compose default). Requires `docker compose up postgres -d` running locally first.
///
/// Every test scopes its rows to a fresh Guid UserId, so tests can run against the same shared
/// database without interfering with each other or requiring cleanup (matches
/// DashboardQueriesTests.cs's precedent).
/// </summary>
public class ReportQueriesTests
{
    private static string GetConnectionString() =>
        Environment.GetEnvironmentVariable("ConnectionStrings__Test")
        ?? "Host=localhost;Port=5433;Database=simplefinance;Username=simplefinance;Password=changeme";

    private static SimpleFinanceDbContext CreateDbContext() =>
        new(new DbContextOptionsBuilder<SimpleFinanceDbContext>()
            .UseNpgsql(GetConnectionString(), vec => vec.UseVector())
            .Options);

    private static ReportQueries CreateQueries()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Default"] = GetConnectionString(),
            })
            .Build();

        return new ReportQueries(new NpgsqlDbConnectionFactory(configuration));
    }

    private static Account NewAccount(
        Guid userId, AccountType type, decimal balance, bool isHidden = false, string name = "Test Account") => new()
    {
        Id = Guid.NewGuid(),
        UserId = userId,
        ConnectionId = Guid.NewGuid(),
        PluggyAccountId = $"acct-{Guid.NewGuid():N}",
        Type = type,
        Subtype = type == AccountType.Credit ? AccountSubtype.CreditCard : AccountSubtype.CheckingAccount,
        Name = name,
        Balance = balance,
        CurrencyCode = "BRL",
        IsHidden = isHidden,
        CreatedAt = DateTime.UtcNow,
        UpdatedAt = DateTime.UtcNow,
    };

    private static CreditCardDetail NewCreditCardDetail(
        Guid accountId, decimal? creditLimit = null, DateTime? balanceDueDate = null) => new()
    {
        Id = Guid.NewGuid(),
        AccountId = accountId,
        CreditLimit = creditLimit,
        BalanceDueDate = balanceDueDate,
        Status = CreditCardStatus.Active,
        CreatedAt = DateTime.UtcNow,
        UpdatedAt = DateTime.UtcNow,
    };

    private static Transaction NewTransaction(
        Guid userId, Guid accountId, DateTime date, decimal amount, TransactionType type,
        bool isTransfer = false, bool isHidden = false,
        TransactionStatus status = TransactionStatus.Posted) => new()
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
        Status = status,
        CategorySource = CategorySource.SystemMapping,
        IsTransfer = isTransfer,
        IsHidden = isHidden,
        CreatedAt = DateTime.UtcNow,
        UpdatedAt = DateTime.UtcNow,
    };

    [Fact]
    public async Task GetCashFlowByAccountAsync_ComputesInflowOutflowNet_ExcludingTransferAndHidden()
    {
        var userId = Guid.NewGuid();
        var periodStart = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc);
        var periodEnd = new DateTime(2026, 6, 30, 23, 59, 59, DateTimeKind.Utc);
        var midPeriod = new DateTime(2026, 6, 15, 12, 0, 0, DateTimeKind.Utc);

        await using var db = CreateDbContext();
        var account = NewAccount(userId, AccountType.Bank, balance: 1000m, name: "Checking");
        db.Accounts.Add(account);
        db.Transactions.AddRange(
            NewTransaction(userId, account.Id, midPeriod, amount: 800m, TransactionType.Credit),
            NewTransaction(userId, account.Id, midPeriod, amount: 300m, TransactionType.Debit),
            // A transfer -- must be excluded from both Inflow and Outflow.
            NewTransaction(userId, account.Id, midPeriod, amount: 999_999m, TransactionType.Credit, isTransfer: true),
            // A hidden expense -- must be excluded from Outflow.
            NewTransaction(userId, account.Id, midPeriod, amount: 777m, TransactionType.Debit, isHidden: true));
        await db.SaveChangesAsync();

        var rows = await CreateQueries().GetCashFlowByAccountAsync(userId, periodStart, periodEnd, CancellationToken.None);

        var row = Assert.Single(rows);
        Assert.Equal(account.Id, row.AccountId);
        Assert.Equal("Checking", row.AccountName);
        Assert.Equal(800m, row.Inflow);
        Assert.Equal(300m, row.Outflow);
        Assert.Equal(500m, row.Net);
    }

    [Fact]
    public async Task GetCashFlowByAccountAsync_ExcludesHiddenAccount_AndPendingTransaction()
    {
        var userId = Guid.NewGuid();
        var periodStart = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc);
        var periodEnd = new DateTime(2026, 6, 30, 23, 59, 59, DateTimeKind.Utc);
        var midPeriod = new DateTime(2026, 6, 15, 12, 0, 0, DateTimeKind.Utc);

        await using var db = CreateDbContext();
        var hiddenAccount = NewAccount(userId, AccountType.Bank, balance: 5000m, isHidden: true);
        db.Accounts.Add(hiddenAccount);
        db.Transactions.AddRange(
            NewTransaction(userId, hiddenAccount.Id, midPeriod, amount: 500m, TransactionType.Credit),
            // Pending -- must not appear in a POSTED-only report.
            NewTransaction(userId, hiddenAccount.Id, midPeriod, amount: 200m, TransactionType.Debit, status: TransactionStatus.Pending));
        await db.SaveChangesAsync();

        var rows = await CreateQueries().GetCashFlowByAccountAsync(userId, periodStart, periodEnd, CancellationToken.None);

        Assert.Empty(rows);
    }

    [Fact]
    public async Task GetCardStatementsByMonthAsync_GroupsByMonth_ExcludingTransferAndHidden()
    {
        var userId = Guid.NewGuid();
        var periodStart = new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc);
        var periodEnd = new DateTime(2026, 6, 30, 23, 59, 59, DateTimeKind.Utc);
        var mayDate = new DateTime(2026, 5, 10, 12, 0, 0, DateTimeKind.Utc);
        var juneDate = new DateTime(2026, 6, 10, 12, 0, 0, DateTimeKind.Utc);
        var dueDate = new DateTime(2026, 7, 5, 0, 0, 0, DateTimeKind.Utc);

        await using var db = CreateDbContext();
        var card = NewAccount(userId, AccountType.Credit, balance: 0m, name: "Visa");
        db.Accounts.Add(card);
        db.CreditCardDetails.Add(NewCreditCardDetail(card.Id, creditLimit: 5000m, balanceDueDate: dueDate));
        db.Transactions.AddRange(
            NewTransaction(userId, card.Id, mayDate, amount: 100m, TransactionType.Debit),
            NewTransaction(userId, card.Id, juneDate, amount: 250m, TransactionType.Debit),
            // Excluded: transfer and hidden.
            NewTransaction(userId, card.Id, juneDate, amount: 10_000m, TransactionType.Debit, isTransfer: true),
            NewTransaction(userId, card.Id, juneDate, amount: 999m, TransactionType.Debit, isHidden: true));
        await db.SaveChangesAsync();

        var rows = await CreateQueries().GetCardStatementsByMonthAsync(userId, periodStart, periodEnd, CancellationToken.None);

        Assert.Equal(2, rows.Count);
        var may = Assert.Single(rows, r => r.Year == 2026 && r.Month == 5);
        Assert.Equal(100m, may.StatementTotal);
        Assert.Equal(5000m, may.CreditLimit);
        Assert.Equal(dueDate, may.BalanceDueDate);
        var june = Assert.Single(rows, r => r.Year == 2026 && r.Month == 6);
        Assert.Equal(250m, june.StatementTotal);
    }

    [Fact]
    public async Task GetCardStatementsByMonthAsync_ExcludesNonCreditAccounts()
    {
        var userId = Guid.NewGuid();
        var periodStart = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc);
        var periodEnd = new DateTime(2026, 6, 30, 23, 59, 59, DateTimeKind.Utc);
        var midPeriod = new DateTime(2026, 6, 15, 12, 0, 0, DateTimeKind.Utc);

        await using var db = CreateDbContext();
        var bankAccount = NewAccount(userId, AccountType.Bank, balance: 1000m);
        db.Accounts.Add(bankAccount);
        db.Transactions.Add(NewTransaction(userId, bankAccount.Id, midPeriod, amount: 100m, TransactionType.Debit));
        await db.SaveChangesAsync();

        var rows = await CreateQueries().GetCardStatementsByMonthAsync(userId, periodStart, periodEnd, CancellationToken.None);

        Assert.Empty(rows);
    }
}
