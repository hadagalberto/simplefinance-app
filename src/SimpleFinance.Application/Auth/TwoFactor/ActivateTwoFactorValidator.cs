using FluentValidation;

namespace SimpleFinance.Application.Auth.TwoFactor;

public sealed class ActivateTwoFactorValidator : AbstractValidator<ActivateTwoFactorCommand>
{
    public ActivateTwoFactorValidator()
    {
        RuleFor(x => x.Code)
            .NotEmpty()
            .Matches("^[0-9]{6}$")
            .WithMessage("'Code' must be exactly 6 digits.");
    }
}
