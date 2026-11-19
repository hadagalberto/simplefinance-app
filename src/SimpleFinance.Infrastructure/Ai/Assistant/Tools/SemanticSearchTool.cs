using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Pgvector;
using Pgvector.EntityFrameworkCore;
using SimpleFinance.Application.Ai;
using SimpleFinance.Application.Ai.Assistant;
using SimpleFinance.Application.Ai.Usage;

namespace SimpleFinance.Infrastructure.Ai.Assistant.Tools;

/// <summary>
/// AI-CHAT-03 RAG tool <c>search_transactions_semantic</c>: the semantic complement to the
/// parameterized read tools. Embeds the (redacted) query text with the same OpenAI embedding model the
/// Phase 16 job uses, then runs a pgvector kNN over the CALLER's <c>TransactionEmbedding</c> index
/// (cosine distance, HNSW-backed) to surface the transactions most similar to the query — answering
/// "aquele gasto grande na farmácia" style questions the exact-filter tools cannot.
///
/// Safety levers mirror the Phase 16 recipe:
/// <list type="bullet">
///   <item>T-17-07: the query text is <see cref="IPiiRedactor"/>-scrubbed BEFORE it leaves to the
///   embedding provider, and every returned Description/MerchantName is redacted again before it
///   re-enters model context.</item>
///   <item>T-17-08: the kNN is scoped to <c>te.UserId == userId</c> — a second user's embeddings can
///   never appear in the caller's matches.</item>
///   <item>T-17-09: <c>topK</c> is clamped to 1..25, capping both result size and cost.</item>
/// </list>
/// When embeddings are unavailable — no OpenAI key configured, or a non-Npgsql provider that cannot map
/// the <c>vector(1536)</c> column — the tool degrades to <c>{ matches: [], note: "indisponível" }</c>
/// instead of throwing, so the orchestrator's tool loop is never broken (the 15-05/16 degrade contract).
/// </summary>
public sealed class SemanticSearchTool(
    Persistence.SimpleFinanceDbContext db,
    IEmbeddingClient embeddings,
    IPiiRedactor redactor,
    IAiKeyProvider keyProvider) : IAssistantTool
{
    private const int DefaultTopK = 10;
    private const int MinTopK = 1;
    private const int MaxTopK = 25;

    private static readonly LlmTool ToolSchemaValue = new(
        "search_transactions_semantic",
        "Busca semântica (RAG) de transações por significado, não por filtro exato — encontra lançamentos "
        + "parecidos com um texto livre (ex.: 'aquele gasto grande na farmácia'). Retorna as transações mais "
        + "similares, redigidas e limitadas a topK.",
        ToolSchema.Parse("""
        {
          "type": "object",
          "properties": {
            "text": { "type": "string", "description": "Texto livre a buscar por similaridade semântica" },
            "topK": { "type": "integer", "minimum": 1, "maximum": 25, "description": "Máximo de resultados (1..25, padrão 10)" }
          },
          "required": ["text"],
          "additionalProperties": false
        }
        """));

    public string Name => "search_transactions_semantic";
    public bool IsWrite => false;
    public LlmTool Schema => ToolSchemaValue;

    public async Task<string> ExecuteAsync(string argsJson, Guid userId, CancellationToken ct)
    {
        var args = JsonSerializer.Deserialize<Args>(argsJson) ?? new Args(null, null);

        if (string.IsNullOrWhiteSpace(args.Text))
        {
            return ToolResults.Error("invalid_query", "O texto de busca é obrigatório.");
        }

        // T-17-07: redact the free text BEFORE it leaves to the embedding provider (defense in depth on
        // top of the client's own redaction) and use the redacted form as the echoed query too.
        var redactedQuery = redactor.Redact(args.Text);
        var topK = Math.Clamp(args.TopK ?? DefaultTopK, MinTopK, MaxTopK);

        // Degradation guard: the pgvector index only exists under Npgsql (the entity is Ignored off-Npgsql,
        // exactly like Phase 16), and there is nothing to embed without an OpenAI key. The key check now
        // considers the DB-stored key too (IAiKeyProvider: DB-decrypt-else-env) so the tool is NOT disabled
        // when only the UI key is set. Either case → "indisponível" with empty matches, never an exception.
        var openAiKey = await keyProvider.GetOpenAiKeyAsync(ct);
        if (!db.Database.IsNpgsql() || string.IsNullOrWhiteSpace(openAiKey))
        {
            return Unavailable(redactedQuery);
        }

        try
        {
            var vectors = await embeddings.EmbedAsync(new[] { redactedQuery }, ct);
            if (vectors.Length == 0)
            {
                return Unavailable(redactedQuery);
            }

            var query = new Vector(vectors[0]);

            // T-17-08: kNN over the caller's OWN embeddings only. Follows the proven Phase 16
            // KnnCategoryVoter shape (join → where UserId → order by CosineDistance → take), which EF
            // translates to ORDER BY embedding <=> $q LIMIT k over the HNSW cosine index.
            var hits = await (
                    from te in db.TransactionEmbeddings
                    join t in db.Transactions on te.TransactionId equals t.Id
                    where te.UserId == userId
                    orderby te.Embedding.CosineDistance(query)
                    select new { t.Description, t.MerchantName, t.Amount, t.Date })
                .Take(topK)
                .ToListAsync(ct);

            // T-17-07: redact every free-text field again before it re-enters the model context.
            var matches = hits.Select(h => new
            {
                description = redactor.Redact(h.Description),
                merchant = redactor.Redact(h.MerchantName),
                amount = Math.Round(h.Amount, 2),
                date = h.Date.ToString("yyyy-MM-dd"),
            });

            return JsonSerializer.Serialize(new
            {
                query = redactedQuery,
                currency = "BRL",
                matches,
            });
        }
        catch (AiBudgetExceededException)
        {
            // A monthly-budget cap is an expected degrade, not a failure — never break the tool loop.
            return Unavailable(redactedQuery);
        }
    }

    private static string Unavailable(string query) =>
        JsonSerializer.Serialize(new { query, matches = Array.Empty<object>(), note = "indisponível" });

    private sealed record Args(
        [property: JsonPropertyName("text")] string? Text,
        [property: JsonPropertyName("topK")] int? TopK);
}
