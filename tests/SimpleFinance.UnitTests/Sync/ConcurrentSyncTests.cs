using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using SimpleFinance.Domain.Entities;
using SimpleFinance.Domain.Enums;
using SimpleFinance.Infrastructure.Jobs;
using SimpleFinance.Infrastructure.Persistence;
using SimpleFinance.Infrastructure.Sync;

namespace SimpleFinance.UnitTests.Sync;

/// <summary>
/// Postgres-backed integration test for SYNC-07 (per 02-04-PLAN.md Task 3) -- advisory locks
/// require a real Postgres connection, not EF InMemory. Requires
/// `docker compose up postgres -d` running locally first (the postgres service maps its port to
/// 127.0.0.1:5433 -- see docker-compose.yml -- specifically so this test can reach it from the
/// host). Connection string resolution: ConnectionStrings__Test env var, else the same default
/// docker-compose's postgres service exposes.
/// </summary>
public class ConcurrentSyncTests
{
    private static string GetConnectionString() =>
        Environment.GetEnvironmentVariable("ConnectionStrings__Test")
        ?? "Host=localhost;Port=5433;Database=simplefinance;Username=simplefinance;Password=changeme";

    private static SimpleFinanceDbContext CreateDbContext() =>
        new(new DbContextOptionsBuilder<SimpleFinanceDbContext>()
            .UseNpgsql(GetConnectionString(), vec => vec.UseVector())
            .Options);

    private static IConfiguration CreateConfiguration() =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:Default"] = GetConnectionString() })
            .Build();

    [Fact]
    public async Task SyncConnectionAsync_TwoConcurrentCallsSameConnection_SerializedNonOverlappingSyncLogWindows()
    {
        var connectionId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var pluggyItemId = $"item-concurrent-{Guid.NewGuid():N}";

        await using (var seedDb = CreateDbContext())
        {
            seedDb.Connections.Add(new Connection
            {
                Id = connectionId,
                UserId = userId,
                PluggyItemId = pluggyItemId,
                Status = ConnectionStatus.Updated,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            });
            await seedDb.SaveChangesAsync();
        }

        async Task RunSyncAsync()
        {
            await using var db = CreateDbContext();
            var provider = new FakeFinancialDataProvider
            {
                Accounts = [],
                // Widens the race window (per 02-04-PLAN.md Task 3's explicit instruction) so
                // serialization is actually exercised rather than accidentally passing on timing
                // luck.
                OnGetAccounts = ct => Task.Delay(200, ct),
            };
            var lockProvider = new PostgresAdvisoryLockProvider(CreateConfiguration());
            var sut = new SyncConnectionUseCase(provider, db, lockProvider, new FakeCategorizationService(), new FakeTransferDetectionService(), new NoOpBackgroundJobClient());
            await sut.SyncConnectionAsync(connectionId, SyncTrigger.Manual, CancellationToken.None);
        }

        var task1 = Task.Run(RunSyncAsync);
        var task2 = Task.Run(RunSyncAsync);
        await Task.WhenAll(task1, task2);

        await using var assertDb = CreateDbContext();
        var logs = await assertDb.SyncLogs
            .Where(l => l.ConnectionId == connectionId)
            .OrderBy(l => l.StartedAt)
            .ToListAsync();

        Assert.Equal(2, logs.Count);
        Assert.NotNull(logs[0].FinishedAt);
        Assert.True(
            logs[1].StartedAt >= logs[0].FinishedAt!.Value,
            $"Expected serialized (non-overlapping) SyncLog windows; " +
            $"first=[{logs[0].StartedAt:O}, {logs[0].FinishedAt:O}], second started={logs[1].StartedAt:O}");
    }
}
