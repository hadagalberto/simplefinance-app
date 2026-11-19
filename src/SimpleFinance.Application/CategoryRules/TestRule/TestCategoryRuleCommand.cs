namespace SimpleFinance.Application.CategoryRules.TestRule;

/// <summary>
/// CAT-03: a DRAFT rule shape (no Id/CategoryId/Priority/IsActive -- pure match criteria) for the
/// "test rule" preview endpoint. Never persisted -- POST /api/category-rules/test evaluates this
/// against the caller's existing transactions in-memory via RuleMatcher and returns a
/// count+sample, with zero database writes.
/// </summary>
public sealed record TestCategoryRuleCommand(
    string MatchField,
    string MatchOperator,
    string? MatchValue,
    decimal? AmountMin,
    decimal? AmountMax);
