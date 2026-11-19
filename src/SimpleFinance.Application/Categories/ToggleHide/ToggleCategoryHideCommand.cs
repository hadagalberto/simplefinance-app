namespace SimpleFinance.Application.Categories.ToggleHide;

/// <summary>
/// CAT-05: hides/unhides a category (Category.IsHidden), allowed on any category including
/// system ones. <see cref="IsHidden"/> is nullable so a JSON body that omits the field can be
/// distinguished from an explicit <c>false</c> and rejected by
/// <see cref="ToggleCategoryHideValidator"/> -- exact structural copy of
/// ToggleAccountHideCommand's nullable-bool + NotNull() pattern (03-01-SUMMARY.md precedent).
/// </summary>
public sealed record ToggleCategoryHideCommand(bool? IsHidden);
