using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using SimpleFinance.Domain.Entities;
using SimpleFinance.Domain.Enums;
using SimpleFinance.Infrastructure.Persistence;
using SimpleFinance.Infrastructure.Reporting;

namespace SimpleFinance.UnitTests.Reporting;

/// <summary>
/// Postgres-backed integration tests for InvestmentQueries (06-02) -- Dapper's raw SQL isn't
/// exercised by EF Core's InMemory provider, so these run against a real local Postgres, exactly
/// like DashboardQueriesTests.cs (127.0.0.1:5433, ConnectionStrings__Test env var else the
/// docker-compose default). Requires `docker compose up postgres -d` running locally first.
///
/// Every test scopes its rows to a fresh Guid UserId, so tests can run against the same shared
/// database without interfering with each other or requiring cleanup.
/// </summary>
public class InvestmentQueriesTests
{
    private static string GetConnectionString() =>
        Environment.GetEnvironmentVariable("ConnectionStrings__Test")
        ?? "Host=localhost;Port=5433;Database=simplefinance;Username=simplefinance;Password=changeme";

    private static SimpleFinanceDbContext CreateDbContext() =>
        new(new DbContextOptionsBuilder<SimpleFinanceDbContext>()
            .UseNpgsql(GetConnectionString(), vec => vec.UseVector())
            .Options);

    private static InvestmentQueries CreateQueries()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Default"] = GetConnectionString(),
            })
            .Build();

        return new InvestmentQueries(new NpgsqlDbConnectionFactory(configuration));
    }

    private static Investment NewInvestment(
        Guid userId,
        InvestmentType type = InvestmentType.FixedIncome,
        string? name = null,
        string? institution = null,
        string? issuerName = null,
        decimal? value = null,
        decimal? amountInvested = null,
        decimal? annualRate = null,
        decimal? lastMonthRate = null,
        DateTime? dueDate = null,
        string? currencyCode = null,
        DateTime? deletedAt = null) => new()
    {
        Id = Guid.NewGuid(),
        UserId = userId,
        ConnectionId = Guid.NewGuid(),
        PluggyInvestmentId = $"inv-{Guid.NewGuid():N}",
        Name = name ?? "Test Investment",
        Type = type,
        Institution = institution,
        IssuerName = issuerName,
        Value = value,
        AmountInvested = amountInvested,
        AnnualRate = annualRate,
        LastMonthRate = lastMonthRate,
        DueDate = dueDate,
        CurrencyCode = currencyCode,
        DeletedAt = deletedAt,
        CreatedAt = DateTime.UtcNow,
        UpdatedAt = DateTime.UtcNow,
    };

    // ---- GetPositionsAsync ----

    [Fact]
    public async Task GetPositionsAsync_AllNullableFieldsNull_ReturnsRowWithoutThrowing()
    {
        var userId = Guid.NewGuid();

        await using var db = CreateDbContext();
        db.Investments.Add(NewInvestment(userId, name: "Bare Investment"));
        await db.SaveChangesAsync();

        var rows = await CreateQueries().GetPositionsAsync(userId, CancellationToken.None);

        var row = Assert.Single(rows);
        Assert.Equal("Bare Investment", row.Name);
        Assert.Null(row.Value);
        Assert.Null(row.AmountInvested);
        Assert.Null(row.AnnualRate);
        Assert.Null(row.LastMonthRate);
        Assert.Null(row.DueDate);
        Assert.Null(row.CurrencyCode);
        Assert.Null(row.Institution);
        Assert.Null(row.IssuerName);
    }

    [Fact]
    public async Task GetPositionsAsync_OrdersByCurrentValueDescending()
    {
        var userId = Guid.NewGuid();

        await using var db = CreateDbContext();
        var lower = NewInvestment(userId, name: "Lower", value: 100m);
        var higher = NewInvestment(userId, name: "Higher", value: 900m);
        db.Investments.AddRange(lower, higher);
        await db.SaveChangesAsync();

        var rows = await CreateQueries().GetPositionsAsync(userId, CancellationToken.None);

        Assert.Equal(2, rows.Count);
        Assert.Equal("Higher", rows[0].Name);
        Assert.Equal("Lower", rows[1].Name);
    }

    [Fact]
    public async Task GetPositionsAsync_ExcludesSoftDeleted()
    {
        var userId = Guid.NewGuid();

        await using var db = CreateDbContext();
        db.Investments.AddRange(
            NewInvestment(userId, name: "Active", value: 100m),
            NewInvestment(userId, name: "Deleted", value: 500m, deletedAt: DateTime.UtcNow));
        await db.SaveChangesAsync();

        var rows = await CreateQueries().GetPositionsAsync(userId, CancellationToken.None);

        var row = Assert.Single(rows);
        Assert.Equal("Active", row.Name);
    }

    [Fact]
    public async Task GetPositionsAsync_ExcludesOtherUsers()
    {
        var userId = Guid.NewGuid();
        var otherUserId = Guid.NewGuid();

        await using var db = CreateDbContext();
        db.Investments.AddRange(
            NewInvestment(userId, name: "Mine", value: 100m),
            NewInvestment(otherUserId, name: "TheirsNotMine", value: 500m));
        await db.SaveChangesAsync();

        var rows = await CreateQueries().GetPositionsAsync(userId, CancellationToken.None);

        var row = Assert.Single(rows);
        Assert.Equal("Mine", row.Name);
    }

    // ---- GetTotalsAsync ----

    [Fact]
    public async Task GetTotalsAsync_NullContributionsTreatedAsZero()
    {
        var userId = Guid.NewGuid();

        await using var db = CreateDbContext();
        db.Investments.AddRange(
            NewInvestment(userId, value: 1000m, amountInvested: 800m),
            NewInvestment(userId, value: null, amountInvested: null),
            NewInvestment(userId, value: 500m, amountInvested: null));
        await db.SaveChangesAsync();

        var totals = await CreateQueries().GetTotalsAsync(userId, CancellationToken.None);

        Assert.Equal(800m, totals.TotalInvested);
        Assert.Equal(1500m, totals.TotalCurrentValue);
    }

    [Fact]
    public async Task GetTotalsAsync_NoInvestments_ReturnsZeroTotals_NotThrow()
    {
        var userId = Guid.NewGuid();

        var totals = await CreateQueries().GetTotalsAsync(userId, CancellationToken.None);

        Assert.Equal(0m, totals.TotalInvested);
        Assert.Equal(0m, totals.TotalCurrentValue);
    }

    // ---- GetDistributionByTypeAsync ----

    [Fact]
    public async Task GetDistributionByTypeAsync_SumsSameType_IntoOneRow()
    {
        var userId = Guid.NewGuid();

        await using var db = CreateDbContext();
        db.Investments.AddRange(
            NewInvestment(userId, type: InvestmentType.Equity, value: 300m),
            NewInvestment(userId, type: InvestmentType.Equity, value: 200m));
        await db.SaveChangesAsync();

        var rows = await CreateQueries().GetDistributionByTypeAsync(userId, CancellationToken.None);

        var row = Assert.Single(rows);
        Assert.Equal(InvestmentType.Equity, row.Type);
        Assert.Equal(500m, row.Value);
    }

    [Fact]
    public async Task GetDistributionByTypeAsync_NullValueContributesZero_NotExcluded()
    {
        var userId = Guid.NewGuid();

        await using var db = CreateDbContext();
        db.Investments.Add(NewInvestment(userId, type: InvestmentType.Crypto, value: null));
        await db.SaveChangesAsync();

        var rows = await CreateQueries().GetDistributionByTypeAsync(userId, CancellationToken.None);

        var row = Assert.Single(rows);
        Assert.Equal(InvestmentType.Crypto, row.Type);
        Assert.Equal(0m, row.Value);
    }

    [Fact]
    public async Task GetDistributionByTypeAsync_NoInvestments_ReturnsEmptyList_NotThrow()
    {
        var userId = Guid.NewGuid();

        var rows = await CreateQueries().GetDistributionByTypeAsync(userId, CancellationToken.None);

        Assert.Empty(rows);
    }

    // ---- GetDistributionByIssuerAsync ----

    [Fact]
    public async Task GetDistributionByIssuerAsync_GroupsByIssuerName_IgnoringInstitution()
    {
        var userId = Guid.NewGuid();

        await using var db = CreateDbContext();
        db.Investments.Add(NewInvestment(
            userId, issuerName: "XP Investimentos", institution: "Some Bank", value: 700m));
        await db.SaveChangesAsync();

        var rows = await CreateQueries().GetDistributionByIssuerAsync(userId, CancellationToken.None);

        var row = Assert.Single(rows);
        Assert.Equal("XP Investimentos", row.IssuerLabel);
        Assert.Equal(700m, row.Value);
    }

    [Fact]
    public async Task GetDistributionByIssuerAsync_NullIssuerName_FallsBackToInstitution()
    {
        var userId = Guid.NewGuid();

        await using var db = CreateDbContext();
        db.Investments.Add(NewInvestment(
            userId, issuerName: null, institution: "Nubank", value: 400m));
        await db.SaveChangesAsync();

        var rows = await CreateQueries().GetDistributionByIssuerAsync(userId, CancellationToken.None);

        var row = Assert.Single(rows);
        Assert.Equal("Nubank", row.IssuerLabel);
        Assert.Equal(400m, row.Value);
    }

    [Fact]
    public async Task GetDistributionByIssuerAsync_BothNull_FallsBackToUnknown()
    {
        var userId = Guid.NewGuid();

        await using var db = CreateDbContext();
        db.Investments.Add(NewInvestment(
            userId, issuerName: null, institution: null, value: 250m));
        await db.SaveChangesAsync();

        var rows = await CreateQueries().GetDistributionByIssuerAsync(userId, CancellationToken.None);

        var row = Assert.Single(rows);
        Assert.Equal("Unknown", row.IssuerLabel);
        Assert.Equal(250m, row.Value);
    }

    [Fact]
    public async Task GetDistributionByIssuerAsync_TwoInvestmentsFallBackToSameInstitution_SumIntoOneRow()
    {
        var userId = Guid.NewGuid();

        await using var db = CreateDbContext();
        db.Investments.AddRange(
            NewInvestment(userId, issuerName: null, institution: "Itau", value: 100m),
            NewInvestment(userId, issuerName: null, institution: "Itau", value: 150m));
        await db.SaveChangesAsync();

        var rows = await CreateQueries().GetDistributionByIssuerAsync(userId, CancellationToken.None);

        var row = Assert.Single(rows);
        Assert.Equal("Itau", row.IssuerLabel);
        Assert.Equal(250m, row.Value);
    }
}
