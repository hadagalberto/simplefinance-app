using System.Globalization;
using CsvHelper;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using SimpleFinance.Domain.Entities;
using SimpleFinance.Domain.Enums;
using SimpleFinance.Infrastructure.Export;
using SimpleFinance.Infrastructure.Persistence;
using SimpleFinance.Infrastructure.Recurring;
using SimpleFinance.Infrastructure.Reporting;
using SimpleFinance.Infrastructure.Reports;

namespace SimpleFinance.UnitTests.Export;

/// <summary>
/// Postgres-backed integration tests for ExportService (07-06) -- report-table building depends
/// on ReportsService, which itself depends on DashboardQueries/ReportQueries (Dapper,
/// real-Postgres-only), exactly like ReportsServiceTests.cs (127.0.0.1:5433,
/// ConnectionStrings__Test env var else the docker-compose default). Requires
/// `docker compose up postgres -d` running locally first.
///
/// Every test scopes its rows to a fresh Guid UserId, so tests can run against the same shared
/// database without interfering with each other or requiring cleanup.
/// </summary>
public class ExportServiceTests
{
    private static string GetConnectionString() =>
        Environment.GetEnvironmentVariable("ConnectionStrings__Test")
        ?? "Host=localhost;Port=5433;Database=simplefinance;Username=simplefinance;Password=changeme";

    private static SimpleFinanceDbContext CreateDbContext() =>
        new(new DbContextOptionsBuilder<SimpleFinanceDbContext>()
            .UseNpgsql(GetConnectionString(), vec => vec.UseVector())
            .Options);

    private static ReportsService CreateReportsService(SimpleFinanceDbContext db)
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

    private static Account NewAccount(Guid userId, AccountType type, decimal balance) => new()
    {
        Id = Guid.NewGuid(),
        UserId = userId,
        ConnectionId = Guid.NewGuid(),
        PluggyAccountId = $"acct-{Guid.NewGuid():N}",
        Type = type,
        Subtype = type == AccountType.Credit ? AccountSubtype.CreditCard : AccountSubtype.CheckingAccount,
        Name = "Checking",
        Balance = balance,
        CurrencyCode = "BRL",
        CreatedAt = DateTime.UtcNow,
        UpdatedAt = DateTime.UtcNow,
    };

    private static Transaction NewTransaction(
        Guid userId, Guid accountId, DateTime date, decimal amount, TransactionType type,
        string description = "Test Transaction", bool isTransfer = false, bool isHidden = false,
        Guid? categoryId = null) => new()
    {
        Id = Guid.NewGuid(),
        UserId = userId,
        AccountId = accountId,
        PluggyTransactionId = $"txn-{Guid.NewGuid():N}",
        Date = date,
        Description = description,
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

    [Fact]
    public async Task BuildTransactionsTableAsync_NoTypeFilter_ExcludesTransferAndHidden()
    {
        var userId = Guid.NewGuid();
        var date = new DateTime(2026, 6, 15, 12, 0, 0, DateTimeKind.Utc);

        await using var db = CreateDbContext();
        var account = NewAccount(userId, AccountType.Bank, balance: 1000m);
        db.Accounts.Add(account);
        db.Transactions.AddRange(
            NewTransaction(userId, account.Id, date, amount: 100m, TransactionType.Debit, description: "Groceries"),
            // Must be excluded: an internal transfer between the user's own accounts.
            NewTransaction(userId, account.Id, date, amount: 999m, TransactionType.Credit, description: "Transfer In", isTransfer: true),
            // Must be excluded: explicitly hidden from reports.
            NewTransaction(userId, account.Id, date, amount: 50m, TransactionType.Debit, description: "Hidden Purchase", isHidden: true));
        await db.SaveChangesAsync();

        var service = new ExportService(db);
        var filters = new TransactionExportFilters(null, null, null, null, null, null, null, null, null);
        var table = await service.BuildTransactionsTableAsync(userId, filters, CancellationToken.None);

        Assert.Equal(["Date", "Description", "Merchant", "Account", "Category", "Type", "Amount", "Status", "Transfer", "Hidden"], table.Headers);
        var row = Assert.Single(table.Rows);
        Assert.Equal("Groceries", row[1]);
        Assert.DoesNotContain(table.Rows, r => r[1] == "Transfer In");
        Assert.DoesNotContain(table.Rows, r => r[1] == "Hidden Purchase");
    }

    [Fact]
    public async Task BuildTransactionsTableAsync_TypeTransferFilter_IncludesOnlyTransfers()
    {
        var userId = Guid.NewGuid();
        var date = new DateTime(2026, 6, 15, 12, 0, 0, DateTimeKind.Utc);

        await using var db = CreateDbContext();
        var account = NewAccount(userId, AccountType.Bank, balance: 1000m);
        db.Accounts.Add(account);
        db.Transactions.AddRange(
            NewTransaction(userId, account.Id, date, amount: 100m, TransactionType.Debit, description: "Groceries"),
            NewTransaction(userId, account.Id, date, amount: 999m, TransactionType.Credit, description: "Transfer In", isTransfer: true));
        await db.SaveChangesAsync();

        var service = new ExportService(db);
        var filters = new TransactionExportFilters(null, null, null, null, "transfer", null, null, null, null);
        var table = await service.BuildTransactionsTableAsync(userId, filters, CancellationToken.None);

        var row = Assert.Single(table.Rows);
        Assert.Equal("Transfer In", row[1]);
    }

    [Fact]
    public async Task BuildTransactionsTableAsync_FormatsAmountAndDateInvariantCulture()
    {
        var userId = Guid.NewGuid();
        var date = new DateTime(2026, 6, 15, 12, 0, 0, DateTimeKind.Utc);

        await using var db = CreateDbContext();
        var account = NewAccount(userId, AccountType.Bank, balance: 1000m);
        db.Accounts.Add(account);
        db.Transactions.Add(NewTransaction(userId, account.Id, date, amount: 1234.5m, TransactionType.Debit));
        await db.SaveChangesAsync();

        var service = new ExportService(db);
        var filters = new TransactionExportFilters(null, null, null, null, null, null, null, null, null);
        var table = await service.BuildTransactionsTableAsync(userId, filters, CancellationToken.None);

        var row = Assert.Single(table.Rows);
        Assert.Equal("2026-06-15", row[0]);
        Assert.Equal("1234.50", row[6]);
    }

    [Fact]
    public async Task BuildReportTableAsync_SpendingByCategory_ReturnsExpectedHeadersAndRow()
    {
        var userId = Guid.NewGuid();
        var monthDate = new DateTime(2026, 6, 15, 12, 0, 0, DateTimeKind.Utc);

        await using var db = CreateDbContext();
        var account = NewAccount(userId, AccountType.Bank, balance: 1000m);
        var category = NewCategory(userId, "Groceries");
        db.Accounts.Add(account);
        db.Categories.Add(category);
        db.Transactions.Add(NewTransaction(userId, account.Id, monthDate, amount: 80m, TransactionType.Debit, categoryId: category.Id));
        await db.SaveChangesAsync();

        var exportService = new ExportService(db);
        var reportsService = CreateReportsService(db);
        var query = new ReportExportQuery(
            new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 6, 30, 23, 59, 59, DateTimeKind.Utc),
            null, null, null);

        var table = await exportService.BuildReportTableAsync("spending-by-category", reportsService, userId, query, CancellationToken.None);

        Assert.Equal(["Category", "Amount"], table.Headers);
        var row = Assert.Single(table.Rows);
        Assert.Equal("Groceries", row[0]);
        Assert.Equal("80.00", row[1]);
    }

    [Fact]
    public async Task BuildReportTableAsync_UnknownReportType_ThrowsArgumentException()
    {
        var userId = Guid.NewGuid();
        await using var db = CreateDbContext();
        var exportService = new ExportService(db);
        var reportsService = CreateReportsService(db);
        var query = new ReportExportQuery(null, null, null, null, null);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            exportService.BuildReportTableAsync("not-a-real-report", reportsService, userId, query, CancellationToken.None));
    }

    [Fact]
    public async Task BuildReportTableAsync_Recurring_ReturnsEmptyTable_NotThrow()
    {
        var userId = Guid.NewGuid();
        await using var db = CreateDbContext();
        var exportService = new ExportService(db);
        var reportsService = CreateReportsService(db);
        var query = new ReportExportQuery(null, null, null, null, null);

        var table = await exportService.BuildReportTableAsync("recurring", reportsService, userId, query, CancellationToken.None);

        Assert.Equal(
            ["DisplayName", "Category", "AverageAmount", "Frequency", "LastSeenDate", "NextExpectedDate", "YearlyTotal", "IsSubscription", "IsActive"],
            table.Headers);
        Assert.Empty(table.Rows);
    }

    [Fact]
    public void WriteCsv_ProducesHeaderRow_ThenDataRows_ReadableByCsvHelper()
    {
        var table = new ExportTable(
            ["Date", "Description", "Amount"],
            [
                ["2026-06-01", "Groceries", "80.00"],
                ["2026-06-02", "Salary", "5000.00"],
            ]);

        using var db = CreateDbContext();
        var service = new ExportService(db);
        using var stream = new MemoryStream();

        service.WriteCsv(table, stream);
        stream.Position = 0;

        using var reader = new StreamReader(stream);
        using var csv = new CsvReader(reader, CultureInfo.InvariantCulture);
        Assert.True(csv.Read());
        csv.ReadHeader();
        Assert.NotNull(csv.HeaderRecord);
        Assert.Equal(["Date", "Description", "Amount"], csv.HeaderRecord);

        Assert.True(csv.Read());
        Assert.Equal("Groceries", csv.GetField(1));
        Assert.Equal("80.00", csv.GetField(2));

        Assert.True(csv.Read());
        Assert.Equal("Salary", csv.GetField(1));
        Assert.Equal("5000.00", csv.GetField(2));

        Assert.False(csv.Read());
    }
}
