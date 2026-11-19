using SimpleFinance.Domain.Common;
using SimpleFinance.Domain.Enums;

namespace SimpleFinance.Domain.Entities;

/// <summary>
/// A savings goal for GOAL-01..03. CurrentAmount is persisted so both linked-source-synced
/// and manually-tracked goals share one column — a later plan's GoalService overwrites this
/// column at read time from the live linked Account/Investment balance when linked, or via
/// an explicit manual-update endpoint when not linked.
/// </summary>
public class Goal : UserOwnedEntity
{
    public string Name { get; set; } = string.Empty;

    public decimal TargetAmount { get; set; }

    public decimal CurrentAmount { get; set; }

    public DateTime? TargetDate { get; set; }

    /// <summary>FK to Account. No navigation property required.</summary>
    public Guid? LinkedAccountId { get; set; }

    /// <summary>FK to Investment. No navigation property required.</summary>
    public Guid? LinkedInvestmentId { get; set; }

    public string? Icon { get; set; }

    public string? Color { get; set; }

    public GoalStatus Status { get; set; } = GoalStatus.Active;
}
