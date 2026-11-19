using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using SimpleFinance.Infrastructure.Budgets;

namespace SimpleFinance.Api.Endpoints;

/// <summary>
/// BUD-01..04: GET/POST/DELETE /api/budgets -- thin HTTP wrappers over BudgetService (05-05
/// Task 1), which owns every bit of actual business logic (copy-forward, spend/state
/// computation, ownership checks). Mirrors CategoriesEndpoints.cs/TransactionsEndpoints.cs's
/// exact structure (group + RequireAuthorization + GetUserId helper). No business logic lives in
/// this file -- every handler is 3-6 lines that resolve BudgetService from DI, call the matching
/// method, and map the result.
/// </summary>
public static class BudgetsEndpoints
{
    public static void MapBudgetsEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/budgets").RequireAuthorization();
        group.MapGet("", GetBudgetsAsync);
        group.MapPost("", UpsertBudgetAsync);
        group.MapDelete("/{id:guid}", DeleteBudgetAsync);
    }

    private static async Task<IResult> GetBudgetsAsync(
        int? year,
        int? month,
        BudgetService budgetService,
        ClaimsPrincipal principal,
        CancellationToken ct)
    {
        var userId = GetUserId(principal);
        if (userId is null)
        {
            return Results.Unauthorized();
        }

        // TODO: replace this UTC-now default with Phase 8's CFG-02 user-timezone preference once
        // it exists -- same documented pattern as DailySyncJob's server-local cron time
        // (02-06-PLAN.md).
        var now = DateTime.UtcNow;
        var resolvedYear = year ?? now.Year;
        var resolvedMonth = month ?? now.Month;

        var result = await budgetService.GetBudgetsForMonthAsync(userId.Value, resolvedYear, resolvedMonth, ct);
        return Results.Ok(result);
    }

    private static async Task<IResult> UpsertBudgetAsync(
        UpsertBudgetRequest body,
        BudgetService budgetService,
        ClaimsPrincipal principal,
        CancellationToken ct)
    {
        var userId = GetUserId(principal);
        if (userId is null)
        {
            return Results.Unauthorized();
        }

        // T-05-06: inline validation -- four scalar checks with no DB dependency beyond what
        // BudgetService itself already handles for CategoryId ownership, so no FluentValidation
        // pipeline is warranted here.
        var errors = new Dictionary<string, string[]>();
        if (body.LimitAmount <= 0)
        {
            errors["limitAmount"] = ["limitAmount must be greater than 0."];
        }

        if (body.Month is < 1 or > 12)
        {
            errors["month"] = ["month must be between 1 and 12."];
        }

        if (body.Year is < 2000 or > 2100)
        {
            errors["year"] = ["year must be between 2000 and 2100."];
        }

        if (errors.Count > 0)
        {
            return Results.ValidationProblem(errors);
        }

        var row = await budgetService.UpsertBudgetAsync(
            userId.Value, body.CategoryId, body.Year, body.Month, body.LimitAmount, ct);

        // T-05-07: null means categoryId doesn't exist or doesn't belong to the caller --
        // BudgetService never throws for this expected case.
        return row is null
            ? Results.BadRequest(new { error = "categoryId does not exist or does not belong to the caller." })
            : Results.Ok(row);
    }

    private static async Task<IResult> DeleteBudgetAsync(
        Guid id,
        BudgetService budgetService,
        ClaimsPrincipal principal,
        CancellationToken ct)
    {
        var userId = GetUserId(principal);
        if (userId is null)
        {
            return Results.Unauthorized();
        }

        var deleted = await budgetService.DeleteBudgetAsync(userId.Value, id, ct);
        return deleted ? Results.NoContent() : Results.NotFound();
    }

    private static Guid? GetUserId(ClaimsPrincipal principal)
    {
        var subClaim = principal.FindFirst(JwtRegisteredClaimNames.Sub)?.Value;
        return subClaim is not null && Guid.TryParse(subClaim, out var userId) ? userId : null;
    }
}

/// <summary>JSON body shape for POST /api/budgets -- CategoryId null means the global monthly
/// cap. Public (not nested-private) so minimal API's JSON body binder can deserialize into it
/// without relying on reflection-only access to a private type.</summary>
public sealed record UpsertBudgetRequest(Guid? CategoryId, int Year, int Month, decimal LimitAmount);
