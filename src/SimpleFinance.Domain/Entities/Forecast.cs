using SimpleFinance.Domain.Common;

namespace SimpleFinance.Domain.Entities;

/// <summary>
/// A deterministic month forecast (Phase 19, AI-FCT-01): projected income/expense/balance for a given
/// month (plus a min/max range and a per-category driver breakdown) computed by the daily
/// <c>AiForecastJob</c> from recurring transactions + trailing historical averages + already-realized
/// transactions. The NUMBERS are deterministic and authoritative — stored on the entity and in
/// <see cref="ByCategoryJson"/> (a jsonb blob); the LLM only writes the optional <see cref="Narrative"/>
/// prose, never the figures.
///
/// One forecast exists per <c>(UserId, Month)</c> (a UNIQUE index) and the daily job REPLACES it in place
/// each run — <see cref="Forecast"/> is deliberately NOT skip-if-exists like <see cref="AiInsight"/>, so
/// the current-month projection is overwritten (never frozen at its day-1 value). Per-user (inherits
/// <see cref="UserOwnedEntity"/> for UserId/Id/CreatedAt/UpdatedAt/DeletedAt).
/// </summary>
public class Forecast : UserOwnedEntity
{
    /// <summary>The month this forecast covers, normalized to the 1st of the month (UTC).</summary>
    public DateTime Month { get; set; }

    /// <summary>Projected total income for the month (deterministic — recurring credits + historical average).</summary>
    public decimal ProjectedIncome { get; set; }

    /// <summary>Projected total expense for the month (deterministic — recurring debits + trailing 3-month averages).</summary>
    public decimal ProjectedExpense { get; set; }

    /// <summary>Projected end-of-month balance (income − expense, folding already-realized transactions).</summary>
    public decimal ProjectedBalance { get; set; }

    /// <summary>Lower bound of the projected-balance range (variance-derived).</summary>
    public decimal BalanceLow { get; set; }

    /// <summary>Upper bound of the projected-balance range (variance-derived).</summary>
    public decimal BalanceHigh { get; set; }

    /// <summary>Per-category projection drivers as a compact jsonb document — the authoritative numbers
    /// the UI displays (never trust the model for numbers).</summary>
    public string ByCategoryJson { get; set; } = "{}";

    /// <summary>How the projection was computed (e.g. "recurring+3mo-avg"), for transparency/auditing.</summary>
    public string Method { get; set; } = "recurring+3mo-avg";

    /// <summary>Optional 1-2 sentence PT-BR explanation written by the LLM (degrade-safe — null when the
    /// budget cap is hit; the forecast numbers still persist).</summary>
    public string? Narrative { get; set; }
}
