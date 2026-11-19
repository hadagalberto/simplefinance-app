using Hangfire;
using Microsoft.EntityFrameworkCore;
using SimpleFinance.Application.Sync;
using SimpleFinance.Domain.Enums;
using SimpleFinance.Infrastructure.Persistence;

namespace SimpleFinance.Api.Endpoints;

/// <summary>
/// SYNC-03's manual sync surface -- per-connection and global, both authenticated (T-02-17,
/// reusing the existing JWT bearer scheme, no new auth mechanism) and server-side debounced
/// (T-02-16) against a configurable cooldown window so rapid repeat "Atualizar agora" clicks
/// never burn the Open Finance monthly quota. Both routes enqueue via Hangfire into the SAME
/// ISyncReconciler every other sync trigger calls (SyncTrigger.Manual) -- never a second,
/// divergent reconciler implementation.
/// </summary>
public static class SyncEndpoints
{
    private const int DefaultCooldownSeconds = 60;

    public static void MapSyncEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/connections").RequireAuthorization();

        group.MapPost("/{id:guid}/sync", TriggerConnectionSyncAsync);
        group.MapPost("/sync", TriggerGlobalSyncAsync);
    }

    private static async Task<IResult> TriggerConnectionSyncAsync(
        Guid id,
        SimpleFinanceDbContext db,
        IBackgroundJobClient backgroundJobClient,
        IConfiguration configuration,
        CancellationToken ct)
    {
        // The DbContext's global soft-delete query filter already excludes soft-deleted
        // Connections -- a missing/soft-deleted id both resolve to null here.
        var connection = await db.Connections.FirstOrDefaultAsync(c => c.Id == id, ct);
        if (connection is null)
        {
            return Results.NotFound();
        }

        var cooldownSeconds = configuration.GetValue("Sync:ManualSyncCooldownSeconds", DefaultCooldownSeconds);
        var retryAfterSeconds = await GetCooldownRemainingSecondsAsync(db, id, cooldownSeconds, ct);

        if (retryAfterSeconds is not null)
        {
            // T-02-16: server-side debounce -- reject before ever enqueuing a job.
            return Results.Json(
                new { error = "sync_in_cooldown", retryAfterSeconds },
                statusCode: StatusCodes.Status429TooManyRequests);
        }

        backgroundJobClient.Enqueue<ISyncReconciler>(r => r.SyncConnectionAsync(id, SyncTrigger.Manual, CancellationToken.None));

        return Results.Accepted();
    }

    private static async Task<IResult> TriggerGlobalSyncAsync(
        SimpleFinanceDbContext db,
        IBackgroundJobClient backgroundJobClient,
        IConfiguration configuration,
        CancellationToken ct)
    {
        var cooldownSeconds = configuration.GetValue("Sync:ManualSyncCooldownSeconds", DefaultCooldownSeconds);
        var connections = await db.Connections.ToListAsync(ct);

        var enqueued = 0;
        var skipped = 0;

        foreach (var connection in connections)
        {
            var retryAfterSeconds = await GetCooldownRemainingSecondsAsync(db, connection.Id, cooldownSeconds, ct);

            if (retryAfterSeconds is not null)
            {
                skipped++;
                continue;
            }

            backgroundJobClient.Enqueue<ISyncReconciler>(
                r => r.SyncConnectionAsync(connection.Id, SyncTrigger.Manual, CancellationToken.None));
            enqueued++;
        }

        return Results.Accepted(value: new { enqueued, skipped });
    }

    /// <summary>
    /// Returns the remaining cooldown in whole seconds (rounded up) if the most recent SyncLog
    /// row for this connection (any trigger, not just Manual) started within the cooldown
    /// window, or null if a sync may proceed. Identical debounce check for both routes.
    /// </summary>
    private static async Task<int?> GetCooldownRemainingSecondsAsync(
        SimpleFinanceDbContext db, Guid connectionId, int cooldownSeconds, CancellationToken ct)
    {
        var lastSync = await db.SyncLogs
            .Where(l => l.ConnectionId == connectionId)
            .OrderByDescending(l => l.StartedAt)
            .FirstOrDefaultAsync(ct);

        if (lastSync is null)
        {
            return null;
        }

        var elapsedSeconds = (DateTime.UtcNow - lastSync.StartedAt).TotalSeconds;
        var remainingSeconds = cooldownSeconds - elapsedSeconds;

        return remainingSeconds > 0 ? (int)Math.Ceiling(remainingSeconds) : null;
    }
}
