using SimpleFinance.Domain.Enums;

namespace SimpleFinance.Application.Sync;

/// <summary>
/// THE single shared reconciler seam — both the webhook processor (02-05) and
/// DailySyncJob/manual-sync endpoints (02-06) call this exact interface. Per ARCHITECTURE.md
/// Anti-Pattern 1, there must never be a second implementation of this interface; a second
/// implementation would reintroduce the duplicate/missed-transaction risk SYNC-04/SYNC-07 exist
/// to prevent.
/// </summary>
public interface ISyncReconciler
{
    Task<SyncResult> SyncConnectionAsync(Guid connectionId, SyncTrigger trigger, CancellationToken ct);
}
