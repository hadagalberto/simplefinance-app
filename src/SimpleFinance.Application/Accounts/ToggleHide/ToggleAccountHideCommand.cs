namespace SimpleFinance.Application.Accounts.ToggleHide;

/// <summary>
/// ACCT-03: hides/unhides an Account from totals and dashboard aggregates (Account.IsHidden).
/// <see cref="IsHidden"/> is nullable so a JSON body that omits the field can be distinguished
/// from an explicit <c>false</c> and rejected by <see cref="ToggleAccountHideValidator"/> -- a
/// non-nullable bool would silently default to false on a missing field, masking a malformed
/// request as a valid "unhide" instead of the 400 the plan's own behavior spec requires.
/// </summary>
public sealed record ToggleAccountHideCommand(bool? IsHidden);
