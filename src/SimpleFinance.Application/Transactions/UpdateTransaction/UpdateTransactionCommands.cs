using FluentValidation;

namespace SimpleFinance.Application.Transactions.UpdateTransaction;

/// <summary>
/// TXN-03/04: the four field-scoped PATCH mutations off the transaction detail's kebab menu.
/// Every command that touches CategoryId or IsTransfer sets the matching provenance field
/// (CategorySource.UserOverride / IsTransferManualOverride) in the endpoint handler, not here --
/// these are pure request DTOs + input-shape validation.
///
/// RecategorizeTransactionCommand's own validator (needs SimpleFinanceDbContext to assert
/// ownership of a non-null CategoryId) is NOT in this file -- it lives in
/// SimpleFinance.Api.Endpoints.TransactionsEndpoints alongside the endpoint handlers, because
/// Application cannot reference Infrastructure (SimpleFinanceDbContext's assembly) without
/// creating a circular project reference, per the established Infrastructure-namespaced-DB-code
/// precedent (02-04-SUMMARY.md/03-02-SUMMARY.md).
/// </summary>
public sealed record RecategorizeTransactionCommand(Guid? CategoryId);

public sealed record UpdateTransactionNoteCommand(string? Note);

/// <summary>
/// Nullable (not the plan's literal non-nullable bool) so a missing <c>isHidden</c> field in the
/// request body actually 400s via NotNull(), instead of silently defaulting to false -- mirrors
/// 03-01's ToggleAccountHideCommand precedent (STATE.md decision log).
/// </summary>
public sealed record ToggleTransactionHiddenCommand(bool? IsHidden);

/// <summary>Nullable for the same reason as <see cref="ToggleTransactionHiddenCommand"/>.</summary>
public sealed record ToggleTransactionTransferCommand(bool? IsTransfer);

public sealed class UpdateTransactionNoteValidator : AbstractValidator<UpdateTransactionNoteCommand>
{
    // T-03-12: bounds unbounded free-text input. Transaction.Notes has no HasMaxLength on its EF
    // Core column today (unbounded Postgres `text`) -- flagged here as a follow-up: add an
    // explicit HasMaxLength(1000) to TransactionConfiguration in a later migration so the DB
    // constraint matches this validator's bound instead of relying on FluentValidation alone.
    public UpdateTransactionNoteValidator()
    {
        RuleFor(x => x.Note).MaximumLength(1000);
    }
}

public sealed class ToggleTransactionHiddenValidator : AbstractValidator<ToggleTransactionHiddenCommand>
{
    public ToggleTransactionHiddenValidator()
    {
        RuleFor(x => x.IsHidden).NotNull();
    }
}

public sealed class ToggleTransactionTransferValidator : AbstractValidator<ToggleTransactionTransferCommand>
{
    public ToggleTransactionTransferValidator()
    {
        RuleFor(x => x.IsTransfer).NotNull();
    }
}
