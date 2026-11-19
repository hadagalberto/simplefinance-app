using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using FluentValidation;
using Microsoft.EntityFrameworkCore;
using SimpleFinance.Domain.Enums;
using SimpleFinance.Infrastructure.Alerts;
using SimpleFinance.Infrastructure.Persistence;

namespace SimpleFinance.Api.Endpoints;

/// <summary>
/// NOTIF-02/CFG-01: full AlertRule CRUD (create/edit/toggle-active/delete) -- thin HTTP wrapper
/// over AlertRuleService, zero business logic here. Mirrors CategoryRulesEndpoints.cs's exact
/// group/RequireAuthorization/GetUserId/ValidateWithUserAsync structure.
///
/// T-08-10: every write is scoped by both id and the caller's own UserId (via AlertRuleService's
/// not-found contract) -- an id belonging to another user 404s rather than leaking existence or
/// being mutable (IDOR).
/// </summary>
public static class AlertRulesEndpoints
{
    public static void MapAlertRulesEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/alert-rules").RequireAuthorization();
        group.MapGet("", GetAlertRulesAsync);
        group.MapPost("", CreateAlertRuleAsync);
        group.MapPut("/{id:guid}", UpdateAlertRuleAsync);
        group.MapPatch("/{id:guid}/active", ToggleAlertRuleActiveAsync);
        group.MapDelete("/{id:guid}", DeleteAlertRuleAsync);
    }

    private static async Task<IResult> GetAlertRulesAsync(
        AlertRuleService alertRuleService,
        ClaimsPrincipal principal,
        CancellationToken ct)
    {
        var userId = GetUserId(principal);
        if (userId is null)
        {
            return Results.Unauthorized();
        }

        var rules = await alertRuleService.GetAlertRulesAsync(userId.Value, ct);
        return Results.Ok(rules);
    }

    private static async Task<IResult> CreateAlertRuleAsync(
        CreateAlertRuleCommand command,
        IValidator<CreateAlertRuleCommand> validator,
        AlertRuleService alertRuleService,
        ClaimsPrincipal principal,
        CancellationToken ct)
    {
        var userId = GetUserId(principal);
        if (userId is null)
        {
            return Results.Unauthorized();
        }

        await ValidateWithUserAsync(validator, command, userId.Value, ct);

        var rule = await alertRuleService.CreateAlertRuleAsync(
            userId.Value,
            Enum.Parse<AlertRuleType>(command.Type, ignoreCase: true),
            command.Threshold,
            command.CategoryId,
            command.AccountId,
            command.DaysBefore,
            Enum.Parse<NotificationChannelType>(command.Channel, ignoreCase: true),
            command.IsActive,
            ct);

        return Results.Created($"/api/alert-rules/{rule.Id}", rule);
    }

    private static async Task<IResult> UpdateAlertRuleAsync(
        Guid id,
        UpdateAlertRuleCommand command,
        IValidator<UpdateAlertRuleCommand> validator,
        AlertRuleService alertRuleService,
        ClaimsPrincipal principal,
        CancellationToken ct)
    {
        var userId = GetUserId(principal);
        if (userId is null)
        {
            return Results.Unauthorized();
        }

        await ValidateWithUserAsync(validator, command, userId.Value, ct);

        var rule = await alertRuleService.UpdateAlertRuleAsync(
            userId.Value,
            id,
            Enum.Parse<AlertRuleType>(command.Type, ignoreCase: true),
            command.Threshold,
            command.CategoryId,
            command.AccountId,
            command.DaysBefore,
            Enum.Parse<NotificationChannelType>(command.Channel, ignoreCase: true),
            command.IsActive,
            ct);

        return rule is null ? Results.NotFound() : Results.Ok(rule);
    }

    private static async Task<IResult> ToggleAlertRuleActiveAsync(
        Guid id,
        ToggleAlertRuleActiveRequest request,
        AlertRuleService alertRuleService,
        ClaimsPrincipal principal,
        CancellationToken ct)
    {
        var userId = GetUserId(principal);
        if (userId is null)
        {
            return Results.Unauthorized();
        }

        var rule = await alertRuleService.ToggleActiveAsync(userId.Value, id, request.IsActive, ct);
        return rule is null ? Results.NotFound() : Results.Ok(rule);
    }

    private static async Task<IResult> DeleteAlertRuleAsync(
        Guid id,
        AlertRuleService alertRuleService,
        ClaimsPrincipal principal,
        CancellationToken ct)
    {
        var userId = GetUserId(principal);
        if (userId is null)
        {
            return Results.Unauthorized();
        }

        var deleted = await alertRuleService.DeleteAlertRuleAsync(userId.Value, id, ct);
        return deleted ? Results.NoContent() : Results.NotFound();
    }

    // Threads the authenticated caller's UserId into FluentValidation's RootContextData so a
    // DB-dependent MustAsync rule (CreateAlertRuleValidator/UpdateAlertRuleValidator's CategoryId/
    // AccountId ownership checks) can scope its check without the command itself carrying UserId.
    // Mirrors CategoryRulesEndpoints.ValidateWithUserAsync<T> exactly.
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

public sealed record CreateAlertRuleCommand(
    string Type,
    decimal? Threshold,
    Guid? CategoryId,
    Guid? AccountId,
    int? DaysBefore,
    string Channel,
    bool IsActive);

public sealed record UpdateAlertRuleCommand(
    string Type,
    decimal? Threshold,
    Guid? CategoryId,
    Guid? AccountId,
    int? DaysBefore,
    string Channel,
    bool IsActive);

public sealed record ToggleAlertRuleActiveRequest(bool IsActive);

/// <summary>
/// T-08-11: CategoryId/AccountId, when set, must exist and belong to the caller -- a rule can
/// never be scoped to another user's category/account. Threshold is required (greater than 0)
/// only for LowBalance/LargeTransaction (AlertRule.Threshold's documented applicability);
/// DaysBefore is required (1-90 inclusive) only for InvoiceDue (AlertRule.DaysBefore's documented
/// applicability). Lives in the Api project (not Application) because it needs
/// SimpleFinanceDbContext for the CategoryId/AccountId ownership MustAsync checks, and
/// Application cannot reference Infrastructure without a circular project reference (03-05/04-03/
/// 07-04 precedent). The caller's UserId is threaded in via ValidationContext.RootContextData
/// (AlertRulesEndpoints.ValidateWithUserAsync).
/// </summary>
public sealed class CreateAlertRuleValidator : AbstractValidator<CreateAlertRuleCommand>
{
    public CreateAlertRuleValidator(SimpleFinanceDbContext db)
    {
        RuleFor(x => x.Type)
            .Must(type => Enum.TryParse<AlertRuleType>(type, ignoreCase: true, out _))
            .WithMessage("type must be one of: BudgetThreshold, LowBalance, InvoiceDue, LargeTransaction, NewRecurring, SyncError, GoalReached.");

        RuleFor(x => x.Channel)
            .Must(channel => Enum.TryParse<NotificationChannelType>(channel, ignoreCase: true, out _))
            .WithMessage("channel must be one of: Telegram, Email, Both.");

        RuleFor(x => x.Threshold)
            .Must(threshold => threshold is > 0m)
            .When(IsThresholdRequired)
            .WithMessage("threshold is required (and must be greater than 0) for LowBalance/LargeTransaction alert types.");

        RuleFor(x => x.DaysBefore)
            .Must(daysBefore => daysBefore is >= 1 and <= 90)
            .When(IsDaysBeforeRequired)
            .WithMessage("daysBefore is required (1-90) for the InvoiceDue alert type.");

        RuleFor(x => x.CategoryId)
            .MustAsync(async (_, categoryId, context, ct) =>
            {
                if (categoryId is null)
                {
                    return true;
                }

                var userId = (Guid)context.RootContextData["UserId"]!;
                return await db.Categories.AnyAsync(c => c.Id == categoryId && c.UserId == userId, ct);
            })
            .WithMessage("categoryId does not exist or does not belong to the caller.");

        RuleFor(x => x.AccountId)
            .MustAsync(async (_, accountId, context, ct) =>
            {
                if (accountId is null)
                {
                    return true;
                }

                var userId = (Guid)context.RootContextData["UserId"]!;
                return await db.Accounts.AnyAsync(a => a.Id == accountId && a.UserId == userId, ct);
            })
            .WithMessage("accountId does not exist or does not belong to the caller.");
    }

    private static bool IsThresholdRequired(CreateAlertRuleCommand cmd) =>
        Enum.TryParse<AlertRuleType>(cmd.Type, ignoreCase: true, out var type)
        && (type == AlertRuleType.LowBalance || type == AlertRuleType.LargeTransaction);

    private static bool IsDaysBeforeRequired(CreateAlertRuleCommand cmd) =>
        Enum.TryParse<AlertRuleType>(cmd.Type, ignoreCase: true, out var type)
        && type == AlertRuleType.InvoiceDue;
}

/// <summary>Same Type/Channel/Threshold/DaysBefore/ownership rules as
/// <see cref="CreateAlertRuleValidator"/>, same circular-reference rationale for living here
/// instead of Application.</summary>
public sealed class UpdateAlertRuleValidator : AbstractValidator<UpdateAlertRuleCommand>
{
    public UpdateAlertRuleValidator(SimpleFinanceDbContext db)
    {
        RuleFor(x => x.Type)
            .Must(type => Enum.TryParse<AlertRuleType>(type, ignoreCase: true, out _))
            .WithMessage("type must be one of: BudgetThreshold, LowBalance, InvoiceDue, LargeTransaction, NewRecurring, SyncError, GoalReached.");

        RuleFor(x => x.Channel)
            .Must(channel => Enum.TryParse<NotificationChannelType>(channel, ignoreCase: true, out _))
            .WithMessage("channel must be one of: Telegram, Email, Both.");

        RuleFor(x => x.Threshold)
            .Must(threshold => threshold is > 0m)
            .When(IsThresholdRequired)
            .WithMessage("threshold is required (and must be greater than 0) for LowBalance/LargeTransaction alert types.");

        RuleFor(x => x.DaysBefore)
            .Must(daysBefore => daysBefore is >= 1 and <= 90)
            .When(IsDaysBeforeRequired)
            .WithMessage("daysBefore is required (1-90) for the InvoiceDue alert type.");

        RuleFor(x => x.CategoryId)
            .MustAsync(async (_, categoryId, context, ct) =>
            {
                if (categoryId is null)
                {
                    return true;
                }

                var userId = (Guid)context.RootContextData["UserId"]!;
                return await db.Categories.AnyAsync(c => c.Id == categoryId && c.UserId == userId, ct);
            })
            .WithMessage("categoryId does not exist or does not belong to the caller.");

        RuleFor(x => x.AccountId)
            .MustAsync(async (_, accountId, context, ct) =>
            {
                if (accountId is null)
                {
                    return true;
                }

                var userId = (Guid)context.RootContextData["UserId"]!;
                return await db.Accounts.AnyAsync(a => a.Id == accountId && a.UserId == userId, ct);
            })
            .WithMessage("accountId does not exist or does not belong to the caller.");
    }

    private static bool IsThresholdRequired(UpdateAlertRuleCommand cmd) =>
        Enum.TryParse<AlertRuleType>(cmd.Type, ignoreCase: true, out var type)
        && (type == AlertRuleType.LowBalance || type == AlertRuleType.LargeTransaction);

    private static bool IsDaysBeforeRequired(UpdateAlertRuleCommand cmd) =>
        Enum.TryParse<AlertRuleType>(cmd.Type, ignoreCase: true, out var type)
        && type == AlertRuleType.InvoiceDue;
}
