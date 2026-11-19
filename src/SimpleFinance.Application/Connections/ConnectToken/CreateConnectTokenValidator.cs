using FluentValidation;

namespace SimpleFinance.Application.Connections.ConnectToken;

/// <summary>
/// ExistingPluggyItemId is optional (null = brand-new connection), but when present it must not
/// be an empty/whitespace string — an accidentally-blank itemId would silently request an
/// update-mode token scoped to nothing.
/// </summary>
public sealed class CreateConnectTokenValidator : AbstractValidator<CreateConnectTokenCommand>
{
    public CreateConnectTokenValidator()
    {
        RuleFor(x => x.ExistingPluggyItemId)
            .NotEmpty()
            .When(x => x.ExistingPluggyItemId is not null)
            .WithMessage("'ExistingPluggyItemId' must not be empty when provided.");
    }
}
