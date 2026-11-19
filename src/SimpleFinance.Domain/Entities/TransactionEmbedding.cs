using SimpleFinance.Domain.Common;

namespace SimpleFinance.Domain.Entities;

/// <summary>
/// One row per embedded Transaction (AI-CAT-01) — the vector representation of a transaction's
/// redacted text (Description + MerchantName) used for kNN similarity categorization. Backed by a
/// real pgvector <c>vector(1536)</c> column with an HNSW cosine index (added in the 16-01
/// migration). Per-user (inherits <see cref="UserOwnedEntity"/> for UserId/Id/CreatedAt/UpdatedAt/
/// DeletedAt).
/// </summary>
public class TransactionEmbedding : UserOwnedEntity
{
    /// <summary>FK to the embedded <see cref="Transaction"/>. Unique — one embedding per transaction.</summary>
    public Guid TransactionId { get; set; }

    /// <summary>
    /// The 1536-dimension embedding vector (text-embedding-3-small). Mapped to <c>vector(1536)</c>
    /// in <c>TransactionEmbeddingConfiguration</c>.
    /// </summary>
    public Pgvector.Vector Embedding { get; set; } = default!;

    /// <summary>Name of the embedding model that produced <see cref="Embedding"/>. Config-driven.</summary>
    public string EmbeddingModel { get; set; } = string.Empty;

    /// <summary>
    /// Hash of the normalized source text (OpenAiEmbeddingClient.HashNormalized) used for cache/dedup
    /// in the 16-02 embedding job — identical text reuses the vector instead of re-calling the API.
    /// </summary>
    public string SourceTextHash { get; set; } = string.Empty;
}
