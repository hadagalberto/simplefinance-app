namespace SimpleFinance.Application.Ai.Assistant;

/// <summary>
/// AI-CHAT-02/05: the neutral, provider-agnostic payload the orchestrator (17-04) yields and the SSE
/// endpoint (17-05) serializes onto <c>SseItem&lt;ChatEvent&gt;.Data</c>. A single record type carries
/// every event kind the chat stream emits so the browser can <c>switch</c> on <see cref="Type"/> (and
/// on the SSE <c>event:</c> line): a streamed prose <c>token</c>, a transparency <c>tool</c> chip, a
/// write <c>action</c> confirmation card (17-06), the terminal <c>done</c> marker (carrying the
/// persisted assistant message id + token usage), or an <c>error</c> degrade message
/// (e.g. the monthly AI budget cap).
/// </summary>
public sealed record ChatEvent(
    string Type,
    string? Text = null,
    string? ToolName = null,
    ActionCard? Action = null,
    TokenUsage? Usage = null,
    Guid? MessageId = null);

/// <summary>
/// Placeholder descriptor for a proposed write action's confirmation card. The write tools (17-06)
/// populate this from a staged <c>AiPendingAction</c> — <see cref="Id"/> is the pending-action id the
/// confirm/cancel endpoints act on, <see cref="ActionType"/> names the change
/// (<c>create_category_rule</c> / <c>set_budget</c> / <c>create_goal</c> / <c>recategorize_transactions</c>),
/// and <see cref="Summary"/> is the human-readable one-liner the UI shows. No write tools exist this
/// wave (all catalog tools are read-only), so the orchestrator never emits an <c>action</c> event yet —
/// the shape is defined here so 17-06 slots in without touching the event contract.
/// </summary>
public sealed record ActionCard(
    Guid Id,
    string ActionType,
    string Summary);
