using Microsoft.EntityFrameworkCore;
using Pgvector;
using Pgvector.EntityFrameworkCore;
using SimpleFinance.Domain.Enums;
using SimpleFinance.Infrastructure.Persistence;

namespace SimpleFinance.Infrastructure.Categorization;

/// <summary>The winning category and its similarity-weighted confidence from a kNN vote.</summary>
public sealed record KnnVote(Guid CategoryId, double Confidence);

/// <summary>
/// AI-CAT-02 level 3. Runs a pgvector nearest-neighbour query over the user's already-categorized
/// transactions (the "trusted labels": <c>CategorySource IN (Rule, UserOverride, AiSuggested)</c>)
/// and turns the neighbours into a similarity-weighted vote. A seam (interface) so the cascade can be
/// unit-tested on the EF InMemory provider — the concrete query needs the Npgsql <c>vector(1536)</c>
/// column, which InMemory cannot map.
/// </summary>
public interface IKnnCategoryVoter
{
    /// <summary>
    /// Returns the winning category + confidence for <paramref name="query"/>, or <c>null</c> when
    /// there are no trusted neighbours or the nearest one is below the proximity floor ("no signal").
    /// </summary>
    Task<KnnVote?> VoteAsync(
        Guid userId,
        Guid currentTransactionId,
        Vector query,
        int k,
        double proximityFloorSim,
        CancellationToken ct);
}

/// <summary>
/// pgvector-backed <see cref="IKnnCategoryVoter"/>. Orders the user's trusted-label transaction
/// embeddings by cosine distance to the query (the HNSW cosine index backs the ordering), takes the
/// top <c>k</c>, and delegates the pure math to <see cref="ScoreNeighbors"/>.
/// </summary>
public sealed class KnnCategoryVoter : IKnnCategoryVoter
{
    private readonly SimpleFinanceDbContext _db;

    public KnnCategoryVoter(SimpleFinanceDbContext db)
    {
        _db = db;
    }

    public async Task<KnnVote?> VoteAsync(
        Guid userId,
        Guid currentTransactionId,
        Vector query,
        int k,
        double proximityFloorSim,
        CancellationToken ct)
    {
        // kNN over the caller's OWN trusted labels only (T-16-06): scoped to UserId, non-null category,
        // CategorySource in Rule/UserOverride/AiSuggested. The global soft-delete filter excludes
        // DeletedAt != null on both sides. EF translates the ORDER BY ... <=> $q LIMIT k using the
        // HNSW cosine index; the distance is projected so the pure scorer stays DB-free.
        var neighbors = await (
                from te in _db.TransactionEmbeddings
                join t in _db.Transactions on te.TransactionId equals t.Id
                where te.UserId == userId
                      && t.Id != currentTransactionId
                      && t.CategoryId != null
                      && (t.CategorySource == CategorySource.Rule
                          || t.CategorySource == CategorySource.UserOverride
                          || t.CategorySource == CategorySource.AiSuggested)
                orderby te.Embedding.CosineDistance(query)
                select new { t.CategoryId, Distance = te.Embedding.CosineDistance(query) })
            .Take(k)
            .ToListAsync(ct);

        var pairs = neighbors
            .Where(n => n.CategoryId.HasValue)
            .Select(n => (n.CategoryId!.Value, n.Distance))
            .ToList();

        var (winner, confidence) = ScoreNeighbors(pairs, proximityFloorSim);
        return winner is Guid category ? new KnnVote(category, confidence) : null;
    }

    /// <summary>
    /// PURE kNN confidence math (16-RESEARCH §2), isolated so it is unit-testable without a DB.
    /// <para>
    /// Proximity floor: if the NEAREST neighbour's similarity (<c>1 - cosine_distance</c>) is below
    /// <paramref name="proximityFloorSim"/>, there is no close analogue yet → returns
    /// <c>(null, 0)</c> ("no signal") so the cascade falls through to the LLM, regardless of any
    /// agreement among the distant neighbours.
    /// </para>
    /// <para>
    /// Otherwise: <c>sim_i = 1 - dist_i</c>; each category's score is the sum of its neighbours' sims;
    /// confidence = winner_score / total_score (similarity-weighted vote share).
    /// </para>
    /// </summary>
    public static (Guid? CategoryId, double Confidence) ScoreNeighbors(
        IReadOnlyList<(Guid categoryId, double distance)> neighbors,
        double proximityFloorSim)
    {
        if (neighbors.Count == 0)
        {
            return (null, 0.0);
        }

        // The proximity floor is gated on the SINGLE nearest neighbour (max similarity / min distance).
        var nearestSim = 1.0 - neighbors.Min(n => n.distance);
        if (nearestSim < proximityFloorSim)
        {
            return (null, 0.0);
        }

        var scores = new Dictionary<Guid, double>();
        var total = 0.0;
        foreach (var (categoryId, distance) in neighbors)
        {
            var sim = 1.0 - distance;
            if (sim <= 0.0)
            {
                // A neighbour past the antipode contributes no positive vote weight.
                continue;
            }

            scores[categoryId] = scores.GetValueOrDefault(categoryId) + sim;
            total += sim;
        }

        if (total <= 0.0)
        {
            return (null, 0.0);
        }

        var winner = scores
            .OrderByDescending(kv => kv.Value)
            .ThenBy(kv => kv.Key)
            .First();

        return (winner.Key, winner.Value / total);
    }
}
