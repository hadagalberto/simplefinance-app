using FluentValidation;

namespace SimpleFinance.Application.Categories.ToggleHide;

/// <summary>
/// Rejects a request body missing <c>isHidden</c> entirely (nullable bool left null) with a 400
/// via the existing ValidationExceptionMiddleware -- an explicit <c>true</c>/<c>false</c> value is
/// always required, never inferred. Exact structural copy of ToggleAccountHideValidator.
/// </summary>
public sealed class ToggleCategoryHideValidator : AbstractValidator<ToggleCategoryHideCommand>
{
    public ToggleCategoryHideValidator()
    {
        RuleFor(x => x.IsHidden).NotNull();
    }
}
