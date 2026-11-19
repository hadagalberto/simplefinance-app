using SimpleFinance.Domain.Common;
using SimpleFinance.Domain.Enums;

namespace SimpleFinance.Domain.Entities;

/// <summary>
/// One row per reconciler run against a Connection, per PRD §11.2 (no UserId). Supports SYNC-05.
/// </summary>
public class SyncLog : AuditableEntity
{
    public Guid ConnectionId { get; set; }

    public SyncTrigger TriggeredBy { get; set; }

    public DateTime StartedAt { get; set; }

    public DateTime? FinishedAt { get; set; }

    public SyncStatus Status { get; set; }

    public int TransactionsAdded { get; set; }

    public int TransactionsUpdated { get; set; }

    public int AccountsAdded { get; set; }

    public int AccountsUpdated { get; set; }

    public int InvestmentsAdded { get; set; }

    public int InvestmentsUpdated { get; set; }

    public string? Error { get; set; }
}
