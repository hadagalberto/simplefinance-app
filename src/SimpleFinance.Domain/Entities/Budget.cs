using SimpleFinance.Domain.Common;

namespace SimpleFinance.Domain.Entities;

/// <summary>
/// A monthly spending limit for BUD-01, either per-category (<see cref="CategoryId"/> set) or
/// an optional global cap across all categories (<see cref="CategoryId"/> null) for a given
/// (user, year, month). No navigation property on <see cref="CategoryId"/> — matches
/// Transaction.CategoryId's plain-FK convention.
///
/// This entity intentionally stores ONLY the limit itself. Spend, percentage-of-cap, remaining
/// amount, end-of-month projection, and OK/Attention/Exceeded state are never persisted here —
/// they are always derived at read time by the BudgetCalculator (a later Phase 5 plan) from live
/// Transaction data, so they can never go stale relative to the source of truth.
/// </summary>
public class Budget : UserOwnedEntity
{
    /// <summary>Null means an optional global monthly cap across all categories.</summary>
    public Guid? CategoryId { get; set; }

    public int Year { get; set; }

    /// <summary>1-12.</summary>
    public int Month { get; set; }

    public decimal LimitAmount { get; set; }
}
