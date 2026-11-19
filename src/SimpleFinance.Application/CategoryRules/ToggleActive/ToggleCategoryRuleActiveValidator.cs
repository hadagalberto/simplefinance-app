using FluentValidation;

namespace SimpleFinance.Application.CategoryRules.ToggleActive;

/// <summary>
/// Rejects a request body missing <c>isActive</c> entirely (nullable bool left null) with a 400
/// via the existing ValidationExceptionMiddleware. No DbContext dependency -- picked up
/// automatically by Program.cs's AddValidatorsFromAssemblyContaining&lt;SetupUserValidator&gt;()
/// assembly scan.
/// </summary>
public sealed class ToggleCategoryRuleActiveValidator : AbstractValidator<ToggleCategoryRuleActiveCommand>
{
    public ToggleCategoryRuleActiveValidator()
    {
        RuleFor(x => x.IsActive).NotNull();
    }
}
