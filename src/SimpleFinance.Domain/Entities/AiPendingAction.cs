using SimpleFinance.Domain.Common;
using SimpleFinance.Domain.Enums;

namespace SimpleFinance.Domain.Entities;

/// <summary>
/// A propose-only write the assistant surfaced for the user to confirm (AI-CHAT-04). The write-tools
/// (create-rule / set-budget / create-goal / recategorize) never mutate directly — they create one of
/// these with <see cref="Status"/> = <see cref="PendingActionStatus.Pending"/> and the change captured
/// in the jsonb <see cref="Payload"/>. Execution happens ONLY when the user confirms
/// (<c>POST /api/ai/actions/{id}/confirm</c>, 17-06), which dispatches by <see cref="ActionType"/> to the
/// matching domain service and stamps <see cref="DecidedAt"/>. Structural clone of
/// <see cref="AiSuggestion"/>'s jsonb-payload + status + DecidedAt lifecycle. Per-user (inherits
/// <see cref="UserOwnedEntity"/> for UserId/Id/CreatedAt/UpdatedAt/DeletedAt).
/// </summary>
public class AiPendingAction : UserOwnedEntity
{
    /// <summary>The conversation that produced this proposal, if any (nullable — a proposal can outlive its thread).</summary>
    public Guid? ConversationId { get; set; }

    /// <summary>Which kind of write is proposed (CreateRule/SetBudget/CreateGoal/Recategorize).</summary>
    public PendingActionType ActionType { get; set; }

    /// <summary>The proposed change as a compact jsonb document; shape depends on <see cref="ActionType"/>.</summary>
    public string Payload { get; set; } = string.Empty;

    /// <summary>Lifecycle state. Starts <see cref="PendingActionStatus.Pending"/>.</summary>
    public PendingActionStatus Status { get; set; } = PendingActionStatus.Pending;

    /// <summary>When the user confirmed/cancelled this action (17-06). Null while Pending.</summary>
    public DateTime? DecidedAt { get; set; }
}
