using Microsoft.EntityFrameworkCore;
using SimpleFinance.Domain.Entities;
using SimpleFinance.Domain.Enums;
using SimpleFinance.Infrastructure.Persistence;

namespace SimpleFinance.Infrastructure.Ai.Insights;

/// <summary>
/// The shared write/read spine for Phase 18 insights + anomalies (AI-INS-01/03/04). Both detection jobs
/// (<c>AiInsightJob</c> / <c>AiAnomalyJob</c>) call <see cref="UpsertAsync"/> to persist a fully-built
/// insight; the endpoints call <see cref="GetAsync"/> / <see cref="DismissAsync"/> /
/// <see cref="MarkSeenAsync"/> to drive the New→Seen→Dismissed lifecycle. Deliberately in Infrastructure
/// (not Application) — it depends directly on <see cref="SimpleFinanceDbContext"/>, mirroring
/// <see cref="AiSuggestionService"/>.
///
/// EVERY query is UserId-scoped (T-18-01): reads filter <c>WHERE UserId == userId</c> and every mutation
/// loads the row scoped to the caller, so a foreign/missing id simply "isn't found" — no mutation, no
/// leak. The <see cref="UpsertAsync"/> existence gate against the unique <c>(UserId, DedupKey)</c> index
/// (T-18-02) is the single anti-spam mechanism: it makes job re-runs idempotent AND makes
/// "dismissed stays dismissed" one check — a row in ANY status (New/Seen/Dismissed) blocks re-creation.
/// The global soft-delete query filter excludes <c>DeletedAt != null</c> rows automatically.
/// </summary>
public sealed class AiInsightService(SimpleFinanceDbContext db)
{
    /// <summary>
    /// Persist a new insight when no row with the same <c>(UserId, DedupKey)</c> exists (returns true);
    /// a no-op returning false when one already exists in ANY status — idempotent re-run AND
    /// dismissed-stays-dismissed in one existence check. The caller supplies the fully-built insight
    /// (deterministic Title/Body/DataJson already set); this method never dispatches notifications.
    /// </summary>
    public async Task<bool> UpsertAsync(AiInsight insight, CancellationToken ct)
    {
        var exists = await db.AiInsights
            .AnyAsync(i => i.UserId == insight.UserId && i.DedupKey == insight.DedupKey, ct);

        if (exists)
        {
            return false;
        }

        db.AiInsights.Add(insight);
        await db.SaveChangesAsync(ct);
        return true;
    }

    /// <summary>
    /// The caller's insights of the given status, newest first. Backed by the <c>(UserId, Status)</c>
    /// index; never returns another user's rows.
    /// </summary>
    public async Task<IReadOnlyList<AiInsight>> GetAsync(Guid userId, AiInsightStatus status, CancellationToken ct)
    {
        return await db.AiInsights
            .Where(i => i.UserId == userId && i.Status == status)
            .OrderByDescending(i => i.CreatedAt)
            .ToListAsync(ct);
    }

    /// <summary>
    /// The caller's single insight of the given <paramref name="kind"/> for the month starting at
    /// <paramref name="periodStart"/> (newest first if the pathological case of multiples ever exists).
    /// Backs <c>GET /api/ai/summary</c> (Kind=MonthlySummary, PeriodStart=1st-of-prior-month UTC): the
    /// unique <c>(UserId, DedupKey)</c> index means at most one row per month, so this is a point lookup.
    /// UserId-scoped like every method here (T-18-01) — a foreign/missing month simply "isn't found", so
    /// the endpoint maps null to a 404 with no leak (T-19-12).
    /// </summary>
    public async Task<AiInsight?> GetByKindPeriodAsync(
        Guid userId, AiInsightKind kind, DateTime periodStart, CancellationToken ct)
    {
        return await db.AiInsights
            .Where(i => i.UserId == userId && i.Kind == kind && i.PeriodStart == periodStart)
            .OrderByDescending(i => i.CreatedAt)
            .FirstOrDefaultAsync(ct);
    }

    /// <summary>
    /// Dismiss a caller-owned insight (Status→Dismissed). Returns false when the id is missing or not
    /// owned — no mutation (IDOR-safe, T-18-01); the endpoint maps that to 404.
    /// </summary>
    public async Task<bool> DismissAsync(Guid userId, Guid id, CancellationToken ct)
    {
        var insight = await db.AiInsights
            .FirstOrDefaultAsync(i => i.Id == id && i.UserId == userId, ct);

        if (insight is null)
        {
            return false;
        }

        var now = DateTime.UtcNow;
        insight.Status = AiInsightStatus.Dismissed;
        insight.UpdatedAt = now;

        await db.SaveChangesAsync(ct);
        return true;
    }

    /// <summary>
    /// Transition a caller-owned New insight to Seen (kept separate so New stays the default list filter).
    /// Returns false when the id is missing or not owned — no mutation.
    /// </summary>
    public async Task<bool> MarkSeenAsync(Guid userId, Guid id, CancellationToken ct)
    {
        var insight = await db.AiInsights
            .FirstOrDefaultAsync(i => i.Id == id && i.UserId == userId, ct);

        if (insight is null)
        {
            return false;
        }

        if (insight.Status == AiInsightStatus.New)
        {
            var now = DateTime.UtcNow;
            insight.Status = AiInsightStatus.Seen;
            insight.UpdatedAt = now;
            await db.SaveChangesAsync(ct);
        }

        return true;
    }
}
