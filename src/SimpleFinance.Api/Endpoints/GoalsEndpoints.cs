using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using FluentValidation;
using Microsoft.EntityFrameworkCore;
using SimpleFinance.Infrastructure.Goals;
using SimpleFinance.Infrastructure.Persistence;

namespace SimpleFinance.Api.Endpoints;

/// <summary>
/// GOAL-01/02/03: GET/POST/PATCH /api/goals -- thin HTTP wrappers over GoalService (07-04 Task 1),
/// which owns every bit of actual computation/mutation. Mirrors CategoriesEndpoints.cs's exact
/// group/RequireAuthorization/GetUserId/ValidateWithUserAsync structure.
///
/// T-07-08: every mutation is scoped by both id and the caller's own UserId (via GoalService's
/// not-found contract) -- an id belonging to another user 404s rather than leaking existence or
/// being mutable (IDOR).
/// </summary>
public static class GoalsEndpoints
{
    public static void MapGoalsEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/goals").RequireAuthorization();
        group.MapGet("", GetGoalsAsync);
        group.MapPost("", CreateGoalAsync);
        group.MapPatch("/{id:guid}", UpdateGoalAsync);
        group.MapPatch("/{id:guid}/progress", UpdateGoalProgressAsync);
        group.MapPatch("/{id:guid}/archive", ArchiveGoalAsync);
    }

    private static async Task<IResult> GetGoalsAsync(
        GoalService goalService,
        ClaimsPrincipal principal,
        CancellationToken ct)
    {
        var userId = GetUserId(principal);
        if (userId is null)
        {
            return Results.Unauthorized();
        }

        var goals = await goalService.GetGoalsAsync(userId.Value, ct);
        return Results.Ok(goals);
    }

    private static async Task<IResult> CreateGoalAsync(
        CreateGoalCommand command,
        IValidator<CreateGoalCommand> validator,
        GoalService goalService,
        ClaimsPrincipal principal,
        CancellationToken ct)
    {
        var userId = GetUserId(principal);
        if (userId is null)
        {
            return Results.Unauthorized();
        }

        await ValidateWithUserAsync(validator, command, userId.Value, ct);

        var goal = await goalService.CreateGoalAsync(
            userId.Value,
            command.Name,
            command.TargetAmount,
            command.TargetDate,
            command.LinkedAccountId,
            command.LinkedInvestmentId,
            command.Icon,
            command.Color,
            ct);

        return Results.Created($"/api/goals/{goal.Id}", goal);
    }

    private static async Task<IResult> UpdateGoalAsync(
        Guid id,
        UpdateGoalCommand command,
        IValidator<UpdateGoalCommand> validator,
        GoalService goalService,
        ClaimsPrincipal principal,
        CancellationToken ct)
    {
        var userId = GetUserId(principal);
        if (userId is null)
        {
            return Results.Unauthorized();
        }

        await ValidateWithUserAsync(validator, command, userId.Value, ct);

        var goal = await goalService.UpdateGoalAsync(
            userId.Value,
            id,
            command.Name,
            command.TargetAmount,
            command.TargetDate,
            command.LinkedAccountId,
            command.LinkedInvestmentId,
            command.Icon,
            command.Color,
            ct);

        return goal is null ? Results.NotFound() : Results.Ok(goal);
    }

    private static async Task<IResult> UpdateGoalProgressAsync(
        Guid id,
        UpdateGoalProgressRequest request,
        GoalService goalService,
        ClaimsPrincipal principal,
        CancellationToken ct)
    {
        var userId = GetUserId(principal);
        if (userId is null)
        {
            return Results.Unauthorized();
        }

        var goal = await goalService.UpdateProgressAsync(userId.Value, id, request.CurrentAmount, ct);

        // T-07-09: GoalService returns null both for a missing/other-user goal AND for a linked
        // goal (whose progress can never be hand-set) -- both map to the same 400 here, no need
        // to distinguish (per the plan's explicit contract).
        return goal is null
            ? Results.BadRequest(new { error = "Goal not found, or progress is derived automatically for linked goals." })
            : Results.Ok(goal);
    }

    private static async Task<IResult> ArchiveGoalAsync(
        Guid id,
        GoalService goalService,
        ClaimsPrincipal principal,
        CancellationToken ct)
    {
        var userId = GetUserId(principal);
        if (userId is null)
        {
            return Results.Unauthorized();
        }

        var goal = await goalService.ArchiveGoalAsync(userId.Value, id, ct);
        return goal is null ? Results.NotFound() : Results.Ok(goal);
    }

    // Threads the authenticated caller's UserId into FluentValidation's RootContextData so the
    // DB-dependent MustAsync rules (CreateGoalValidator/UpdateGoalValidator's LinkedAccountId/
    // LinkedInvestmentId ownership checks) can scope themselves without the command itself
    // carrying UserId. Mirrors CategoriesEndpoints.ValidateWithUserAsync<T> exactly.
    private static async Task ValidateWithUserAsync<T>(IValidator<T> validator, T command, Guid userId, CancellationToken ct)
    {
        var context = new ValidationContext<T>(command);
        context.RootContextData["UserId"] = userId;
        var result = await validator.ValidateAsync(context, ct);
        if (!result.IsValid)
        {
            throw new ValidationException(result.Errors);
        }
    }

    private static Guid? GetUserId(ClaimsPrincipal principal)
    {
        var subClaim = principal.FindFirst(JwtRegisteredClaimNames.Sub)?.Value;
        return subClaim is not null && Guid.TryParse(subClaim, out var userId) ? userId : null;
    }
}

public sealed record CreateGoalCommand(
    string Name,
    decimal TargetAmount,
    DateTime? TargetDate,
    Guid? LinkedAccountId,
    Guid? LinkedInvestmentId,
    string? Icon,
    string? Color);

public sealed record UpdateGoalCommand(
    string Name,
    decimal TargetAmount,
    DateTime? TargetDate,
    Guid? LinkedAccountId,
    Guid? LinkedInvestmentId,
    string? Icon,
    string? Color);

public sealed record UpdateGoalProgressRequest(decimal CurrentAmount);

/// <summary>
/// T-07-07: LinkedAccountId/LinkedInvestmentId, when set, must exist and belong to the caller --
/// an attempt to link another user's account/investment is rejected with a 400, never silently
/// accepted (mirrors CreateCategoryValidator's parentCategoryId mitigation exactly). Also
/// enforces Name (non-empty, &lt;=128 chars, matching GoalConfiguration.cs's HasMaxLength(128)),
/// TargetAmount &gt; 0, TargetDate null or strictly in the future, and rejects a request with BOTH
/// LinkedAccountId and LinkedInvestmentId set. Lives in the Api project (not Application) because
/// it needs SimpleFinanceDbContext, and Application cannot reference Infrastructure without a
/// circular project reference (02-04-SUMMARY.md/03-02-SUMMARY.md/04-03-SUMMARY.md precedent). The
/// caller's UserId is threaded in via ValidationContext.RootContextData
/// (GoalsEndpoints.ValidateWithUserAsync).
/// </summary>
public sealed class CreateGoalValidator : AbstractValidator<CreateGoalCommand>
{
    public CreateGoalValidator(SimpleFinanceDbContext db)
    {
        RuleFor(x => x.Name).NotEmpty().MaximumLength(128);

        RuleFor(x => x.TargetAmount).GreaterThan(0m);

        RuleFor(x => x.TargetDate)
            .Must(targetDate => targetDate is null || targetDate.Value.Date > DateTime.UtcNow.Date)
            .WithMessage("targetDate must be null or strictly in the future.");

        RuleFor(x => x.LinkedAccountId)
            .Must((command, linkedAccountId) => linkedAccountId is null || command.LinkedInvestmentId is null)
            .WithMessage("a goal can link an account or an investment, not both.");

        RuleFor(x => x.LinkedAccountId)
            .MustAsync(async (_, linkedAccountId, context, ct) =>
            {
                if (linkedAccountId is null)
                {
                    return true;
                }

                var userId = (Guid)context.RootContextData["UserId"]!;
                return await db.Accounts.AnyAsync(a => a.Id == linkedAccountId.Value && a.UserId == userId, ct);
            })
            .WithMessage("linkedAccountId does not exist or does not belong to the caller.");

        RuleFor(x => x.LinkedInvestmentId)
            .MustAsync(async (_, linkedInvestmentId, context, ct) =>
            {
                if (linkedInvestmentId is null)
                {
                    return true;
                }

                var userId = (Guid)context.RootContextData["UserId"]!;
                return await db.Investments.AnyAsync(i => i.Id == linkedInvestmentId.Value && i.UserId == userId, ct);
            })
            .WithMessage("linkedInvestmentId does not exist or does not belong to the caller.");
    }
}

/// <summary>Same Name/TargetAmount/TargetDate/both-linked/ownership rules as
/// <see cref="CreateGoalValidator"/>, same circular-reference rationale for living here instead
/// of Application.</summary>
public sealed class UpdateGoalValidator : AbstractValidator<UpdateGoalCommand>
{
    public UpdateGoalValidator(SimpleFinanceDbContext db)
    {
        RuleFor(x => x.Name).NotEmpty().MaximumLength(128);

        RuleFor(x => x.TargetAmount).GreaterThan(0m);

        RuleFor(x => x.TargetDate)
            .Must(targetDate => targetDate is null || targetDate.Value.Date > DateTime.UtcNow.Date)
            .WithMessage("targetDate must be null or strictly in the future.");

        RuleFor(x => x.LinkedAccountId)
            .Must((command, linkedAccountId) => linkedAccountId is null || command.LinkedInvestmentId is null)
            .WithMessage("a goal can link an account or an investment, not both.");

        RuleFor(x => x.LinkedAccountId)
            .MustAsync(async (_, linkedAccountId, context, ct) =>
            {
                if (linkedAccountId is null)
                {
                    return true;
                }

                var userId = (Guid)context.RootContextData["UserId"]!;
                return await db.Accounts.AnyAsync(a => a.Id == linkedAccountId.Value && a.UserId == userId, ct);
            })
            .WithMessage("linkedAccountId does not exist or does not belong to the caller.");

        RuleFor(x => x.LinkedInvestmentId)
            .MustAsync(async (_, linkedInvestmentId, context, ct) =>
            {
                if (linkedInvestmentId is null)
                {
                    return true;
                }

                var userId = (Guid)context.RootContextData["UserId"]!;
                return await db.Investments.AnyAsync(i => i.Id == linkedInvestmentId.Value && i.UserId == userId, ct);
            })
            .WithMessage("linkedInvestmentId does not exist or does not belong to the caller.");
    }
}
