namespace SimpleFinance.Application.Ai;

/// <summary>
/// The ONLY seam any non-Infrastructure code may depend on for LLM chat/completions
/// (mirrors <see cref="Sync.IFinancialDataProvider"/> — never let the concrete DeepSeekLlmClient
/// be referenced directly from Application or Api). Implemented by Infrastructure's
/// DeepSeekLlmClient (15-03), which enforces the redact → budget-check → send → record invariant
/// internally so no caller can bypass PII redaction or the monthly budget guard.
/// Isolates the provider so a future swap (e.g. deepseek-chat → deepseek-v4-flash, or a different
/// vendor entirely) never touches the Domain or callers. The model name always travels on
/// <see cref="LlmChatRequest.Model"/> — never a hard-coded constant — because the current model
/// names are config-driven and deprecating.
/// </summary>
public interface ILlmClient
{
    /// <summary>
    /// Non-streaming chat completion. Supports tool-calling when
    /// <see cref="LlmChatRequest.Tools"/> is set; returns the model's answer plus any requested
    /// tool calls and the token usage for cost/budget accounting.
    /// </summary>
    Task<LlmResult> ChatAsync(LlmChatRequest request, CancellationToken ct);

    /// <summary>
    /// Streaming chat completion over server-sent events. Yields incremental
    /// <see cref="LlmStreamChunk"/> deltas as the model generates; the final chunk carries token
    /// usage when available. Declared now, exercised by the chat UI in a later phase (17).
    /// </summary>
    IAsyncEnumerable<LlmStreamChunk> ChatStreamAsync(LlmChatRequest request, CancellationToken ct);
}
