using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SimpleFinance.Application.Sync;
using SimpleFinance.Domain.Enums;
using SimpleFinance.Infrastructure.Persistence;

namespace SimpleFinance.Infrastructure.Jobs;

/// <summary>
/// SYNC-02's cron backstop: a thin Hangfire-invoked adapter (ARCHITECTURE.md Anti-Pattern 3)
/// that iterates every active Connection and calls the SAME ISyncReconciler every other sync
/// trigger calls (SyncTrigger.Cron) -- never a second, divergent reconciler implementation.
/// Per Pluggy's own guidance (02-RESEARCH.md: "if you need item updates on a daily basis, you
/// must use our auto-sync feature"), this job is a safety net for missed webhooks, not the
/// primary sync mechanism. Scheduled via RecurringJob.AddOrUpdate in Program.cs using a
/// configurable cron expression (Sync:DailySyncCronExpression, default 06:00/18:00 server time).
/// A single connection's reconciler failure is caught and logged, never allowed to abort the
/// batch -- every other connection must still be attempted (T-02-18).
/// </summary>
public sealed class DailySyncJob
{
    private readonly SimpleFinanceDbContext _db;
    private readonly ISyncReconciler _reconciler;
    private readonly ILogger<DailySyncJob> _logger;

    public DailySyncJob(SimpleFinanceDbContext db, ISyncReconciler reconciler, ILogger<DailySyncJob> logger)
    {
        _db = db;
        _reconciler = reconciler;
        _logger = logger;
    }

    public async Task ExecuteAsync(CancellationToken ct)
    {
        // The DbContext's global soft-delete query filter (FND-04) already excludes
        // DeletedAt != null Connections -- IgnoreQueryFilters is never called here.
        var connections = await _db.Connections.ToListAsync(ct);

        foreach (var connection in connections)
        {
            try
            {
                await _reconciler.SyncConnectionAsync(connection.Id, SyncTrigger.Cron, ct);
            }
            catch (Exception ex)
            {
                // T-02-18: never let one connection's failure abort the batch --
                // SyncConnectionUseCase (02-04) already wrote its own failure SyncLog; this just
                // logs structured context and moves on to the next connection.
                _logger.LogError(ex, "DailySyncJob failed to sync connection {ConnectionId}", connection.Id);
            }
        }
    }
}
