using Microsoft.EntityFrameworkCore;
using SimpleFinance.Domain.Entities;
using SimpleFinance.Domain.Enums;
using SimpleFinance.Infrastructure.Persistence;

namespace SimpleFinance.Infrastructure.Ai.Assistant;

/// <summary>
/// The outcome of a confirm/cancel transition — mapped by the endpoints to 200 / 404 / 409.
/// <see cref="NotFound"/> = the id is missing or owned by another user (IDOR-safe, T-17-20);
/// <see cref="Conflict"/> = the action is not <see cref="PendingActionStatus.Pending"/> (already
/// decided, or just expired) — the idempotency guard (T-17-19/T-17-22); <see cref="Success"/> = the
/// transition ran (and, for confirm, the single domain write executed exactly once).
/// </summary>
public enum PendingActionOutcome
{
    NotFound,
    Conflict,
    Success,
}

/// <summary>
/// AI-CHAT-04 — the propose/confirm/cancel state machine over <see cref="AiPendingAction"/>. This is
/// the SAFETY SPINE of the assistant's write path: the write-tools (17-06) never mutate a domain table;
/// they call <see cref="ProposeAsync"/> to stage a <see cref="PendingActionStatus.Pending"/> row whose
/// jsonb <see cref="AiPendingAction.Payload"/> captures the proposed change. NOTHING is written until an
/// explicit <c>POST /api/ai/actions/{id}/confirm</c> reaches <see cref="ConfirmAsync"/>, which dispatches
/// by <see cref="AiPendingAction.ActionType"/> to the matching domain service via
/// <see cref="PendingActionExecutor"/> — the only place a write actually happens.
///
/// Clone of <see cref="AiSuggestionService"/>'s load-scoped-then-transition + <c>DecidedAt</c> stamp.
/// Every load filters <c>WHERE Id == id AND UserId == userId</c> so a foreign/missing id simply "isn't
/// found" (no leak, no write). Confirm is idempotent — only a Pending action transitions; a non-Pending
/// (already Confirmed/Cancelled/Expired) action returns <see cref="PendingActionOutcome.Conflict"/> with
/// no second execution. A stale (&gt; <see cref="ExpiryWindow"/>) action is flipped to
/// <see cref="PendingActionStatus.Expired"/> and rejected with Conflict, never executed.
/// </summary>
public sealed class AiPendingActionService(SimpleFinanceDbContext db, PendingActionExecutor executor)
{
    /// <summary>How long a proposal stays confirmable before it is treated as stale (T-17-22).</summary>
    public static readonly TimeSpan ExpiryWindow = TimeSpan.FromHours(24);

    /// <summary>
    /// Stage a propose-only write: insert one <see cref="AiPendingAction"/> with
    /// <see cref="PendingActionStatus.Pending"/> and the validated jsonb <paramref name="payloadJson"/>.
    /// MUTATES NO domain table — this is the whole point of the safety spine (T-17-18). The write-tools
    /// call this after resolving/validating their args; execution waits for an explicit confirm.
    /// </summary>
    public async Task<AiPendingAction> ProposeAsync(
        Guid userId,
        Guid? conversationId,
        PendingActionType actionType,
        string payloadJson,
        CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var action = new AiPendingAction
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            ConversationId = conversationId,
            ActionType = actionType,
            Payload = payloadJson,
            Status = PendingActionStatus.Pending,
            CreatedAt = now,
            UpdatedAt = now,
        };

        db.AiPendingActions.Add(action);
        await db.SaveChangesAsync(ct);
        return action;
    }

    /// <summary>
    /// Confirm and execute a pending action EXACTLY ONCE. Loads the row UserId-scoped (missing/foreign →
    /// <see cref="PendingActionOutcome.NotFound"/>); a non-Pending status → Conflict (idempotent — a
    /// re-confirm never double-applies); an action older than <see cref="ExpiryWindow"/> is stamped
    /// Expired and rejected with Conflict, no write. Otherwise the change is dispatched to the existing
    /// domain service (<see cref="PendingActionExecutor"/>), the action flips to
    /// <see cref="PendingActionStatus.Confirmed"/> + <c>DecidedAt</c>, and a single trailing
    /// <c>SaveChangesAsync</c> commits the status transition (covering the CreateRule/Recategorize
    /// entities staged in this same context).
    /// </summary>
    public async Task<PendingActionOutcome> ConfirmAsync(Guid userId, Guid id, CancellationToken ct)
    {
        var action = await db.AiPendingActions
            .FirstOrDefaultAsync(a => a.Id == id && a.UserId == userId, ct);

        if (action is null)
        {
            return PendingActionOutcome.NotFound;
        }

        if (action.Status != PendingActionStatus.Pending)
        {
            return PendingActionOutcome.Conflict;
        }

        var now = DateTime.UtcNow;

        // Stale-proposal guard (T-17-22): flip to Expired and reject WITHOUT executing.
        if (now - action.CreatedAt > ExpiryWindow)
        {
            action.Status = PendingActionStatus.Expired;
            action.DecidedAt = now;
            action.UpdatedAt = now;
            await db.SaveChangesAsync(ct);
            return PendingActionOutcome.Conflict;
        }

        // The ONLY place a write happens: dispatch by ActionType to the existing domain service. A
        // corrupt/unusable payload returns false (no mutation) and leaves the action Pending → Conflict.
        var applied = await executor.ExecuteAsync(action, userId, ct);
        if (!applied)
        {
            return PendingActionOutcome.Conflict;
        }

        action.Status = PendingActionStatus.Confirmed;
        action.DecidedAt = now;
        action.UpdatedAt = now;

        // Single trailing save: commits the status transition + any entities the executor staged
        // (CreateRule/Recategorize) into this context. Goal/Budget go through their own self-saving
        // services, so a second confirm always finds Status=Confirmed and returns Conflict.
        await db.SaveChangesAsync(ct);
        return PendingActionOutcome.Success;
    }

    /// <summary>
    /// Cancel a pending action: UserId-scoped load (missing/foreign → NotFound); a Pending action flips
    /// to <see cref="PendingActionStatus.Cancelled"/> + <c>DecidedAt</c> with NO domain write; a
    /// non-Pending action → Conflict. A cancelled action can never later be confirmed.
    /// </summary>
    public async Task<PendingActionOutcome> CancelAsync(Guid userId, Guid id, CancellationToken ct)
    {
        var action = await db.AiPendingActions
            .FirstOrDefaultAsync(a => a.Id == id && a.UserId == userId, ct);

        if (action is null)
        {
            return PendingActionOutcome.NotFound;
        }

        if (action.Status != PendingActionStatus.Pending)
        {
            return PendingActionOutcome.Conflict;
        }

        var now = DateTime.UtcNow;
        action.Status = PendingActionStatus.Cancelled;
        action.DecidedAt = now;
        action.UpdatedAt = now;

        await db.SaveChangesAsync(ct);
        return PendingActionOutcome.Success;
    }
}
