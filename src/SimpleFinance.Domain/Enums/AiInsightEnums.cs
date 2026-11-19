namespace SimpleFinance.Domain.Enums;

/// <summary>
/// What kind of <see cref="Entities.AiInsight"/> was detected. Phase 18 produces the first five
/// (<see cref="SpendingChange"/>, <see cref="Trend"/>, <see cref="SubscriptionCreep"/>,
/// <see cref="BudgetRisk"/>, <see cref="Anomaly"/>); <see cref="MonthlySummary"/> and
/// <see cref="Forecast"/> are declared but reserved for Phase 19. Persisted as <c>int</c> —
/// APPEND-ONLY, never reorder.
/// </summary>
public enum AiInsightKind
{
    SpendingChange,
    Trend,
    SubscriptionCreep,
    BudgetRisk,
    Anomaly,
    MonthlySummary,
    Forecast
}

/// <summary>
/// How urgent/important an <see cref="Entities.AiInsight"/> is — drives the anomaly-notification
/// threshold (Critical/Warning notify) and the card badge color. Persisted as <c>int</c> —
/// APPEND-ONLY, never reorder.
/// </summary>
public enum AiInsightSeverity
{
    Info,
    Warning,
    Critical
}

/// <summary>
/// Lifecycle of an <see cref="Entities.AiInsight"/>. Created <see cref="New"/>; moves to
/// <see cref="Seen"/> when the user views it and <see cref="Dismissed"/> when dismissed — a Dismissed
/// occurrence never reappears for the same DedupKey. Persisted as <c>int</c> — APPEND-ONLY, never reorder.
/// </summary>
public enum AiInsightStatus
{
    New,
    Seen,
    Dismissed
}
