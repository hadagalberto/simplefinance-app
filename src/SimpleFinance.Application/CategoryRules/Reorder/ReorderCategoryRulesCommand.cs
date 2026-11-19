namespace SimpleFinance.Application.CategoryRules.Reorder;

/// <summary>
/// CAT-03: reorders every one of the caller's CategoryRules in one shot. <see cref="OrderedIds"/>
/// must contain every one of the user's existing rule ids exactly once (enforced by
/// ReorderCategoryRulesValidator, Api project -- DB-dependent, same circular-reference rationale
/// as Create/UpdateCategoryRuleValidator) -- a partial/stale array would otherwise silently drop a
/// rule out of the priority sequence. The handler sets each rule's Priority to its zero-based
/// index in this array.
/// </summary>
public sealed record ReorderCategoryRulesCommand(Guid[] OrderedIds);
