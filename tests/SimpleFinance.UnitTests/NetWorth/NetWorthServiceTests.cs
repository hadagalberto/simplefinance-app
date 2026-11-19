using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using SimpleFinance.Domain.Entities;
using SimpleFinance.Domain.Enums;
using SimpleFinance.Infrastructure.NetWorth;
using SimpleFinance.Infrastructure.Persistence;
using SimpleFinance.Infrastructure.Reporting;

namespace SimpleFinance.UnitTests.NetWorth;

/// <summary>
/// Postgres-backed integration tests for NetWorthService (06-04) -- NetWorthService delegates
/// every actual aggregate to DashboardQueries (Dapper), which requires a REAL Postgres connection
/// (EF Core's InMemory provider has no ADO.NET connection behind it for Dapper to use). Same
/// connection-string-resolution pattern as DashboardQueriesTests.cs/InvestmentsServiceTests.cs:
/// ConnectionStrings__Test env var, else the docker-compose default at 127.0.0.1:5433. Requires
/// `docker compose up postgres -d` running locally first.
///
/// Every test scopes its rows to a fresh Guid UserId, so tests can run against the same shared
/// database without interfering with each other or requiring cleanup (matches
/// DashboardQueriesTests.cs/InvestmentsServiceTests.cs precedent).
/// </summary>
public class NetWorthServiceTests
{
    private static string GetConnectionString() =>
        Environment.GetEnvironmentVariable("ConnectionStrings__Test")
        ?? "Host=localhost;Port=5433;Database=simplefinance;Username=simplefinance;Password=changeme";

    private static SimpleFinanceDbContext CreateDbContext() =>
        new(new DbContextOptionsBuilder<SimpleFinanceDbContext>()
            .UseNpgsql(GetConnectionString(), vec => vec.UseVector())
            .Options);

    private static NetWorthService CreateService()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Default"] = GetConnectionString(),
            })
            .Build();

        return new NetWorthService(new DashboardQueries(new NpgsqlDbConnectionFactory(configuration)));
    }

    private static NetWorthSnapshot NewSnapshot(Guid userId, DateTime date, decimal netWorth) => new()
    {
        Id = Guid.NewGuid(),
        UserId = userId,
        Date = date,
        TotalAssets = netWorth,
        TotalLiabilities = 0m,
        NetWorth = netWorth,
        AccountsBalance = netWorth,
        InvestmentsBalance = 0m,
        CreditCardDebt = 0m,
        CreatedAt = DateTime.UtcNow,
        UpdatedAt = DateTime.UtcNow,
    };

    private static Account NewAccount(Guid userId, decimal balance) => new()
    {
        Id = Guid.NewGuid(),
        UserId = userId,
        ConnectionId = Guid.NewGuid(),
        PluggyAccountId = $"acct-{Guid.NewGuid():N}",
        Type = AccountType.Bank,
        Subtype = AccountSubtype.CheckingAccount,
        Name = "Test Account",
        Balance = balance,
        CurrencyCode = "BRL",
        IsHidden = false,
        CreatedAt = DateTime.UtcNow,
        UpdatedAt = DateTime.UtcNow,
    };

    // ---- GetSummaryAsync ----

    [Fact]
    public async Task GetSummaryAsync_PassesThroughLiveComposition_NotStaleSnapshot()
    {
        var userId = Guid.NewGuid();
        var today = DateTime.UtcNow.Date;

        await using var db = CreateDbContext();
        db.Accounts.Add(NewAccount(userId, balance: 3000m));
        // A stale snapshot from an earlier run with a DIFFERENT value -- GetSummaryAsync must
        // return the LIVE-computed figure, never this stale row.
        db.NetWorthSnapshots.Add(NewSnapshot(userId, today, netWorth: 999m));
        await db.SaveChangesAsync();

        var result = await CreateService().GetSummaryAsync(userId, CancellationToken.None);

        Assert.Equal(3000m, result.AccountsBalance);
        Assert.Equal(3000m, result.NetWorth);
    }

    [Fact]
    public async Task GetSummaryAsync_NoData_ReturnsZeroSafeSummary()
    {
        var userId = Guid.NewGuid();

        var result = await CreateService().GetSummaryAsync(userId, CancellationToken.None);

        Assert.Equal(0m, result.AccountsBalance);
        Assert.Equal(0m, result.InvestmentsBalance);
        Assert.Equal(0m, result.CreditCardDebt);
        Assert.Equal(0m, result.NetWorth);
    }

    // ---- GetEvolutionAsync ----

    [Fact]
    public async Task GetEvolutionAsync_Days30_ExcludesSnapshotOlderThan30Days()
    {
        var userId = Guid.NewGuid();
        var today = DateTime.UtcNow.Date;

        await using var db = CreateDbContext();
        db.NetWorthSnapshots.AddRange(
            NewSnapshot(userId, today.AddDays(-10), netWorth: 1000m),
            // 40 days old -- must be excluded from a 30-day range.
            NewSnapshot(userId, today.AddDays(-40), netWorth: 5000m));
        await db.SaveChangesAsync();

        var result = await CreateService().GetEvolutionAsync(userId, days: 30, CancellationToken.None);

        var point = Assert.Single(result.Points);
        Assert.Equal(1000m, point.NetWorth);
    }

    [Fact]
    public async Task GetEvolutionAsync_NoSnapshotRows_ReturnsEmptyPoints_NotException()
    {
        var userId = Guid.NewGuid();

        var result = await CreateService().GetEvolutionAsync(userId, days: 30, CancellationToken.None);

        Assert.Empty(result.Points);
    }
}
