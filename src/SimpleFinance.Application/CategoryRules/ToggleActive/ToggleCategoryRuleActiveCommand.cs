namespace SimpleFinance.Application.CategoryRules.ToggleActive;

/// <summary>
/// CAT-03: activates/deactivates a CategoryRule (CategoryRule.IsActive) without touching any
/// other field. <see cref="IsActive"/> is nullable so a JSON body that omits the field can be
/// distinguished from an explicit <c>false</c> and rejected by
/// <see cref="ToggleCategoryRuleActiveValidator"/> -- exact structural copy of
/// ToggleCategoryHideCommand/ToggleAccountHideCommand's nullable-bool + NotNull() pattern
/// (03-01/04-03 precedent). Not part of 04-05-PLAN.md's literal files_modified listing -- added
/// as a dedicated command/validator pair (mirroring every other toggle in this codebase) instead
/// of an inline bool parameter, so the PATCH .../active route stays consistent with the rest of
/// the CRUD surface (Rule 2: missing structural consistency, not new behavior).
/// </summary>
public sealed record ToggleCategoryRuleActiveCommand(bool? IsActive);
