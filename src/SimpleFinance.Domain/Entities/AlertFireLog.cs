using SimpleFinance.Domain.Common;

namespace SimpleFinance.Domain.Entities;

/// <summary>
/// De-dupe ledger for AlertEvaluationJob (a later Phase 8 plan)'s TryRecordFireAsync — checked
/// before firing an alert so the same event isn't re-alerted repeatedly. One row per
/// (AlertRuleId, TargetKey) that has ever fired, unique-indexed.
/// </summary>
public class AlertFireLog : UserOwnedEntity
{
    /// <summary>FK to AlertRule. No navigation property required.</summary>
    public Guid AlertRuleId { get; set; }

    /// <summary>
    /// The de-duped target of this alert (e.g. an AccountId, CategoryId, or composite key)
    /// scoped within the rule — the exact shape is decided by AlertEvaluationJob per type.
    /// </summary>
    public string TargetKey { get; set; } = string.Empty;

    public DateTime LastFiredAt { get; set; }
}
