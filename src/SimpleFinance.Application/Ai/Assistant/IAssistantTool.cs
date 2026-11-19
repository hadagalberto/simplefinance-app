namespace SimpleFinance.Application.Ai.Assistant;

/// <summary>
/// AI-CHAT-02: one callable tool the assistant orchestrator (17-04) may hand to the LLM. Every tool
/// advertises a JSON-Schema (<see cref="Schema"/>) the model uses to emit a tool call, and runs
/// server-side, UserId-scoped, when the model invokes it. READ tools (this wave) wrap an existing
/// reporting service and return a compact aggregate; WRITE tools (17-06, <see cref="IsWrite"/> = true)
/// stage an <c>AiPendingAction</c> instead of mutating. The contract lives in Application so the
/// catalog/dispatcher can depend on it without referencing Infrastructure; the concrete tools live in
/// Infrastructure (they touch the DbContext / reporting services).
/// </summary>
public interface IAssistantTool
{
    /// <summary>The tool name the model calls (matches <see cref="LlmTool.Name"/> and the dispatch key).</summary>
    string Name { get; }

    /// <summary>
    /// False for read tools (safe to run without confirmation). True for write tools, which only
    /// PROPOSE an action (17-06). The orchestrator uses this to decide whether a confirmation card is
    /// required — a read tool result is never gated.
    /// </summary>
    bool IsWrite { get; }

    /// <summary>The JSON-Schema advertised to the model, published to <c>LlmChatRequest.Tools</c>.</summary>
    LlmTool Schema { get; }

    /// <summary>
    /// Execute the tool for <paramref name="userId"/> with the model-emitted <paramref name="argsJson"/>.
    /// The implementation deserializes + validates the args (rejecting unknown/hallucinated entities),
    /// runs the fixed parameterized query scoped to the caller, and returns a compact JSON result — never
    /// a raw history dump, never free-form SQL. Argument/validation problems are returned as an
    /// <c>{ "error": ... }</c> JSON string rather than thrown, so the tool loop never breaks.
    /// </summary>
    Task<string> ExecuteAsync(string argsJson, Guid userId, CancellationToken ct);
}
