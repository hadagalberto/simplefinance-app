namespace SimpleFinance.Application.Sync;

/// <summary>
/// Post-sync enrichment step (TXN-05) that flags transactions between the user's own accounts
/// as internal transfers. Layers two signals by strength: Pluggy <c>paymentData</c> (payer/
/// receiver document match across two of the user's own accounts) as the primary signal, falling
/// back to an opposite-sign/same-|amount|/date-window heuristic when paymentData is absent or
/// unparseable. Idempotent -- re-running with unchanged data flags zero additional rows -- and
/// never touches a transaction with <c>IsTransferManualOverride=true</c> (a user's explicit
/// PATCH .../transfer choice always wins).
/// </summary>
public interface ITransferDetectionService
{
    /// <summary>
    /// Runs both detection signals for every currently-unflagged, non-overridden transaction
    /// belonging to <paramref name="userId"/>. Returns the count of transactions newly flagged
    /// (0 on a fully idempotent re-run).
    /// </summary>
    Task<int> DetectTransfersForUserAsync(Guid userId, CancellationToken ct);
}
