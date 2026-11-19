using SimpleFinance.Domain.Common;
using SimpleFinance.Domain.Enums;

namespace SimpleFinance.Domain.Entities;

/// <summary>
/// A user-configured alert rule for NOTIF-02/NOTIF-03. AlertEvaluationJob (a later Phase 8 plan)
/// evaluates active rules against current state and emits Notification rows + dispatches to
/// Channel when a condition fires.
/// </summary>
public class AlertRule : UserOwnedEntity
{
    public AlertRuleType Type { get; set; }

    /// <summary>
    /// Required by LowBalance/LargeTransaction only. BudgetThreshold uses BUD-04's fixed
    /// 90%/100% tiers instead of a configurable threshold, so this is unused for that type.
    /// </summary>
    public decimal? Threshold { get; set; }

    /// <summary>
    /// BudgetThreshold scoping. FK to Category, no navigation property (matches
    /// Transaction.CategoryId's plain-FK convention). Null = every budgeted category incl.
    /// the global cap.
    /// </summary>
    public Guid? CategoryId { get; set; }

    /// <summary>
    /// LowBalance/InvoiceDue/LargeTransaction scoping. FK to Account, no navigation property.
    /// Null = every account.
    /// </summary>
    public Guid? AccountId { get; set; }

    /// <summary>InvoiceDue only — days before the due date to fire.</summary>
    public int? DaysBefore { get; set; }

    public NotificationChannelType Channel { get; set; }

    public bool IsActive { get; set; } = true;
}
