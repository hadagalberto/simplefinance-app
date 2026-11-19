using SimpleFinance.Domain.Common;

namespace SimpleFinance.Domain.Entities;

/// <summary>
/// A daily net-worth data point for DASH-03's net-worth evolution chart.
///
/// Phase 6's <c>NetWorthSnapshotJob</c> (INV-04) is this table's writer: it upserts one row per
/// user per date with the current totals plus the per-asset-class composition below. The
/// composition fields (<see cref="AccountsBalance"/>, <see cref="InvestmentsBalance"/>,
/// <see cref="CreditCardDebt"/>) are consumed directly by the Net Worth screen's composition view
/// (06-06) so historical dates don't need to be recomputed live from Accounts/Investments/cards.
/// </summary>
public class NetWorthSnapshot : UserOwnedEntity
{
    /// <summary>UTC midnight of the snapshot day.</summary>
    public DateTime Date { get; set; }

    public decimal TotalAssets { get; set; }

    public decimal TotalLiabilities { get; set; }

    public decimal NetWorth { get; set; }

    /// <summary>Sum of non-hidden account balances on this snapshot's date.</summary>
    public decimal AccountsBalance { get; set; }

    /// <summary>Sum of investment current values on this snapshot's date.</summary>
    public decimal InvestmentsBalance { get; set; }

    /// <summary>Sum of outstanding credit card debt on this snapshot's date.</summary>
    public decimal CreditCardDebt { get; set; }
}
