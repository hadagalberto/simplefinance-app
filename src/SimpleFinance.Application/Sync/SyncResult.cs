using SimpleFinance.Domain.Enums;

namespace SimpleFinance.Application.Sync;

/// <summary>Outcome of one <see cref="ISyncReconciler"/> run, mirrored into a SyncLog row.</summary>
public sealed record SyncResult(
    SyncStatus Status,
    int TransactionsAdded,
    int TransactionsUpdated,
    int AccountsAdded,
    int AccountsUpdated,
    int InvestmentsAdded,
    int InvestmentsUpdated,
    string? Error);
