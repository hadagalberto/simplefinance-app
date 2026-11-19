using SimpleFinance.Domain.Enums;

namespace SimpleFinance.Application.Ai.Usage;

/// <summary>
/// AI-FND-04: records exactly one usage/cost row per cloud AI call. The provider clients (15-05)
/// call this after every LLM/embedding request so token counts and the estimated USD cost are
/// always captured — the data the 15-04 budget kill-switch sums per month (T-15-06). Defined in
/// Application (the seam callers depend on); implemented in Infrastructure over the DbContext.
/// </summary>
public interface IAiUsageRecorder
{
    /// <summary>
    /// Persists one AiUsageLog row for <paramref name="userId"/>, computing the estimated cost from
    /// the configured rate table for <paramref name="model"/> and the token counts.
    /// </summary>
    Task RecordAsync(
        Guid userId,
        AiFeature feature,
        AiProvider provider,
        string model,
        int promptTokens,
        int completionTokens,
        CancellationToken ct);
}
