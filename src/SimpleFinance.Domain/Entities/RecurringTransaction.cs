using SimpleFinance.Domain.Common;
using SimpleFinance.Domain.Enums;

namespace SimpleFinance.Domain.Entities;

/// <summary>
/// A detected recurring charge/subscription group for RECUR-01..04. Populated and kept
/// up to date by Phase 7's RecurringDetectionJob, which groups POSTED, non-transfer,
/// non-hidden expense Transactions by normalized merchant/description. Idempotent upsert
/// key is (UserId, NormalizedMerchant) — enforced by a unique DB index in
/// RecurringTransactionConfiguration, so re-running detection updates existing rows
/// instead of duplicating them.
/// </summary>
public class RecurringTransaction : UserOwnedEntity
{
    /// <summary>
    /// Normalized merchant/description used as the grouping + idempotency key (trailing
    /// digits/dates/store numbers stripped, uppercased, whitespace collapsed).
    /// </summary>
    public string NormalizedMerchant { get; set; } = string.Empty;

    /// <summary>Last-seen raw description/merchant, used for human display.</summary>
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>FK to Category. No navigation property required.</summary>
    public Guid? CategoryId { get; set; }

    public decimal AverageAmount { get; set; }

    /// <summary>
    /// Populated on the update path of a later plan's idempotent upsert; used to render
    /// RECUR-04's "amount increased" comparison against the current AverageAmount.
    /// </summary>
    public decimal? PreviousAverageAmount { get; set; }

    public RecurringFrequency Frequency { get; set; }

    public DateTime FirstSeenDate { get; set; }

    public DateTime LastSeenDate { get; set; }

    public DateTime NextExpectedDate { get; set; }

    public int OccurrenceCount { get; set; }

    /// <summary>User-toggleable. Defaults false.</summary>
    public bool IsSubscription { get; set; }

    /// <summary>User-toggleable active/cancelled. Defaults true.</summary>
    public bool IsActive { get; set; } = true;

    /// <summary>
    /// RECUR-04's "new recurring" flag. Defaults true on first detection, flipped to false
    /// the next time a detection run finds this same NormalizedMerchant again.
    /// </summary>
    public bool IsNewlyDetected { get; set; } = true;

    /// <summary>RECUR-04's "amount increase" flag. Defaults false.</summary>
    public bool AmountIncreaseDetected { get; set; }
}
