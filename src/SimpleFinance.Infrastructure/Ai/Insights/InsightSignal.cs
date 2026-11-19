using SimpleFinance.Domain.Enums;

namespace SimpleFinance.Infrastructure.Ai.Insights;

/// <summary>
/// A single qualifying insight candidate produced by <see cref="IInsightDetector"/> (18-02, AI-INS-01).
/// This is the PURE, deterministic output of detection: the authoritative numbers live in
/// <see cref="DataJson"/> (never the LLM), a deterministic PT-BR <see cref="FallbackTitle"/>/
/// <see cref="FallbackBody"/> is computed up front so persistence never depends on the prose layer, and
/// the composed <see cref="DedupKey"/> is the anti-spam spine consumed by
/// <see cref="AiInsightService.UpsertAsync"/> in 18-03.
///
/// 18-03 maps a signal to an <see cref="Domain.Entities.AiInsight"/> (Kind/Severity/Period/DataJson/
/// DedupKey copied verbatim, Title/Body = the fallback or the LLM overwrite) and Upserts it. The
/// <see cref="Id"/> is a stable per-signal key (equal to <see cref="DedupKey"/>) used to align a batched
/// LLM prose response back to the right signal without ever letting the model move numbers between
/// candidates.
/// </summary>
public sealed record InsightSignal(
    string Id,
    AiInsightKind Kind,
    AiInsightSeverity Severity,
    string Subject,
    DateTime PeriodStart,
    DateTime PeriodEnd,
    string DataJson,
    string DedupKey,
    string FallbackTitle,
    string FallbackBody);
