using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SimpleFinance.Application.Sync;
using SimpleFinance.Domain.Enums;
using SimpleFinance.Infrastructure.Persistence;

namespace SimpleFinance.Infrastructure.Jobs;

/// <summary>
/// The Hangfire-invoked IWebhookProcessor implementation -- resolves a persisted WebhookEvent
/// (queued by WebhookEndpoints) into the SAME ISyncReconciler every other sync trigger calls
/// (SyncTrigger.Webhook), per ARCHITECTURE.md Anti-Pattern 1 (never a second reconciler
/// implementation). Never throws on an unrecognized event type -- Pluggy may add new event types
/// the app must tolerate without crashing (02-RESEARCH.md) -- and treats a missing local
/// Connection as an expected race (e.g. item/created firing before 02-07's POST
/// /api/connections finishes writing the Connection row), not a bug.
/// </summary>
public sealed class WebhookProcessorJob : IWebhookProcessor
{
    // The event types that actually trigger a reconciliation run. Everything else (e.g.
    // item/waiting_user_input, item/login_succeeded, item/error, item/deleted, or any future
    // type Pluggy introduces) is intentionally ignored -- logged, then marked Processed.
    private static readonly HashSet<string> ReconcileEventTypes =
    [
        "item/created",
        "item/updated",
        "transactions/created",
        "transactions/updated",
        "transactions/deleted",
        "connector/status_updated",
    ];

    private readonly SimpleFinanceDbContext _db;
    private readonly ISyncReconciler _reconciler;
    private readonly ILogger<WebhookProcessorJob> _logger;

    public WebhookProcessorJob(SimpleFinanceDbContext db, ISyncReconciler reconciler, ILogger<WebhookProcessorJob> logger)
    {
        _db = db;
        _reconciler = reconciler;
        _logger = logger;
    }

    public async Task ProcessAsync(Guid webhookEventId, CancellationToken ct)
    {
        // A genuinely missing WebhookEvent row is a Hangfire-retry-worthy bug (the id came from
        // the enqueue call itself), not an expected case -- let this throw.
        var webhookEvent = await _db.WebhookEvents.FirstOrDefaultAsync(e => e.Id == webhookEventId, ct)
            ?? throw new InvalidOperationException($"WebhookEvent '{webhookEventId}' was not found.");

        if (ReconcileEventTypes.Contains(webhookEvent.EventType))
        {
            var connection = await _db.Connections
                .FirstOrDefaultAsync(c => c.PluggyItemId == webhookEvent.PluggyItemId, ct);

            if (connection is null)
            {
                // Expected race (e.g. item/created arriving before 02-07's POST
                // /api/connections finishes persisting the Connection row) -- never throw.
                MarkFailed(webhookEvent, $"No matching connection for item {webhookEvent.PluggyItemId}");
                await _db.SaveChangesAsync(ct);
                return;
            }

            try
            {
                await _reconciler.SyncConnectionAsync(connection.Id, SyncTrigger.Webhook, ct);
            }
            catch (Exception ex)
            {
                // SyncConnectionUseCase (02-04) already writes its own SyncLog on failure -- this
                // catch only updates the WebhookEvent's own status, then rethrows so Hangfire's
                // own retry policy applies (never double-handle retries).
                MarkFailed(webhookEvent, ex.Message);
                await _db.SaveChangesAsync(ct);
                throw;
            }
        }
        else
        {
            _logger.LogInformation("Ignoring unhandled webhook event type {EventType}", webhookEvent.EventType);
        }

        webhookEvent.Status = WebhookEventStatus.Processed;
        webhookEvent.ProcessedAt = DateTime.UtcNow;
        webhookEvent.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
    }

    private static void MarkFailed(Domain.Entities.WebhookEvent webhookEvent, string error)
    {
        webhookEvent.Status = WebhookEventStatus.Failed;
        webhookEvent.Error = error;
        webhookEvent.UpdatedAt = DateTime.UtcNow;
    }
}
