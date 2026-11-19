namespace SimpleFinance.Application.Common;

/// <summary>
/// Serializes overlapping operations across process/request boundaries via a named distributed
/// lock. SYNC-07's mechanism: <c>SyncConnectionUseCase</c> acquires one keyed by the Connection
/// so a webhook-triggered sync and a concurrent cron/manual sync for the SAME connection never
/// run simultaneously. The real implementation (<c>PostgresAdvisoryLockProvider</c>, 02-04)
/// BLOCKS until the lock is available rather than rejecting — callers get true serialization,
/// not a race-prone try-and-fail pattern (per ARCHITECTURE.md Anti-Pattern 2 / RESEARCH.md's
/// Claude's Discretion note on choosing a Postgres advisory lock over a Hangfire
/// DisableConcurrentExecution attribute, so the reconciler itself enforces serialization
/// regardless of caller).
/// </summary>
public interface IDistributedLockProvider
{
    /// <summary>
    /// Acquires the named lock, blocking until it becomes available. Release by disposing the
    /// returned handle (e.g. <c>await using var handle = await AcquireAsync(key, ct);</c>).
    /// </summary>
    Task<IAsyncDisposable> AcquireAsync(string key, CancellationToken ct);
}
