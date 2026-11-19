using FluentValidation;

namespace SimpleFinance.Application.Accounts.ToggleHide;

/// <summary>
/// Rejects a request body missing <c>isHidden</c> entirely (nullable bool left null) with a 400
/// via the existing ValidationExceptionMiddleware -- an explicit <c>true</c>/<c>false</c> value is
/// always required, never inferred.
/// </summary>
public sealed class ToggleAccountHideValidator : AbstractValidator<ToggleAccountHideCommand>
{
    public ToggleAccountHideValidator()
    {
        RuleFor(x => x.IsHidden).NotNull();
    }
}
