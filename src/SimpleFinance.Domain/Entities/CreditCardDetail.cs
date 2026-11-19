using SimpleFinance.Domain.Common;
using SimpleFinance.Domain.Enums;

namespace SimpleFinance.Domain.Entities;

/// <summary>
/// 1:1 child of Account for credit-card-type accounts, per PRD §11.3's own schema (no UserId —
/// ownership flows through Account). Supports PLG-03/SYNC-04.
/// </summary>
public class CreditCardDetail : AuditableEntity
{
    public Guid AccountId { get; set; }

    public string? Brand { get; set; }

    public string? Level { get; set; }

    public string? BrandAdditionalInfo { get; set; }

    public decimal? CreditLimit { get; set; }

    public decimal? AvailableCredit { get; set; }

    public DateTime? BalanceCloseDate { get; set; }

    public DateTime? BalanceDueDate { get; set; }

    public decimal? MinimumPayment { get; set; }

    public decimal? LastStatementBalance { get; set; }

    public CreditCardStatus Status { get; set; }

    public string? HolderType { get; set; }
}
