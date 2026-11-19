namespace SimpleFinance.Application.Ai.Assistant;

/// <summary>
/// AI-CHAT-05: token-budget truncation of conversation history before it enters a chat request
/// (RESEARCH §Context Management, T-17-13). DeepSeek ships no lightweight .NET tokenizer and exact
/// counts arrive only in the API response, so truncation uses the cheap, safe over-estimate
/// <c>estTokens ≈ ceil(chars / 4)</c> for PT-BR text.
///
/// The orchestrator always keeps the system prompt + the current user turn; <see cref="Truncate"/>
/// fills the remaining budget with the most-recent PRIOR turns, newest-first, and drops the oldest
/// turns wholesale (predictable, no mid-turn truncation). Keeping tool results compact at the source
/// (17-02) keeps them from dominating the budget.
/// </summary>
public static class ContextBudget
{
    /// <summary>
    /// Default whole-request soft cap (~10k tokens). Conservative even if the deployed model window is
    /// far larger — keeps cost/latency low and is safe on the legacy <c>deepseek-chat</c> window.
    /// </summary>
    public const int DefaultBudgetTokens = 10_000;

    /// <summary>
    /// Cheap PT-BR token estimate: <c>ceil(chars / 4)</c>. Over-estimates (errs toward truncating
    /// earlier), which is the safe direction for a cost/context guard.
    /// </summary>
    public static int EstimateTokens(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return 0;
        }

        return (int)Math.Ceiling(text.Length / 4.0);
    }

    /// <summary>
    /// Estimated token cost of a single message (its content plus any tool-call/argument text).
    /// </summary>
    public static int EstimateTokens(LlmMessage message)
    {
        var total = EstimateTokens(message.Content);
        if (message.ToolCalls is { Count: > 0 })
        {
            foreach (var call in message.ToolCalls)
            {
                total += EstimateTokens(call.Name) + EstimateTokens(call.ArgumentsJson);
            }
        }

        return total;
    }

    /// <summary>
    /// Keep the newest prior-turn messages that fit within <paramref name="budgetTokens"/>, returned in
    /// chronological (oldest-first) order so the caller can splice them between the system prompt and the
    /// current user turn. Accumulates newest-first and stops at the first message that would overflow the
    /// budget — dropping every older turn wholesale. A single message larger than the whole budget is
    /// dropped (never partially included).
    /// </summary>
    public static IReadOnlyList<LlmMessage> Truncate(
        IReadOnlyList<LlmMessage> history,
        int budgetTokens = DefaultBudgetTokens)
    {
        if (history.Count == 0 || budgetTokens <= 0)
        {
            return [];
        }

        var kept = new List<LlmMessage>(history.Count);
        var used = 0;

        for (var i = history.Count - 1; i >= 0; i--)
        {
            var cost = EstimateTokens(history[i]);
            if (used + cost > budgetTokens)
            {
                break;
            }

            used += cost;
            kept.Add(history[i]);
        }

        kept.Reverse();
        return kept;
    }
}
