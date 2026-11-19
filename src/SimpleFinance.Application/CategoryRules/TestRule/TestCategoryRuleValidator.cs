using FluentValidation;
using SimpleFinance.Domain.Enums;

namespace SimpleFinance.Application.CategoryRules.TestRule;

/// <summary>
/// Same field/operator/value combination rules as CreateCategoryRuleValidator/
/// UpdateCategoryRuleValidator (shared via CategoryRuleValidationRules), minus the categoryId
/// ownership check -- a draft test-rule has no CategoryId. No DbContext dependency needed here,
/// so (unlike Create/Update/Reorder) this validator lives in the Application project and is
/// picked up automatically by Program.cs's
/// AddValidatorsFromAssemblyContaining&lt;SetupUserValidator&gt;() assembly scan.
/// </summary>
public sealed class TestCategoryRuleValidator : AbstractValidator<TestCategoryRuleCommand>
{
    public TestCategoryRuleValidator()
    {
        RuleFor(x => x.MatchField)
            .Must(field => Enum.TryParse<RuleMatchField>(field, ignoreCase: true, out _))
            .WithMessage("matchField must be one of: Description, Merchant, PluggyCategory, Amount.");

        RuleFor(x => x.MatchOperator)
            .Must(op => Enum.TryParse<RuleMatchOperator>(op, ignoreCase: true, out _))
            .WithMessage("matchOperator must be one of: Contains, Equals, Regex, Range.");

        RuleFor(x => x)
            .Must(cmd => !CategoryRuleValidationRules.TryParseFields(cmd.MatchField, cmd.MatchOperator, out var field, out var op)
                || CategoryRuleValidationRules.HasValidFieldOperatorCombination(field, op, cmd.MatchValue, cmd.AmountMin, cmd.AmountMax))
            .WithMessage("Invalid matchField/matchOperator combination, or a required matchValue/amountMin/amountMax is missing.");

        RuleFor(x => x.MatchValue)
            .Must(v => !CategoryRuleValidationRules.ExceedsRegexMaxLength(v))
            .When(IsRegex)
            .WithMessage("matchValue (regex pattern) must not exceed 200 characters.");

        RuleFor(x => x.MatchValue)
            .Must(CategoryRuleValidationRules.IsValidRegexPattern)
            .When(IsRegex)
            .WithMessage("matchValue is not a valid regular expression.");
    }

    private static bool IsRegex(TestCategoryRuleCommand cmd) =>
        Enum.TryParse<RuleMatchOperator>(cmd.MatchOperator, ignoreCase: true, out var op)
        && CategoryRuleValidationRules.IsRegexOperator(op);
}
