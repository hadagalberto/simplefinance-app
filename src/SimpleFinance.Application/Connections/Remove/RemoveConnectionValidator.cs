using FluentValidation;

namespace SimpleFinance.Application.Connections.Remove;

public sealed class RemoveConnectionValidator : AbstractValidator<RemoveConnectionCommand>
{
    public RemoveConnectionValidator()
    {
        RuleFor(x => x.Confirm)
            .Equal(true)
            .WithMessage("confirmation required to remove a connection");
    }
}
