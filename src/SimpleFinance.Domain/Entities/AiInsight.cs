using SimpleFinance.Domain.Common;
using SimpleFinance.Domain.Enums;

namespace SimpleFinance.Domain.Entities;

/// <summary>
/// A proactive AI insight or anomaly surfaced to the user (Phase 18, AI-INS-01..04). Written by the
/// deterministic detection jobs (<c>AiInsightJob</c> / <c>AiAnomalyJob</c>): the NUMBERS come from a
/// deterministic calculation and are stored in <see cref="DataJson"/> (a jsonb blob — the authoritative
/// figures behind the text); the LLM only phrases the <see cref="Title"/>/<see cref="Body"/> prose.
/// The <see cref="DedupKey"/> + the unique <c>(UserId, DedupKey)</c> index are the anti-spam spine —
/// the same signal/occurrence is never re-created while still relevant, and a Dismissed insight never
/// reappears for the same key (a single existence check, no separate fire-log table). Its
/// <see cref="Status"/> starts <c>New</c> and moves New→Seen→Dismissed. Per-user (inherits
/// <see cref="UserOwnedEntity"/> for UserId/Id/CreatedAt/UpdatedAt/DeletedAt).
/// </summary>
public class AiInsight : UserOwnedEntity
{
    /// <summary>What kind of insight this is. This phase produces the first five kinds
    /// (SpendingChange/Trend/SubscriptionCreep/BudgetRisk/Anomaly); MonthlySummary/Forecast are reserved for Phase 19.</summary>
    public AiInsightKind Kind { get; set; }

    /// <summary>How urgent/important the insight is (drives notification + card badge color).</summary>
    public AiInsightSeverity Severity { get; set; }

    /// <summary>Short LLM-written (or deterministic-fallback) PT-BR title shown on the card.</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>LLM-written (or deterministic-fallback) PT-BR body — the explanation prose.</summary>
    public string Body { get; set; } = string.Empty;

    /// <summary>Start of the period the insight covers.</summary>
    public DateTime PeriodStart { get; set; }

    /// <summary>End of the period the insight covers.</summary>
    public DateTime PeriodEnd { get; set; }

    /// <summary>The deterministic numbers behind the text as a compact jsonb document — the authoritative
    /// figures the UI displays (never trust the model for numbers).</summary>
    public string DataJson { get; set; } = "{}";

    /// <summary>Lifecycle state. Starts <see cref="AiInsightStatus.New"/>.</summary>
    public AiInsightStatus Status { get; set; }

    /// <summary>Stable per-occurrence key (by kind + period + subject) that prevents re-creating the same
    /// insight while still relevant AND makes "dismissed stays dismissed" a single existence check.
    /// Unique per user via the <c>(UserId, DedupKey)</c> index.</summary>
    public string DedupKey { get; set; } = string.Empty;
}
