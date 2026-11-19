using FluentValidation;

namespace SimpleFinance.Application.Connections.Register;

public sealed class RegisterConnectionValidator : AbstractValidator<RegisterConnectionCommand>
{
    public RegisterConnectionValidator()
    {
        RuleFor(x => x.PluggyItemId).NotEmpty();
    }
}
