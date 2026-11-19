using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SimpleFinance.Infrastructure.Ai.Insights;
using SimpleFinance.Infrastructure.Persistence;

namespace SimpleFinance.Infrastructure.Jobs;

/// <summary>
/// AI-INS-01: the nightly Hangfire job that turns the deterministic 18-02 signals into persisted
/// <see cref="Domain.Entities.AiInsight"/> cards. For every user with <c>AiEnabled &amp;&amp;
/// AiInsightsEnabled</c> it runs <see cref="IInsightDetector.DetectAsync"/>, batch-phrases the resulting
/// signals through <see cref="InsightProseWriter.WriteAsync"/> (ONE id-keyed ChatAsync, degrading to the
/// deterministic fallback when over budget), and Upserts each row via <see cref="AiInsightService"/> — the
/// service's <c>(UserId, DedupKey)</c> existence gate makes re-runs idempotent AND makes
/// "dismissed stays dismissed" the same single check.
///
/// Registered as a daily recurring job (<c>ai-insight</c>) in Program.cs. Deliberately NOT chained into the
/// post-sync AI cascade — CONTEXT locks this to daily-only (the anomaly job in a later plan owns the
/// post-sync path). Each user is processed inside its own try/catch + <see cref="DbContext.ChangeTracker"/>
/// clear so one user's failure never aborts the batch (mirrors <see cref="AiCategorizationJob"/> /
/// AlertEvaluationJob). The prose layer never throws, so a budget cap simply yields fallback-worded cards.
/// </summary>
public sealed class AiInsightJob
{
    private readonly SimpleFinanceDbContext _db;
    private readonly IInsightDetector _detector;
    private readonly InsightProseWriter _prose;
    private readonly AiInsightService _service;
    private readonly ILogger<AiInsightJob> _logger;

    public AiInsightJob(
        SimpleFinanceDbContext db,
        IInsightDetector detector,
        InsightProseWriter prose,
        AiInsightService service,
        ILogger<AiInsightJob> logger)
    {
        _db = db;
        _detector = detector;
        _prose = prose;
        _service = service;
        _logger = logger;
    }

    public async Task ExecuteAsync(CancellationToken ct)
    {
        var referenceDate = DateTime.UtcNow;

        // Only opted-in users are ever processed (single-user app: the one owner). The per-feature flag
        // gate mirrors AiCategorizationJob so the job is a no-op when insights are off.
        var users = await _db.UserSettings
            .Where(s => s.AiEnabled && s.AiInsightsEnabled)
            .Select(s => new { s.UserId, s.AiChatModel })
            .ToListAsync(ct);

        foreach (var user in users)
        {
            try
            {
                await ProcessUserAsync(user.UserId, user.AiChatModel, referenceDate, ct);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "AiInsightJob failed for user {UserId}", user.UserId);
                _db.ChangeTracker.Clear();
            }
        }
    }

    private async Task ProcessUserAsync(Guid userId, string chatModel, DateTime referenceDate, CancellationToken ct)
    {
        var signals = await _detector.DetectAsync(userId, referenceDate, ct);
        if (signals.Count == 0)
        {
            return;
        }

        // One batched, id-keyed prose call for all of the user's signals; degrades to the deterministic
        // fallback on AiBudgetExceededException / any failure — persistence never depends on the LLM.
        var insights = await _prose.WriteAsync(signals, chatModel, ct);

        foreach (var insight in insights)
        {
            insight.UserId = userId;
            insight.CreatedAt = referenceDate;
            insight.UpdatedAt = referenceDate;

            // Idempotent + dismissed-stays-dismissed: false means the (UserId, DedupKey) already exists —
            // skip, never retry or mutate.
            await _service.UpsertAsync(insight, ct);
        }
    }
}
