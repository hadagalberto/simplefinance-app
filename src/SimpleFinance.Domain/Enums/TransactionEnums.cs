namespace SimpleFinance.Domain.Enums;

/// <summary>Pluggy Transaction <c>type</c> field.</summary>
public enum TransactionType
{
    Debit,
    Credit
}

/// <summary>Pluggy Transaction <c>status</c> field — PENDING transitions to POSTED in place.</summary>
public enum TransactionStatus
{
    Pending,
    Posted
}

/// <summary>
/// Provenance of a Transaction's assigned Category — per CONTEXT.md/PITFALLS.md #10, this field
/// must exist from Phase 2 even though only <see cref="SystemMapping"/> is ever set here; Phase 4
/// adds Rule/UserOverride writers without a schema migration.
/// </summary>
/// <remarks>
/// Persisted as <c>int</c>. Members are APPEND-ONLY — never reorder existing values, since the
/// ordinal is the stored value (SystemMapping=0, Rule=1, UserOverride=2, AiAuto=3, AiSuggested=4).
/// The Phase 16 kNN fallback (16-03) filters trusted labels by
/// <c>CategorySource IN (Rule=1, UserOverride=2, AiSuggested=4)</c>.
/// </remarks>
public enum CategorySource
{
    SystemMapping,
    Rule,
    UserOverride,

    /// <summary>
    /// Auto-applied by the Phase 16 categorization cascade at/above the auto-apply confidence
    /// threshold (AI-CAT-03/04). Reversible by the user, but the AI must never re-touch it.
    /// </summary>
    AiAuto,

    /// <summary>
    /// User-confirmed via accepting an AI suggestion (AI-CAT-04/06). Treated like a user-confirmed
    /// override: a trusted label the AI must never overwrite, and a positive kNN neighbour.
    /// </summary>
    AiSuggested
}
