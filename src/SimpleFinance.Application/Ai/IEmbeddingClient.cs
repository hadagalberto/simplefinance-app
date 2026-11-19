namespace SimpleFinance.Application.Ai;

/// <summary>
/// The ONLY seam any non-Infrastructure code may depend on for text embeddings
/// (mirrors <see cref="Sync.IFinancialDataProvider"/> — never let the concrete
/// OpenAiEmbeddingClient be referenced directly from Application or Api). Implemented by
/// Infrastructure's OpenAiEmbeddingClient (15-03), which enforces PII redaction and the budget
/// guard internally. Isolates the embeddings provider so a future swap never touches callers.
/// </summary>
public interface IEmbeddingClient
{
    /// <summary>
    /// Embeds a batch of texts and returns one 1536-length vector per input, aligned to the input
    /// order. The batch MUST be capped at 100 texts per request; callers embedding more should
    /// chunk. The implementation deduplicates identical texts by SHA-256 hash of the normalized
    /// source so an identical normalized string is never re-embedded within a batch.
    /// </summary>
    Task<float[][]> EmbedAsync(IReadOnlyList<string> texts, CancellationToken ct);
}
