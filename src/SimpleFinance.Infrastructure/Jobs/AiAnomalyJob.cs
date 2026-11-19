using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SimpleFinance.Domain.Entities;
using SimpleFinance.Domain.Enums;
using SimpleFinance.Infrastructure.Ai.Insights;
using SimpleFinance.Infrastructure.Notifications;
using SimpleFinance.Infrastructure.Persistence;

namespace SimpleFinance.Infrastructure.Jobs;

/// <summary>
/// AI-INS-02/03: the Hangfire job that turns the deterministic 18-04 anomaly signals into persisted
/// <c>Kind=Anomaly</c> <see cref="AiInsight"/> cards and, for high-severity z-score/duplicate anomalies
/// ONLY, fires a <see cref="Notification"/> (Type <see cref="AlertRuleType.AiAnomaly"/>, Channel
/// <see cref="NotificationChannelType.Both"/>) via the existing <see cref="NotificationDispatcher"/>. For
/// every user with <c>AiEnabled &amp;&amp; AiAnomalyEnabled</c> it runs
/// <see cref="IAnomalyDetector.DetectAsync"/>, batch-phrases the resulting signals through
/// <see cref="InsightProseWriter.WriteAsync"/> (ONE id-keyed ChatAsync attributed to
/// <see cref="AiFeature.Anomaly"/>, degrading to the deterministic fallback when over budget), and Upserts
/// each row via <see cref="AiInsightService.UpsertAsync"/>.
///
/// The unique <c>(UserId, DedupKey)</c> index is the fire-once guarantee (T-18-14): the dispatch branch is
/// reached ONLY when <see cref="AiInsightService.UpsertAsync"/> returned <c>true</c> (a brand-new row was
/// just inserted). A re-run — or a dismissed occurrence still sitting in the table — makes Upsert return
/// <c>false</c>, so the same anomaly never notifies twice and a dismissed one never re-fires. Dispatch is
/// further gated to <c>Severity &gt;= Warning</c> AND a dispatch-eligible subkind: subscription-increase and
/// atypical-category anomalies are CARD-ONLY (T-18-16), because Phase 8's NewRecurring/BudgetThreshold
/// alerts already push those — dispatching them here would double-notify.
///
/// Runs BOTH post-sync (chained after categorization in <c>SyncConnectionUseCase.EnqueueAiCascade</c>) and
/// nightly (the <c>ai-anomaly</c> recurring job). Each user is processed inside its own try/catch +
/// <see cref="DbContext.ChangeTracker"/> clear so one user's failure never aborts the batch (mirrors
/// <see cref="AiInsightJob"/> / AlertEvaluationJob). <see cref="NotificationDispatcher.DispatchAsync"/> never
/// throws (per-channel try/catch), so no guard is needed around it and dispatch is independent of the LLM.
/// </summary>
public sealed class AiAnomalyJob
{
    private readonly SimpleFinanceDbContext _db;
    private readonly IAnomalyDetector _detector;
    private readonly InsightProseWriter _prose;
    private readonly AiInsightService _service;
    private readonly NotificationDispatcher _dispatcher;
    private readonly ILogger<AiAnomalyJob> _logger;

    public AiAnomalyJob(
        SimpleFinanceDbContext db,
        IAnomalyDetector detector,
        InsightProseWriter prose,
        AiInsightService service,
        NotificationDispatcher dispatcher,
        ILogger<AiAnomalyJob> logger)
    {
        _db = db;
        _detector = detector;
        _prose = prose;
        _service = service;
        _dispatcher = dispatcher;
        _logger = logger;
    }

    public async Task ExecuteAsync(CancellationToken ct)
    {
        var referenceDate = DateTime.UtcNow;

        // Only opted-in users are ever processed (single-user app: the one owner). Mirrors AiInsightJob's
        // per-feature gate, swapping AiInsightsEnabled → AiAnomalyEnabled.
        var users = await _db.UserSettings
            .Where(s => s.AiEnabled && s.AiAnomalyEnabled)
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
                _logger.LogError(ex, "AiAnomalyJob failed for user {UserId}", user.UserId);
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

        // One batched, id-keyed prose call for all of the user's anomalies (AiFeature.Anomaly); degrades to
        // the deterministic fallback on AiBudgetExceededException / any failure — persistence AND dispatch
        // never depend on the LLM.
        var insights = await _prose.WriteAsync(signals, chatModel, AiFeature.Anomaly, ct);

        // Dispatch-eligibility is decided by the signal's DedupKey prefix (== subkind); keep a lookup so the
        // persist loop can classify each just-inserted insight.
        var dispatchEligible = signals
            .Where(s => IsDispatchEligible(s.DedupKey))
            .Select(s => s.DedupKey)
            .ToHashSet();

        // Loaded lazily only if we actually need to dispatch (avoids a Users read for card-only runs).
        User? owner = null;

        foreach (var insight in insights)
        {
            insight.UserId = userId;
            insight.CreatedAt = referenceDate;
            insight.UpdatedAt = referenceDate;

            // Idempotent + dismissed-stays-dismissed: false means the (UserId, DedupKey) already exists —
            // skip, and (critically) never reach the dispatch branch below. This IS the fire-once guarantee.
            var inserted = await _service.UpsertAsync(insight, ct);
            if (!inserted)
            {
                continue;
            }

            // Card-only unless: high severity AND a dispatch-eligible subkind (z-score / duplicate).
            if (insight.Severity < AiInsightSeverity.Warning || !dispatchEligible.Contains(insight.DedupKey))
            {
                continue;
            }

            owner ??= await _db.Users.FirstOrDefaultAsync(u => u.Id == userId, ct);
            if (owner is null)
            {
                continue;
            }

            await FireAsync(userId, owner.Email, insight, ct);
        }
    }

    /// <summary>
    /// Reproduces AlertEvaluationJob.FireAsync's shape: build the Notification (Type=AiAnomaly, Channel=Both),
    /// SaveChanges, then best-effort DispatchAsync. Reached ONLY for a just-inserted, dispatch-eligible,
    /// high-severity anomaly — so the notification row and the push are both fire-once via the DedupKey gate.
    /// </summary>
    private async Task FireAsync(Guid userId, string email, AiInsight insight, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        _db.Notifications.Add(new Notification
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            Type = AlertRuleType.AiAnomaly,
            Title = insight.Title,
            Message = insight.Body,
            Channel = NotificationChannelType.Both,
            IsRead = false,
            SentAt = now,
            CreatedAt = now,
            UpdatedAt = now,
        });

        await _db.SaveChangesAsync(ct);

        // DispatchAsync never throws (per-channel try/catch) and skips unconfigured channels.
        await _dispatcher.DispatchAsync(
            NotificationChannelType.Both, insight.Title, insight.Body, email, ct);
    }

    /// <summary>
    /// Only z-score amount outliers and duplicate charges push (RESEARCH OQ2). Classified by the
    /// AnomalyDetector's DedupKey namespaces — subscription-increase (<c>Anomaly:subincrease:</c>) and
    /// atypical-category (<c>Anomaly:category:</c>) are card-only and return false.
    /// </summary>
    private static bool IsDispatchEligible(string dedupKey) =>
        dedupKey.StartsWith("Anomaly:zscore:", StringComparison.Ordinal)
        || dedupKey.StartsWith("Anomaly:dup:", StringComparison.Ordinal);
}
