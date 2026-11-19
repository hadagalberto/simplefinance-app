using SimpleFinance.Domain.Common;
using SimpleFinance.Domain.Enums;

namespace SimpleFinance.Domain.Entities;

/// <summary>
/// A single Pluggy transaction row, per PRD §11.4. Supports SYNC-04/SYNC-05/SYNC-06.
/// Idempotent upsert key is (AccountId, PluggyTransactionId) — enforced by a unique DB
/// index in TransactionConfiguration, the hard backstop behind SYNC-04's "never duplicates"
/// requirement.
/// </summary>
public class Transaction : UserOwnedEntity
{
    public Guid AccountId { get; set; }

    /// <summary>Pluggy's Transaction UUID. Unique per (AccountId, PluggyTransactionId).</summary>
    public string PluggyTransactionId { get; set; } = string.Empty;

    public DateTime Date { get; set; }

    public string Description { get; set; } = string.Empty;

    public string? DescriptionRaw { get; set; }

    public decimal Amount { get; set; }

    public TransactionType Type { get; set; }

    public string CurrencyCode { get; set; } = string.Empty;

    public decimal? AmountInAccountCurrency { get; set; }

    public decimal? BalanceAfter { get; set; }

    public TransactionStatus Status { get; set; }

    /// <summary>Nullable — 02-RESEARCH.md flags this field as possibly Pro-tier-gated.</summary>
    public string? MerchantName { get; set; }

    public string? MerchantDocument { get; set; }

    public string? PluggyCategory { get; set; }

    /// <summary>Nullable — 02-RESEARCH.md flags this field as possibly Pro-tier-gated.</summary>
    public string? PluggyCategoryId { get; set; }

    /// <summary>FK to Category. No navigation property required.</summary>
    public Guid? CategoryId { get; set; }

    public CategorySource CategorySource { get; set; }

    /// <summary>
    /// AI cascade confidence (0..1) for the current <see cref="CategoryId"/> when it was assigned
    /// by AI (AI-CAT-03). Null when the category came from a deterministic source or is unset.
    /// </summary>
    public double? CategoryConfidence { get; set; }

    /// <summary>
    /// Name of the model that produced the AI category (AI-CAT-03), e.g. the kNN embedding model
    /// or the LLM chat model. Null for deterministic/unset categories. Config-driven — never
    /// hardcoded.
    /// </summary>
    public string? CategoryModel { get; set; }

    /// <summary>Set by Phase 3's internal-transfer detection. Defaults false in Phase 2.</summary>
    public bool IsTransfer { get; set; }

    /// <summary>
    /// True once the user has explicitly toggled <see cref="IsTransfer"/> via the PATCH
    /// .../transfer endpoint (03-05). Protects that explicit choice from being reverted by a
    /// later automatic <c>ITransferDetectionService</c> run -- generalizes PITFALLS.md Pitfall
    /// 9/10's provenance lesson (category overrides must survive re-sync) to the transfer flag.
    /// A transaction with this set to true is permanently excluded from both the paymentData and
    /// fallback detection signals, even if its shape would otherwise match.
    /// </summary>
    public bool IsTransferManualOverride { get; set; }

    public bool IsHidden { get; set; }

    public string? Notes { get; set; }

    public int? InstallmentNumber { get; set; }

    public int? TotalInstallments { get; set; }

    public decimal? TotalInstallmentsAmount { get; set; }

    /// <summary>Appears once a card installment's bill posts (billId per 02-RESEARCH.md).</summary>
    public string? BillId { get; set; }

    /// <summary>
    /// Raw JSON of Pluggy's <c>paymentData</c>, per PRD §11.4 — forward groundwork for Phase 3's
    /// transfer detection.
    /// </summary>
    public string? PaymentDataJson { get; set; }

    /// <summary>
    /// FK to RecurringTransaction. No navigation property required. Set by Phase 7's
    /// RecurringDetectionJob when this transaction is grouped into a detected recurring charge.
    /// </summary>
    public Guid? RecurringTransactionId { get; set; }
}
