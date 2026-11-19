using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using SimpleFinance.Application.Sync;
using SimpleFinance.Domain.Entities;
using SimpleFinance.Domain.Enums;
using SimpleFinance.Infrastructure.Jobs;
using SimpleFinance.Infrastructure.Persistence;

namespace SimpleFinance.UnitTests.Sync;

/// <summary>
/// DailySyncJob (SYNC-02) is the cron backstop -- iterates every active Connection and calls the
/// SAME ISyncReconciler every other sync trigger calls (SyncTrigger.Cron), isolating
/// per-connection failures (T-02-18) and respecting the global soft-delete query filter without
/// ever calling IgnoreQueryFilters. EF Core InMemory + a hand-built ISyncReconciler fake,
/// mirroring 02-05's WebhookProcessorJobTests test-double style.
/// </summary>
public class DailySyncJobTests
{
    [Fact]
    public async Task ExecuteAsync_ThreeActiveConnections_CallsReconcilerOncePerConnection_WithCronTrigger()
    {
        using var db = CreateDb();
        var c1 = SeedConnection(db, "item-1");
        var c2 = SeedConnection(db, "item-2");
        var c3 = SeedConnection(db, "item-3");
        await db.SaveChangesAsync();

        var reconciler = new FakeSyncReconciler();
        var job = new DailySyncJob(db, reconciler, NullLogger<DailySyncJob>.Instance);

        await job.ExecuteAsync(CancellationToken.None);

        Assert.Equal(3, reconciler.Calls.Count);
        Assert.All(reconciler.Calls, call => Assert.Equal(SyncTrigger.Cron, call.Trigger));
        Assert.Contains(reconciler.Calls, call => call.ConnectionId == c1.Id);
        Assert.Contains(reconciler.Calls, call => call.ConnectionId == c2.Id);
        Assert.Contains(reconciler.Calls, call => call.ConnectionId == c3.Id);
    }

    [Fact]
    public async Task ExecuteAsync_OneConnectionThrows_OtherTwoStillCalled_BatchNeverAborts()
    {
        using var db = CreateDb();
        var c1 = SeedConnection(db, "item-1");
        var c2 = SeedConnection(db, "item-2");
        var c3 = SeedConnection(db, "item-3");
        await db.SaveChangesAsync();

        var reconciler = new FakeSyncReconciler { ThrowForConnectionId = c2.Id };
        var job = new DailySyncJob(db, reconciler, NullLogger<DailySyncJob>.Instance);

        // Must not throw -- a single connection's reconciler failure is caught and logged, never
        // allowed to abort the batch.
        await job.ExecuteAsync(CancellationToken.None);

        Assert.Equal(3, reconciler.Calls.Count);
        Assert.Contains(reconciler.Calls, call => call.ConnectionId == c1.Id);
        Assert.Contains(reconciler.Calls, call => call.ConnectionId == c2.Id);
        Assert.Contains(reconciler.Calls, call => call.ConnectionId == c3.Id);
    }

    [Fact]
    public async Task ExecuteAsync_SoftDeletedConnection_ExcludedFromBatch_NoIgnoreQueryFiltersBypass()
    {
        using var db = CreateDb();
        var active = SeedConnection(db, "item-active");
        var deleted = SeedConnection(db, "item-deleted");
        deleted.DeletedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();

        var reconciler = new FakeSyncReconciler();
        var job = new DailySyncJob(db, reconciler, NullLogger<DailySyncJob>.Instance);

        await job.ExecuteAsync(CancellationToken.None);

        var call = Assert.Single(reconciler.Calls);
        Assert.Equal(active.Id, call.ConnectionId);
    }

    private static SimpleFinanceDbContext CreateDb()
    {
        var options = new DbContextOptionsBuilder<SimpleFinanceDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;
        return new SimpleFinanceDbContext(options);
    }

    private static Connection SeedConnection(SimpleFinanceDbContext db, string pluggyItemId)
    {
        var now = DateTime.UtcNow;
        var connection = new Connection
        {
            Id = Guid.NewGuid(),
            UserId = Guid.NewGuid(),
            PluggyItemId = pluggyItemId,
            Status = ConnectionStatus.Updated,
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.Connections.Add(connection);
        return connection;
    }

    private sealed class FakeSyncReconciler : ISyncReconciler
    {
        public List<(Guid ConnectionId, SyncTrigger Trigger)> Calls { get; } = [];

        /// <summary>When set, SyncConnectionAsync throws for exactly this connection id --
        /// exercises the per-connection failure-isolation path (T-02-18).</summary>
        public Guid? ThrowForConnectionId { get; set; }

        public Task<SyncResult> SyncConnectionAsync(Guid connectionId, SyncTrigger trigger, CancellationToken ct)
        {
            Calls.Add((connectionId, trigger));

            if (ThrowForConnectionId == connectionId)
            {
                throw new InvalidOperationException("simulated reconciler failure");
            }

            return Task.FromResult(new SyncResult(SyncStatus.Success, 0, 0, 0, 0, 0, 0, null));
        }
    }
}
