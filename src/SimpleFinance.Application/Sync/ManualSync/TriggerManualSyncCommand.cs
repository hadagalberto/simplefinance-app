namespace SimpleFinance.Application.Sync.ManualSync;

/// <summary>
/// Thin DTO shaping the manual-sync debounce check's input (SYNC-03). The actual debounce
/// logic lives directly in SyncEndpoints's handlers -- this is glue over the existing
/// ISyncReconciler, not new business logic (02-06-PLAN.md's Task Sizing guidance: no separate
/// use-case class needed for this phase's scope).
/// </summary>
public sealed record TriggerManualSyncCommand(Guid ConnectionId);
