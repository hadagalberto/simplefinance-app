namespace SimpleFinance.Domain.Enums;

/// <summary>
/// The author of an <see cref="Entities.AiMessage"/> turn, mirroring the LLM chat roles
/// (<c>User</c> asks, <c>Assistant</c> answers, <c>Tool</c> carries a tool-call result back into the
/// thread). Matches the string role names already used by <c>ILlmClient</c>/<c>LlmMessage.Role</c>.
/// Persisted as <c>int</c> — APPEND-ONLY, never reorder.
/// </summary>
public enum MessageRole
{
    User,
    Assistant,
    Tool
}

/// <summary>
/// What kind of write an <see cref="Entities.AiPendingAction"/> proposes (AI-CHAT-04). The assistant's
/// write-tools never mutate directly — they queue one of these for the user to confirm, at which point
/// the confirm endpoint (17-06) dispatches to the matching domain service. Maps 1:1 to the reserved
/// <see cref="AiSuggestionType.Rule"/>/<see cref="AiSuggestionType.Budget"/>/<see cref="AiSuggestionType.Goal"/>
/// buckets. Persisted as <c>int</c> — APPEND-ONLY, never reorder.
/// </summary>
public enum PendingActionType
{
    CreateRule,
    SetBudget,
    CreateGoal,
    Recategorize
}

/// <summary>
/// Lifecycle of an <see cref="Entities.AiPendingAction"/>. A proposed write starts <see cref="Pending"/>;
/// the user's confirm/cancel (17-06) moves it to <see cref="Confirmed"/>/<see cref="Cancelled"/> and
/// stamps <c>DecidedAt</c>; <see cref="Expired"/> is reserved for stale-proposal cleanup.
/// Persisted as <c>int</c> — APPEND-ONLY, never reorder.
/// </summary>
public enum PendingActionStatus
{
    Pending,
    Confirmed,
    Cancelled,
    Expired
}
