namespace SimpleFinance.Application.Transactions.ApplySimilar;

/// <summary>
/// TXN-03: bulk-assigns an existing Category (assignment only -- rule creation is Phase 4) to
/// every OTHER transaction sharing the source transaction's merchant-or-description match key.
///
/// ApplySimilarCommand's own validator (needs SimpleFinanceDbContext to assert ownership of
/// CategoryId, same T-03-11/T-03-13 mitigation as RecategorizeTransactionCommand) lives in
/// SimpleFinance.Api.Endpoints.TransactionsEndpoints, not here -- see
/// UpdateTransactionCommands.cs's doc comment for the circular-project-reference rationale.
/// </summary>
public sealed record ApplySimilarCommand(Guid CategoryId);
