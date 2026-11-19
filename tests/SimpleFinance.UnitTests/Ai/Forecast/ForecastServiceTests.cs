using Microsoft.EntityFrameworkCore;
using SimpleFinance.Infrastructure.Ai.Forecast;
using SimpleFinance.Infrastructure.Persistence;
using ForecastEntity = SimpleFinance.Domain.Entities.Forecast;

namespace SimpleFinance.UnitTests.Ai.Forecast;

/// <summary>
/// Behavior tests for <see cref="ForecastService"/> (19-01 Task 2, AI-FCT-01). Runs on the EF InMemory
/// provider. Proves the REPLACE-per-month upsert (a second run for the same <c>(UserId, Month)</c> UPDATES
/// the row rather than inserting a second or freezing the first — the anti-freeze rule), the UserId
/// isolation, and the null-when-missing read.
/// </summary>
public class ForecastServiceTests
{
    private static SimpleFinanceDbContext CreateDbContext() =>
        new(new DbContextOptionsBuilder<SimpleFinanceDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private static readonly DateTime Month = new(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc);

    private static ForecastEntity NewForecast(Guid userId, DateTime month, decimal projectedBalance)
    {
        var now = DateTime.UtcNow;
        return new ForecastEntity
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            Month = month,
            ProjectedIncome = 5000m,
            ProjectedExpense = 3000m,
            ProjectedBalance = projectedBalance,
            BalanceLow = projectedBalance - 200m,
            BalanceHigh = projectedBalance + 200m,
            ByCategoryJson = "{\"food\":800}",
            Method = "recurring+3mo-avg",
            Narrative = null,
            CreatedAt = now,
            UpdatedAt = now,
        };
    }

    [Fact]
    public async Task UpsertAsync_NoExistingRow_InsertsAndGetReturnsIt()
    {
        await using var db = CreateDbContext();
        var service = new ForecastService(db);
        var userId = Guid.NewGuid();

        await service.UpsertAsync(NewForecast(userId, Month, 2000m), CancellationToken.None);

        Assert.Equal(1, await db.Forecasts.CountAsync());
        var fetched = await service.GetAsync(userId, Month, CancellationToken.None);
        Assert.NotNull(fetched);
        Assert.Equal(2000m, fetched!.ProjectedBalance);
    }

    [Fact]
    public async Task UpsertAsync_SameUserAndMonthTwice_ReplacesSingleRowWithSecondRun()
    {
        await using var db = CreateDbContext();
        var service = new ForecastService(db);
        var userId = Guid.NewGuid();

        await service.UpsertAsync(NewForecast(userId, Month, 2000m), CancellationToken.None);
        await service.UpsertAsync(NewForecast(userId, Month, 3500m), CancellationToken.None);

        // Exactly ONE row — the second run overwrote the first, did not insert a duplicate.
        Assert.Equal(1, await db.Forecasts.CountAsync(f => f.UserId == userId && f.Month == Month));
        var fetched = await service.GetAsync(userId, Month, CancellationToken.None);
        Assert.NotNull(fetched);
        // Reflects the SECOND run's numbers, not frozen at the first.
        Assert.Equal(3500m, fetched!.ProjectedBalance);
        Assert.Equal(3700m, fetched.BalanceHigh);
    }

    [Fact]
    public async Task UpsertAsync_DifferentUsersSameMonth_KeepsRowsIsolated()
    {
        await using var db = CreateDbContext();
        var service = new ForecastService(db);
        var userA = Guid.NewGuid();
        var userB = Guid.NewGuid();

        await service.UpsertAsync(NewForecast(userA, Month, 2000m), CancellationToken.None);
        await service.UpsertAsync(NewForecast(userB, Month, 9000m), CancellationToken.None);

        Assert.Equal(2, await db.Forecasts.CountAsync());
        var a = await service.GetAsync(userA, Month, CancellationToken.None);
        var b = await service.GetAsync(userB, Month, CancellationToken.None);
        Assert.Equal(2000m, a!.ProjectedBalance);
        Assert.Equal(9000m, b!.ProjectedBalance);
    }

    [Fact]
    public async Task UpsertAsync_ReplacingUserAForecast_NeverTouchesUserB()
    {
        await using var db = CreateDbContext();
        var service = new ForecastService(db);
        var userA = Guid.NewGuid();
        var userB = Guid.NewGuid();
        await service.UpsertAsync(NewForecast(userA, Month, 2000m), CancellationToken.None);
        await service.UpsertAsync(NewForecast(userB, Month, 9000m), CancellationToken.None);

        // Re-run user A only.
        await service.UpsertAsync(NewForecast(userA, Month, 1000m), CancellationToken.None);

        var b = await service.GetAsync(userB, Month, CancellationToken.None);
        Assert.Equal(9000m, b!.ProjectedBalance);
        Assert.Equal(2, await db.Forecasts.CountAsync());
    }

    [Fact]
    public async Task GetAsync_NoRowForUserAndMonth_ReturnsNull()
    {
        await using var db = CreateDbContext();
        var service = new ForecastService(db);

        var result = await service.GetAsync(Guid.NewGuid(), Month, CancellationToken.None);

        Assert.Null(result);
    }
}
