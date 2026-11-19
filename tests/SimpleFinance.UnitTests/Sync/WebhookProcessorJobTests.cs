using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using SimpleFinance.Application.Sync;
using SimpleFinance.Domain.Entities;
using SimpleFinance.Domain.Enums;
using SimpleFinance.Infrastructure.Jobs;
using SimpleFinance.Infrastructure.Persistence;

namespace SimpleFinance.UnitTests.Sync;

/// <summary>
/// WebhookProcessorJob is the IWebhookProcessor implementation Hangfire invokes -- it routes a
/// persisted WebhookEvent into the SAME ISyncReconciler every other sync trigger calls
/// (SyncTrigger.Webhook), never throws on an unrecognized event type, and handles the
/// no-matching-connection race (item/created firing before 02-07's POST /api/connections
/// finishes) gracefully rather than as a bug. EF Core InMemory + a hand-built ISyncReconciler
/// fake, mirroring 02-04's own FakeFinancialDataProvider test-double style.
/// </summary>
public class WebhookProcessorJobTests
{
    [Fact]
    public async Task ItemUpdatedEvent_ResolvesConnection_CallsReconcilerOnce_MarksProcessed()
    {
        using var db = CreateDb();
        var connection = SeedConnection(db, "item-123");
        var webhookEvent = SeedWebhookEvent(db, "evt-1", "item/updated", "item-123");
        await db.SaveChangesAsync();

        var reconciler = new FakeSyncReconciler();
        var job = new WebhookProcessorJob(db, reconciler, NullLogger<WebhookProcessorJob>.Instance);

        await job.ProcessAsync(webhookEvent.Id, CancellationToken.None);

        var call = Assert.Single(reconciler.Calls);
        Assert.Equal(connection.Id, call.ConnectionId);
        Assert.Equal(SyncTrigger.Webhook, call.Trigger);

        var reloaded = await db.WebhookEvents.SingleAsync(e => e.Id == webhookEvent.Id);
        Assert.Equal(WebhookEventStatus.Processed, reloaded.Status);
        Assert.NotNull(reloaded.ProcessedAt);
    }

    [Theory]
    [InlineData("transactions/created")]
    [InlineData("transactions/updated")]
    [InlineData("transactions/deleted")]
    [InlineData("item/created")]
    [InlineData("connector/status_updated")]
    public async Task HandledEventTypes_ResolveConnection_CallReconcilerExactlyOnce(string eventType)
    {
        using var db = CreateDb();
        var connection = SeedConnection(db, "item-tx");
        var webhookEvent = SeedWebhookEvent(db, $"evt-{eventType}", eventType, "item-tx");
        await db.SaveChangesAsync();

        var reconciler = new FakeSyncReconciler();
        var job = new WebhookProcessorJob(db, reconciler, NullLogger<WebhookProcessorJob>.Instance);

        await job.ProcessAsync(webhookEvent.Id, CancellationToken.None);

        var call = Assert.Single(reconciler.Calls);
        Assert.Equal(connection.Id, call.ConnectionId);
        Assert.Equal(SyncTrigger.Webhook, call.Trigger);
    }

    [Theory]
    [InlineData("item/waiting_user_input")]
    [InlineData("item/login_succeeded")]
    [InlineData("item/waiting_user_action")]
    [InlineData("item/error")]
    [InlineData("item/deleted")]
    [InlineData("some/future_event_type")]
    public async Task UnhandledEventType_LogsAndIgnores_NeverCallsReconciler_MarksProcessed_NeverThrows(string eventType)
    {
        using var db = CreateDb();
        var webhookEvent = SeedWebhookEvent(db, $"evt-{eventType}", eventType, "item-999");
        await db.SaveChangesAsync();

        var reconciler = new FakeSyncReconciler();
        var job = new WebhookProcessorJob(db, reconciler, NullLogger<WebhookProcessorJob>.Instance);

        await job.ProcessAsync(webhookEvent.Id, CancellationToken.None);

        Assert.Empty(reconciler.Calls);

        var reloaded = await db.WebhookEvents.SingleAsync(e => e.Id == webhookEvent.Id);
        Assert.Equal(WebhookEventStatus.Processed, reloaded.Status);
        Assert.NotNull(reloaded.ProcessedAt);
    }

    [Fact]
    public async Task NoMatchingConnection_MarksFailed_WithDescriptiveError_NeverThrows_NeverCallsReconciler()
    {
        using var db = CreateDb();
        var webhookEvent = SeedWebhookEvent(db, "evt-orphan", "item/updated", "item-does-not-exist");
        await db.SaveChangesAsync();

        var reconciler = new FakeSyncReconciler();
        var job = new WebhookProcessorJob(db, reconciler, NullLogger<WebhookProcessorJob>.Instance);

        // Must not throw -- this is an expected race (item/created firing before the local
        // Connection row exists yet), not a bug.
        await job.ProcessAsync(webhookEvent.Id, CancellationToken.None);

        Assert.Empty(reconciler.Calls);

        var reloaded = await db.WebhookEvents.SingleAsync(e => e.Id == webhookEvent.Id);
        Assert.Equal(WebhookEventStatus.Failed, reloaded.Status);
        Assert.Contains("item-does-not-exist", reloaded.Error);
    }

    [Fact]
    public async Task ReconcilerThrows_MarksFailed_WithExceptionMessage_RethrowsForHangfireRetry()
    {
        using var db = CreateDb();
        SeedConnection(db, "item-boom");
        var webhookEvent = SeedWebhookEvent(db, "evt-boom", "item/updated", "item-boom");
        await db.SaveChangesAsync();

        var reconciler = new FakeSyncReconciler { ThrowOnSync = new InvalidOperationException("boom") };
        var job = new WebhookProcessorJob(db, reconciler, NullLogger<WebhookProcessorJob>.Instance);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => job.ProcessAsync(webhookEvent.Id, CancellationToken.None));
        Assert.Equal("boom", ex.Message);

        var reloaded = await db.WebhookEvents.SingleAsync(e => e.Id == webhookEvent.Id);
        Assert.Equal(WebhookEventStatus.Failed, reloaded.Status);
        Assert.Equal("boom", reloaded.Error);
    }

    [Fact]
    public async Task MissingWebhookEvent_Throws()
    {
        using var db = CreateDb();

        var reconciler = new FakeSyncReconciler();
        var job = new WebhookProcessorJob(db, reconciler, NullLogger<WebhookProcessorJob>.Instance);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => job.ProcessAsync(Guid.NewGuid(), CancellationToken.None));
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

    private static WebhookEvent SeedWebhookEvent(SimpleFinanceDbContext db, string eventId, string eventType, string? pluggyItemId)
    {
        var now = DateTime.UtcNow;
        var webhookEvent = new WebhookEvent
        {
            Id = Guid.NewGuid(),
            EventId = eventId,
            EventType = eventType,
            PluggyItemId = pluggyItemId,
            PayloadJson = "{}",
            ReceivedAt = now,
            Status = WebhookEventStatus.Pending,
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.WebhookEvents.Add(webhookEvent);
        return webhookEvent;
    }

    private sealed class FakeSyncReconciler : ISyncReconciler
    {
        public List<(Guid ConnectionId, SyncTrigger Trigger)> Calls { get; } = [];

        public Exception? ThrowOnSync { get; set; }

        public Task<SyncResult> SyncConnectionAsync(Guid connectionId, SyncTrigger trigger, CancellationToken ct)
        {
            Calls.Add((connectionId, trigger));

            if (ThrowOnSync is not null)
            {
                throw ThrowOnSync;
            }

            return Task.FromResult(new SyncResult(SyncStatus.Success, 0, 0, 0, 0, 0, 0, null));
        }
    }
}
