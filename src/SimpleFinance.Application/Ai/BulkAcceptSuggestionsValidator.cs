using FluentValidation;

namespace SimpleFinance.Application.Ai;

/// <summary>
/// AI-CAT-06 (T-16-13): validates POST /api/ai/suggestions/bulk-accept input. Pure structural checks
/// with no DB dependency, so it lives in Application and is auto-scanned by Program.cs's
/// <c>AddValidatorsFromAssemblyContaining&lt;SetupUserValidator&gt;()</c>. Invoked manually by
/// AiEndpoints (never FluentValidation.AspNetCore's deprecated auto-pipeline, per STACK.md). Caps the
/// batch size so a single request can't queue an unbounded write set.
/// </summary>
public sealed class BulkAcceptSuggestionsValidator : AbstractValidator<BulkAcceptSuggestionsCommand>
{
    /// <summary>Max ids accepted per bulk call — bounds the write set of one request (T-16-13).</summary>
    public const int MaxIds = 500;

    public BulkAcceptSuggestionsValidator()
    {
        RuleFor(x => x.Ids)
            .NotNull()
            .NotEmpty()
            .WithMessage("'Ids' must contain at least one suggestion id.");

        RuleFor(x => x.Ids)
            .Must(ids => ids is null || ids.Count <= MaxIds)
            .WithMessage($"'Ids' must not contain more than {MaxIds} entries.");

        RuleForEach(x => x.Ids)
            .NotEqual(Guid.Empty)
            .WithMessage("'Ids' must not contain an empty guid.");
    }
}
