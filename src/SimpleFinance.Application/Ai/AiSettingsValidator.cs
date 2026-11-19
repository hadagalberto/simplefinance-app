using FluentValidation;

namespace SimpleFinance.Application.Ai;

/// <summary>
/// AI-FND-08 (T-15-16): validates PUT /api/ai/settings input. Pure range/non-empty checks with no
/// DB dependency, so it lives in Application and is auto-scanned by Program.cs's
/// <c>AddValidatorsFromAssemblyContaining&lt;SetupUserValidator&gt;()</c>. Invoked manually by
/// AiEndpoints (never FluentValidation.AspNetCore's deprecated auto-pipeline, per STACK.md).
/// </summary>
public sealed class AiSettingsValidator : AbstractValidator<UpdateAiSettingsCommand>
{
    public AiSettingsValidator()
    {
        // Confidence threshold above which an AI categorization auto-applies — a probability, so it
        // must fall in [0, 1] inclusive.
        RuleFor(x => x.AiAutoApplyThreshold)
            .InclusiveBetween(0d, 1d)
            .WithMessage("'AiAutoApplyThreshold' must be between 0 and 1.");

        // Hard monthly USD cap — 0 is valid (disables all cloud calls); negative is nonsensical.
        RuleFor(x => x.AiMonthlyBudgetUsd)
            .GreaterThanOrEqualTo(0m)
            .WithMessage("'AiMonthlyBudgetUsd' must be zero or greater.");

        RuleFor(x => x.AiChatModel)
            .NotEmpty()
            .MaximumLength(64);

        RuleFor(x => x.AiSummaryModel)
            .NotEmpty()
            .MaximumLength(64);

        RuleFor(x => x.AiLanguage)
            .NotEmpty()
            .MaximumLength(16);

        // Write-only provider keys: null (unchanged) and "" (clear) are both valid — only cap the length
        // of a provided non-empty value so an absurd paste can't bloat the encrypted column. No format
        // check (providers rotate key shapes); the key is never echoed back.
        RuleFor(x => x.DeepSeekApiKey)
            .MaximumLength(200)
            .When(x => !string.IsNullOrEmpty(x.DeepSeekApiKey));

        RuleFor(x => x.OpenAiApiKey)
            .MaximumLength(200)
            .When(x => !string.IsNullOrEmpty(x.OpenAiApiKey));
    }
}
