namespace SimpleFinance.Infrastructure.Ai.Insights;

/// <summary>
/// The pure, deterministic statistical anomaly detector (18-04, AI-INS-02). Produces
/// <see cref="InsightSignal"/> candidates of <c>Kind=Anomaly</c> for four families — per-merchant
/// modified z-score outliers, duplicate charges, subscription increases, and atypical category spend —
/// with the documented false-positive guards (Debit-only scope so income is never flagged, the
/// RecurringTransaction allowlist for legitimate recurring, absolute floors + minimum history).
/// No LLM and no persistence live here: 18-05's anomaly job feeds the returned list to the prose layer
/// and <see cref="AiInsightService.UpsertAsync"/>, then dispatches notifications for the dispatchable
/// families. Each signal carries authoritative numbers in <c>DataJson</c>, a deterministic PT-BR
/// fallback Title/Body, a Severity, and a per-occurrence <c>DedupKey</c> so the job fires exactly once.
/// </summary>
public interface IAnomalyDetector
{
    Task<IReadOnlyList<InsightSignal>> DetectAsync(Guid userId, DateTime referenceDate, CancellationToken ct);
}
