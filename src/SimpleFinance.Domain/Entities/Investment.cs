using SimpleFinance.Domain.Common;
using SimpleFinance.Domain.Enums;

namespace SimpleFinance.Domain.Entities;

/// <summary>
/// A single Pluggy investment position, per PRD §11.7/§6.9. Supports SYNC-04/SYNC-05.
/// Idempotent upsert key is (ConnectionId, PluggyInvestmentId).
/// </summary>
public class Investment : UserOwnedEntity
{
    public Guid ConnectionId { get; set; }

    /// <summary>Pluggy's Investment UUID. Unique per (ConnectionId, PluggyInvestmentId).</summary>
    public string PluggyInvestmentId { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public InvestmentType Type { get; set; }

    public string? Subtype { get; set; }

    public string? Institution { get; set; }

    public string? IssuerName { get; set; }

    public decimal? Balance { get; set; }

    public decimal? Value { get; set; }

    /// <summary>
    /// High-precision quantity (e.g. crypto) — HasPrecision(18, 8) override in
    /// InvestmentConfiguration, never the DbContext's global (18,2) convention.
    /// </summary>
    public decimal? Quantity { get; set; }

    public decimal? AmountInvested { get; set; }

    /// <summary>HasPrecision(9, 6) override in InvestmentConfiguration.</summary>
    public decimal? LastMonthRate { get; set; }

    /// <summary>HasPrecision(9, 6) override in InvestmentConfiguration.</summary>
    public decimal? AnnualRate { get; set; }

    public DateTime? Date { get; set; }

    public DateTime? DueDate { get; set; }

    public string? CurrencyCode { get; set; }
}
