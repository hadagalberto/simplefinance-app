using SimpleFinance.Domain.Common;
using SimpleFinance.Domain.Enums;

namespace SimpleFinance.Domain.Entities;

/// <summary>
/// Wraps a Pluggy Item — the top-level record of a bank/institution connection.
/// Supports PLG-03/PLG-04/PLG-05/PLG-06/PLG-07 and is the anchor entity SYNC-01..07's
/// reconciler/webhook/job pipeline operates against.
/// </summary>
public class Connection : UserOwnedEntity
{
    /// <summary>Pluggy's Item UUID. Unique per Connection.</summary>
    public string PluggyItemId { get; set; } = string.Empty;

    public int? ConnectorId { get; set; }

    public string? ConnectorName { get; set; }

    public string? InstitutionName { get; set; }

    /// <summary>
    /// User-defined nickname for this connection (e.g. "Nubank PJ", "Conta da esposa").
    /// Lets the user tell apart multiple connections to the same institution. Falls back
    /// to InstitutionName/ConnectorName for display when null.
    /// </summary>
    public string? CustomName { get; set; }

    public string? ImageUrl { get; set; }

    /// <summary>Top-level Pluggy Item <c>status</c> — the 5-value verified enum.</summary>
    public ConnectionStatus Status { get; set; }

    /// <summary>
    /// Free-text Pluggy <c>executionStatus</c> (e.g. MERGE_ERROR, SUCCESS, PARTIAL_SUCCESS,
    /// INVALID_CREDENTIALS, ...). Deliberately NOT a closed C# enum — Pluggy has ~24 documented
    /// values that can grow without a code migration (02-RESEARCH.md).
    /// </summary>
    public string? ExecutionStatus { get; set; }

    public string? StatusDetail { get; set; }

    public string? ErrorCode { get; set; }

    public string? ErrorMessage { get; set; }

    public DateTime? LastSyncAt { get; set; }
}
