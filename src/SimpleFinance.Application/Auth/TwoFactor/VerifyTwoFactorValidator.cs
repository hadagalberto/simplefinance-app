using FluentValidation;

namespace SimpleFinance.Application.Auth.TwoFactor;

public sealed class VerifyTwoFactorValidator : AbstractValidator<VerifyTwoFactorCommand>
{
    public VerifyTwoFactorValidator()
    {
        RuleFor(x => x.PendingToken).NotEmpty();
        RuleFor(x => x.Code).NotEmpty();
    }
}
